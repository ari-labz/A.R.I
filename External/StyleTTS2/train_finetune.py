# load packages
import random
import yaml
import time
from munch import Munch
import numpy as np
import torch
from torch import nn
import torch.nn.functional as F
import torchaudio
import librosa
import click
import shutil
import warnings
warnings.simplefilter('ignore')
from torch.utils.tensorboard import SummaryWriter

from meldataset import build_dataloader

from Utils.ASR.models import ASRCNN
from Utils.JDC.model import JDCNet
from Utils.PLBERT.util import load_plbert

from models import *
from losses import *
from utils import *

from Modules.slmadv import SLMAdversarialLoss
from Modules.diffusion.sampler import DiffusionSampler, ADPM2Sampler, KarrasSchedule

from optimizers import build_optimizer

# simple fix for dataparallel that allows access to class attributes
class MyDataParallel(torch.nn.DataParallel):
    def __getattr__(self, name):
        try:
            return super().__getattr__(name)
        except AttributeError:
            return getattr(self.module, name)
        
import logging
from logging import StreamHandler
logger = logging.getLogger(__name__)
logger.setLevel(logging.DEBUG)
handler = StreamHandler()
handler.setLevel(logging.DEBUG)
logger.addHandler(handler)


_FLOAT32_TINY = torch.finfo(torch.float32).tiny  # 1.175e-38 — smallest normal float32

def _clip_and_check(model, keys, max_norm=5.0):
    """Sanitize, clip, and validate gradients. Returns False only if clipping itself fails."""
    params = [p for k in keys for p in model[k].parameters() if p.grad is not None]
    if not params:
        return True
    # Zero NaN/inf gradients before clipping — MPS can produce them on backward passes.
    # Zeroing is safer than propagating: AdamW's second moment accumulates squared grads,
    # so a single NaN grad would permanently corrupt v for that parameter.
    _F32_MAX = torch.finfo(torch.float32).max
    for p in params:
        torch.nan_to_num_(p.grad, nan=_FLOAT32_TINY, posinf=_F32_MAX, neginf=-_F32_MAX)
    torch.nn.utils.clip_grad_norm_(params, max_norm=max_norm)
    return not any(torch.isnan(p.grad).any() or torch.isinf(p.grad).any() for p in params)


def _clamp_weight_norm_vectors(module):
    """Clamp weight_norm direction vectors to prevent ‖v‖→0 divide-by-zero on MPS.

    MPS flushes subnormal float32 to zero (FTZ mode). weight_norm computes
    g * v / ‖v‖ on every forward pass — if ‖v‖ == 0 the result is NaN.
    Rows whose norm has collapsed are reinitialised; individual subnormal elements
    are bumped to ±_FLOAT32_TINY (smallest normal float32 MPS can represent)
    with sign preserved so the direction vector stays valid.

    NaN elements are also fixed here — IEEE 754: NaN < x is always False, so the
    original subnormal check silently skipped NaN-contaminated rows. This caused
    the post-optimizer clamp to miss NaN weight_v values that the Adam step produced,
    leaving the healer to undo them on the NEXT step, creating an infinite loop.
    """
    from torch.nn.utils.weight_norm import WeightNorm
    for m in module.modules():
        for hook in getattr(m, '_forward_pre_hooks', {}).values():
            if isinstance(hook, WeightNorm):
                v = getattr(m, hook.name + '_v')
                g = getattr(m, hook.name + '_g')
                with torch.no_grad():
                    # Fix NaN/inf in weight_v elements before norm check.
                    if not torch.isfinite(v.data).all():
                        torch.nan_to_num_(v.data, nan=1e-4, posinf=1e-4, neginf=-1e-4)
                    flat = v.data.view(v.shape[0], -1)
                    norms = flat.norm(dim=1)
                    # NaN norms (from NaN elements) also need reinit — use ~isfinite.
                    bad_rows = (~torch.isfinite(norms) | (norms < _FLOAT32_TINY)).nonzero(as_tuple=False).squeeze(1)
                    if bad_rows.numel():
                        for i in bad_rows:
                            v.data[i].fill_(1e-4)
                    # Bump any subnormal or non-finite element to ±_FLOAT32_TINY.
                    too_small = (v.data.abs() < _FLOAT32_TINY) | ~torch.isfinite(v.data)
                    if too_small.any():
                        signs = v.data.sign()
                        signs[signs == 0] = 1.0
                        v.data[too_small] = signs[too_small] * _FLOAT32_TINY
                    # Also clamp weight_g — NaN/zero g produces NaN weight regardless of v.
                    if not torch.isfinite(g.data).all() or (g.data <= 0).any():
                        torch.nan_to_num_(g.data, nan=1.0, posinf=1.0, neginf=1.0)
                        g.data.clamp_(min=_FLOAT32_TINY)


def _assert_no_nan(model, epoch, step, log_file_path):
    """Heal NaN/inf params and zero weight_v norms in-place; log warnings but never crash.

    MPS can produce NaN or inf values in parameters after an optimizer step due to
    numerical instability (subnormal underflow, flush-to-zero, or gradient spikes).
    Raising here aborts training; healing allows it to recover from transient spikes.
    NaN params are replaced with 0 (neutral for most ops); inf params are clamped to
    ±float32.max; zero weight_v norms are re-initialised via _clamp_weight_norm_vectors.
    """
    from torch.nn.utils.weight_norm import WeightNorm as _WN
    _F32_MAX = torch.finfo(torch.float32).max
    healed = []
    for k in model:
        for p in model[k].parameters():
            if p.is_floating_point():
                bad = torch.isnan(p.data) | torch.isinf(p.data)
                if bad.any():
                    torch.nan_to_num_(p.data, nan=_FLOAT32_TINY, posinf=_F32_MAX, neginf=-_F32_MAX)
                    healed.append(f'{k}(NaN/inf param ×{int(bad.sum())})')
        for m in model[k].modules():
            for h in getattr(m, '_forward_pre_hooks', {}).values():
                if isinstance(h, _WN):
                    v = getattr(m, h.name + '_v')
                    norms = v.data.view(v.shape[0], -1).norm(dim=1)
                    if (~torch.isfinite(norms) | (norms < _FLOAT32_TINY)).any():
                        _clamp_weight_norm_vectors(model[k])
                        healed.append(f'{k}(zero weight_v norm in {type(m).__name__})')
                        break
    if healed:
        msg = f'Healed model state at epoch {epoch} step {step} — {healed}'
        logger.warning(msg)
        try:
            with open(log_file_path, 'a') as f:
                f.write(f'WARNING:{msg}\n')
        except Exception:
            pass


# All model keys that contain weight_norm layers (exhaustive — better to over-clamp)
_WEIGHT_NORM_KEYS = ['decoder', 'text_aligner', 'text_encoder', 'predictor',
                     'predictor_encoder', 'style_encoder', 'wd']
# msd and mpd are excluded — on MPS their weight_norm is removed at load time.


@click.command()
@click.option('-p', '--config_path', default='Configs/config_ft.yml', type=str)
def main(config_path):
    config = yaml.safe_load(open(config_path))
    
    log_dir = config['log_dir']
    if not osp.exists(log_dir): os.makedirs(log_dir, exist_ok=True)
    shutil.copy(config_path, osp.join(log_dir, osp.basename(config_path)))
    writer = SummaryWriter(log_dir + "/tensorboard")

    # write logs
    file_handler = logging.FileHandler(osp.join(log_dir, 'train.log'))
    file_handler.setLevel(logging.DEBUG)
    file_handler.setFormatter(logging.Formatter('%(levelname)s:%(asctime)s: %(message)s'))
    logger.addHandler(file_handler)

    # clean human-readable stats log — one line per log interval + one validation line per epoch
    _stats_log_path = osp.join(log_dir, 'Train.log')
    _stats_log = open(_stats_log_path, 'a')

    
    batch_size = config.get('batch_size', 10)

    epochs = config.get('epochs', 200)
    save_freq = config.get('save_freq', 2)
    log_interval = config.get('log_interval', 10)
    saving_epoch = config.get('save_freq', 2)

    data_params = config.get('data_params', None)
    sr = config['preprocess_params'].get('sr', 24000)
    train_path = data_params['train_data']
    val_path = data_params['val_data']
    root_path = data_params['root_path']
    min_length = data_params['min_length']
    OOD_data = data_params['OOD_data']

    max_len = config.get('max_len', 200)
    
    loss_params = Munch(config['loss_params'])
    diff_epoch = loss_params.diff_epoch
    joint_epoch = loss_params.joint_epoch
    
    optimizer_params = Munch(config['optimizer_params'])
    
    train_list, val_list = get_data_path_list(train_path, val_path)
    device = config.get('device', 'cuda')

    # MPS has poor float16 numerical stability — force float32 to prevent gradient explosion
    if device == 'mps':
        torch.set_default_dtype(torch.float32)

    train_dataloader = build_dataloader(train_list,
                                        root_path,
                                        OOD_data=OOD_data,
                                        min_length=min_length,
                                        batch_size=batch_size,
                                        num_workers=0,
                                        dataset_config={},
                                        device=device)

    val_dataloader = build_dataloader(val_list,
                                      root_path,
                                      OOD_data=OOD_data,
                                      min_length=min_length,
                                      batch_size=batch_size,
                                      validation=True,
                                      num_workers=0,
                                      device=device,
                                      dataset_config={})
    
    # load pretrained ASR model
    ASR_config = config.get('ASR_config', False)
    ASR_path = config.get('ASR_path', False)
    text_aligner = load_ASR_models(ASR_path, ASR_config)
    
    # load pretrained F0 model
    F0_path = config.get('F0_path', False)
    pitch_extractor = load_F0_models(F0_path)
    
    # load PL-BERT model
    BERT_path = config.get('PLBERT_dir', False)
    plbert = load_plbert(BERT_path)
    
    # build model
    model_params = recursive_munch(config['model_params'])
    multispeaker = model_params.multispeaker
    model = build_model(model_params, text_aligner, pitch_extractor, plbert)
    _ = [model[key].to(device) for key in model]

    # MPS: remove weight_norm from discriminators before training.
    # weight_norm splits weight into (weight_g, weight_v) and recomputes weight = g*v/||v||
    # on every forward pass. On MPS the Adam step drives weight_v toward NaN each iteration
    # because MPS's FTZ behaviour creates near-zero norms that the backward amplifies by 1/||v||.
    # remove_weight_norm fuses g and v back into a single weight tensor — mathematically identical
    # but without the reparameterisation, so NaN can no longer originate from the discriminators.
    if device == 'mps':
        def _remove_weight_norm_recursive(module):
            for m in module.modules():
                try:
                    torch.nn.utils.remove_weight_norm(m)
                except ValueError:
                    pass
        _remove_weight_norm_recursive(model.msd)
        _remove_weight_norm_recursive(model.mpd)
        logger.info("MPS: removed weight_norm from msd and mpd discriminators.")

    # DP
    for key in model:
        if key != "mpd" and key != "msd" and key != "wd":
            model[key] = MyDataParallel(model[key])
            
    start_epoch = 0
    iters = 0

    load_pretrained = config.get('pretrained_model', '') != '' and config.get('second_stage_load_pretrained', False)
    
    if not load_pretrained:
        if config.get('first_stage_path', '') != '':
            first_stage_path = osp.join(log_dir, config.get('first_stage_path', 'first_stage.pth'))
            print('Loading the first stage model at %s ...' % first_stage_path)
            model, _, start_epoch, iters = load_checkpoint(model, 
                None, 
                first_stage_path,
                load_only_params=True,
                ignore_modules=['bert', 'bert_encoder', 'predictor', 'predictor_encoder', 'msd', 'mpd', 'wd', 'diffusion']) # keep starting epoch for tensorboard log

            # these epochs should be counted from the start epoch
            diff_epoch += start_epoch
            joint_epoch += start_epoch
            epochs += start_epoch
            
            model.predictor_encoder = copy.deepcopy(model.style_encoder)
        else:
            raise ValueError('You need to specify the path to the first stage model.') 

    gl = GeneratorLoss(model.mpd, model.msd).to(device)
    dl = DiscriminatorLoss(model.mpd, model.msd).to(device)
    wl = WavLMLoss(model_params.slm.model, 
                   model.wd, 
                   sr, 
                   model_params.slm.sr).to(device)

    gl = MyDataParallel(gl)
    dl = MyDataParallel(dl)
    wl = MyDataParallel(wl)
    
    sampler = DiffusionSampler(
        model.diffusion.diffusion,
        sampler=ADPM2Sampler(),
        sigma_schedule=KarrasSchedule(sigma_min=0.0001, sigma_max=3.0, rho=9.0), # empirical parameters
        clamp=False
    )
    
    scheduler_params = {
        "max_lr": optimizer_params.lr,
        "pct_start": float(0),
        "epochs": epochs,
        "steps_per_epoch": len(train_dataloader),
    }
    scheduler_params_dict= {key: scheduler_params.copy() for key in model}
    scheduler_params_dict['bert']['max_lr'] = optimizer_params.bert_lr * 2
    scheduler_params_dict['decoder']['max_lr'] = optimizer_params.ft_lr * 2
    scheduler_params_dict['style_encoder']['max_lr'] = optimizer_params.ft_lr * 2
    
    optimizer = build_optimizer({key: model[key].parameters() for key in model},
                                          scheduler_params_dict=scheduler_params_dict, lr=optimizer_params.lr)
    
    # adjust BERT learning rate
    for g in optimizer.optimizers['bert'].param_groups:
        g['betas'] = (0.9, 0.99)
        g['lr'] = optimizer_params.bert_lr
        g['initial_lr'] = optimizer_params.bert_lr
        g['min_lr'] = 0
        g['weight_decay'] = 0.01
        
    # adjust acoustic module learning rate
    for module in ["decoder", "style_encoder"]:
        for g in optimizer.optimizers[module].param_groups:
            g['betas'] = (0.0, 0.99)
            g['lr'] = optimizer_params.ft_lr
            g['initial_lr'] = optimizer_params.ft_lr
            g['min_lr'] = 0
            g['weight_decay'] = 1e-4
        
    # load models if there is a model
    if load_pretrained:
        model, optimizer, start_epoch, iters = load_checkpoint(model,  optimizer, config['pretrained_model'],
                                    load_only_params=config.get('load_only_params', True))
        # Sanitize optimizer momentum buffers — a previous NaN-corrupted run poisons
        # exp_avg / exp_avg_sq which then propagates NaN into every subsequent step.
        for _opt in optimizer.optimizers.values():
            for _param_state in _opt.state.values():
                for _k, _v in _param_state.items():
                    if isinstance(_v, torch.Tensor) and _v.is_floating_point():
                        if torch.isnan(_v).any() or torch.isinf(_v).any():
                            torch.nan_to_num_(_v, nan=0.0, posinf=0.0, neginf=0.0)

    # Fix any zero-norm weight_v vectors inherited from the pretrained checkpoint.
    # weight_norm computes g * v / ‖v‖ — if ‖v‖ == 0 the result is NaN even
    # when no parameter contains NaN.  The per-step clamp only fires after the
    # first optimizer step, so pre-existing zeros would corrupt batch 1.
    for _wk in _WEIGHT_NORM_KEYS:
        if _wk in model:
            _fixed = []
            from torch.nn.utils.weight_norm import WeightNorm as _WN
            for _m in model[_wk].modules():
                for _h in list(getattr(_m, '_forward_pre_hooks', {}).values()):
                    if isinstance(_h, _WN):
                        _v = getattr(_m, _h.name + '_v')
                        _norms = _v.data.view(_v.shape[0], -1).norm(dim=1)
                        _bad = (_norms == 0) | (_norms < _FLOAT32_TINY)
                        if _bad.any():
                            with torch.no_grad():
                                for _i in _bad.nonzero(as_tuple=False).squeeze(1):
                                    _v.data[_i].fill_(1e-4)
                            _fixed.append(f'{type(_m).__name__}.{_h.name}_v[{_bad.sum().item()}]')
            if _fixed:
                logger.warning(f'Fixed zero weight_v norms in {_wk}: {_fixed}')
        
    n_down = model.text_aligner.n_down

    best_loss = float('inf')  # best test loss
    loss_train_record = list([])
    loss_test_record = list([])
    iters = 0
    
    criterion = nn.L1Loss() # F0 loss (regression)
    torch.cuda.empty_cache()
    
    stft_loss = MultiResolutionSTFTLoss().to(device)
    
    print('BERT', optimizer.optimizers['bert'])
    print('decoder', optimizer.optimizers['decoder'])

    start_ds = False
    
    running_std = []
    
    slmadv_params = Munch(config['slmadv_params'])
    slmadv = SLMAdversarialLoss(model, wl, sampler, 
                                slmadv_params.min_len, 
                                slmadv_params.max_len,
                                batch_percentage=slmadv_params.batch_percentage,
                                skip_update=slmadv_params.iter, 
                                sig=slmadv_params.sig
                               )
    
    
    _train_log_path = osp.join(log_dir, 'train.log')

    for epoch in range(start_epoch, epochs):
        running_loss = 0
        start_time = time.time()

        _ = [model[key].eval() for key in model]

        model.text_aligner.train()
        model.text_encoder.train()
        
        model.predictor.train()
        model.bert_encoder.train()
        model.bert.train()
        model.msd.train()
        model.mpd.train()

        for i, batch in enumerate(train_dataloader):
            # Clamp weight_norm vectors before every forward pass. The post-step clamp
            # catches damage done by the optimizer; this one prevents NaN from entering
            # the computation graph in the first place when a norm is near zero.
            for _wk in _WEIGHT_NORM_KEYS:
                if _wk in model:
                    _clamp_weight_norm_vectors(model[_wk])
            waves = batch[0]
            batch = [b.to(device) for b in batch[1:]]
            texts, input_lengths, ref_texts, ref_lengths, mels, mel_input_length, ref_mels = batch
            with torch.no_grad():
                mask = length_to_mask(mel_input_length // (2 ** n_down)).to(device)
                mel_mask = length_to_mask(mel_input_length).to(device)
                text_mask = length_to_mask(input_lengths).to(texts.device)

                # compute reference styles
                if multispeaker and epoch >= diff_epoch:
                    ref_ss = model.style_encoder(ref_mels.unsqueeze(1))
                    ref_sp = model.predictor_encoder(ref_mels.unsqueeze(1))
                    ref = torch.cat([ref_ss, ref_sp], dim=1)
                
            try:
                ppgs, s2s_pred, s2s_attn = model.text_aligner(mels, mask, texts)
                s2s_attn = s2s_attn.transpose(-1, -2)
                s2s_attn = s2s_attn[..., 1:]
                s2s_attn = s2s_attn.transpose(-1, -2)
            except:
                continue

            mask_ST = mask_from_lens(s2s_attn, input_lengths, mel_input_length // (2 ** n_down))
            s2s_attn_mono = maximum_path(s2s_attn, mask_ST)

            # encode
            t_en = model.text_encoder(texts, input_lengths, text_mask)
            
            # 50% of chance of using monotonic version
            if bool(random.getrandbits(1)):
                asr = (t_en @ s2s_attn)
            else:
                asr = (t_en @ s2s_attn_mono)

            d_gt = s2s_attn_mono.sum(axis=-1).detach()

            # compute the style of the entire utterance
            # this operation cannot be done in batch because of the avgpool layer (may need to work on masked avgpool)
            ss = []
            gs = []
            for bib in range(len(mel_input_length)):
                mel_length = int(mel_input_length[bib].item())
                mel = mels[bib, :, :mel_input_length[bib]]
                s = model.predictor_encoder(mel.unsqueeze(0).unsqueeze(1))
                ss.append(s)
                s = model.style_encoder(mel.unsqueeze(0).unsqueeze(1))
                gs.append(s)

            s_dur = torch.stack(ss).squeeze()  # global prosodic styles
            gs = torch.stack(gs).squeeze() # global acoustic styles
            s_trg = torch.cat([gs, s_dur], dim=-1).detach() # ground truth for denoiser

            bert_dur = model.bert(texts, attention_mask=(~text_mask).int())
            d_en = model.bert_encoder(bert_dur).transpose(-1, -2) 
            
            # denoiser training
            if epoch >= diff_epoch:
                num_steps = np.random.randint(3, 5)
                
                if model_params.diffusion.dist.estimate_sigma_data:
                    model.diffusion.module.diffusion.sigma_data = s_trg.std(axis=-1).mean().item() # batch-wise std estimation
                    running_std.append(model.diffusion.module.diffusion.sigma_data)
                    
                if multispeaker:
                    s_preds = sampler(noise = torch.randn_like(s_trg).unsqueeze(1).to(device), 
                          embedding=bert_dur,
                          embedding_scale=1,
                                   features=ref, # reference from the same speaker as the embedding
                             embedding_mask_proba=0.1,
                             num_steps=num_steps).squeeze(1)
                    loss_diff = model.diffusion(s_trg.unsqueeze(1), embedding=bert_dur, features=ref).mean() # EDM loss
                    loss_sty = F.l1_loss(s_preds, s_trg.detach()) # style reconstruction loss
                else:
                    s_preds = sampler(noise = torch.randn_like(s_trg).unsqueeze(1).to(device), 
                          embedding=bert_dur,
                          embedding_scale=1,
                             embedding_mask_proba=0.1,
                             num_steps=num_steps).squeeze(1)                    
                    loss_diff = model.diffusion.module.diffusion(s_trg.unsqueeze(1), embedding=bert_dur).mean() # EDM loss
                    loss_sty = F.l1_loss(s_preds, s_trg.detach()) # style reconstruction loss
            else:
                loss_sty = 0
                loss_diff = 0

                
            s_loss = 0
            

            d, p = model.predictor(d_en, s_dur, 
                                                    input_lengths, 
                                                    s2s_attn_mono, 
                                                    text_mask)
                
            mel_len_st = int(mel_input_length.min().item() / 2 - 1)
            mel_len = min(int(mel_input_length.min().item() / 2 - 1), max_len // 2)
            en = []
            gt = []
            p_en = []
            wav = []
            st = []
            
            for bib in range(len(mel_input_length)):
                mel_length = int(mel_input_length[bib].item() / 2)

                random_start = np.random.randint(0, mel_length - mel_len)
                en.append(asr[bib, :, random_start:random_start+mel_len])
                p_en.append(p[bib, :, random_start:random_start+mel_len])
                gt.append(mels[bib, :, (random_start * 2):((random_start+mel_len) * 2)])
                
                y = waves[bib][(random_start * 2) * 300:((random_start+mel_len) * 2) * 300]
                wav.append(torch.from_numpy(y).float().to(device))
                
                # style reference (better to be different from the GT)
                random_start = np.random.randint(0, mel_length - mel_len_st)
                st.append(mels[bib, :, (random_start * 2):((random_start+mel_len_st) * 2)])
                
            wav = torch.stack(wav).float().detach()

            en = torch.stack(en)
            p_en = torch.stack(p_en)
            gt = torch.stack(gt).detach()
            st = torch.stack(st).detach()
            
            
            if gt.size(-1) < 80:
                continue
            
            s = model.style_encoder(gt.unsqueeze(1))           
            s_dur = model.predictor_encoder(gt.unsqueeze(1))
                
            with torch.no_grad():
                F0_real, _, F0 = model.pitch_extractor(gt.unsqueeze(1))
                F0 = F0.reshape(F0.shape[0], F0.shape[1] * 2, F0.shape[2], 1).squeeze()

                N_real = log_norm(gt.unsqueeze(1)).squeeze(1)
                
                y_rec_gt = wav.unsqueeze(1)
                y_rec_gt_pred = model.decoder(en, F0_real, N_real, s)

                wav = y_rec_gt

            F0_fake, N_fake = model.predictor.F0Ntrain(p_en, s_dur)

            y_rec = model.decoder(en, F0_fake, N_fake, s)

            loss_F0_rec =  (F.smooth_l1_loss(F0_real, F0_fake)) / 10
            loss_norm_rec = F.smooth_l1_loss(N_real, N_fake)

            optimizer.zero_grad()
            d_loss = dl(wav.detach(), y_rec.detach()).mean()
            d_loss.backward()
            if _clip_and_check(model, ['msd', 'mpd']):
                optimizer.step('msd')
                optimizer.step('mpd')
                for _wk in ['msd', 'mpd']:
                    _clamp_weight_norm_vectors(model[_wk])

            # generator loss
            optimizer.zero_grad()

            loss_mel = stft_loss(y_rec, wav)
            loss_gen_all = gl(wav, y_rec).mean()
            loss_lm = wl(wav.detach().squeeze(), y_rec.squeeze()).mean()

            loss_ce = 0
            loss_dur = 0
            for _s2s_pred, _text_input, _text_length in zip(d, (d_gt), input_lengths):
                _s2s_pred = _s2s_pred[:_text_length, :]
                _text_input = _text_input[:_text_length].long()
                _s2s_trg = torch.zeros_like(_s2s_pred)
                for p in range(_s2s_trg.shape[0]):
                    _s2s_trg[p, :_text_input[p]] = 1
                _dur_pred = torch.sigmoid(_s2s_pred).sum(axis=1)

                loss_dur += F.l1_loss(_dur_pred[1:_text_length-1], 
                                       _text_input[1:_text_length-1])
                loss_ce += F.binary_cross_entropy_with_logits(_s2s_pred.flatten(), _s2s_trg.flatten())

            loss_ce /= texts.size(0)
            loss_dur /= texts.size(0)
            
            loss_s2s = 0
            for _s2s_pred, _text_input, _text_length in zip(s2s_pred, texts, input_lengths):
                loss_s2s += F.cross_entropy(_s2s_pred[:_text_length], _text_input[:_text_length])
            loss_s2s /= texts.size(0)

            loss_mono = F.l1_loss(s2s_attn, s2s_attn_mono) * 10

            def _safe(t):
                # Drop any loss term that went NaN/inf — better to train without it
                # than to propagate NaN through the backward pass and corrupt params.
                if not isinstance(t, torch.Tensor):
                    return t
                return t if torch.isfinite(t) else torch.zeros_like(t)

            g_loss = loss_params.lambda_mel  * _safe(loss_mel)      + \
                     loss_params.lambda_F0   * _safe(loss_F0_rec)   + \
                     loss_params.lambda_ce   * _safe(loss_ce)       + \
                     loss_params.lambda_norm * _safe(loss_norm_rec) + \
                     loss_params.lambda_dur  * _safe(loss_dur)      + \
                     loss_params.lambda_gen  * _safe(loss_gen_all)  + \
                     loss_params.lambda_slm  * _safe(loss_lm)       + \
                     loss_params.lambda_sty  * _safe(loss_sty)      + \
                     loss_params.lambda_diff * _safe(loss_diff)     + \
                     loss_params.lambda_mono * _safe(loss_mono)     + \
                     loss_params.lambda_s2s  * _safe(loss_s2s)
            
            running_loss += loss_mel.item()
            if torch.isnan(g_loss):
                optimizer.zero_grad()
                continue

            g_loss.backward()
            _gen_keys = ['bert_encoder', 'bert', 'predictor', 'predictor_encoder',
                         'style_encoder', 'decoder', 'text_encoder', 'text_aligner']
            if epoch >= diff_epoch:
                _gen_keys.append('diffusion')
            if _clip_and_check(model, _gen_keys):
                optimizer.step('bert_encoder')
                optimizer.step('bert')
                optimizer.step('predictor')
                optimizer.step('predictor_encoder')
                optimizer.step('style_encoder')
                optimizer.step('decoder')
                optimizer.step('text_encoder')
                optimizer.step('text_aligner')
                if epoch >= diff_epoch:
                    optimizer.step('diffusion')
                # Clamp weight_norm direction vectors to float32 minimum to prevent ‖v‖→0
                for _wk in _WEIGHT_NORM_KEYS:
                    _clamp_weight_norm_vectors(model[_wk])
                _assert_no_nan(model, epoch + 1, iters, _train_log_path)

            d_loss_slm, loss_gen_lm = 0, 0
            if epoch >= joint_epoch:
                # randomly pick whether to use in-distribution text
                if np.random.rand() < 0.5:
                    use_ind = True
                else:
                    use_ind = False

                if use_ind:
                    ref_lengths = input_lengths
                    ref_texts = texts
                    
                slm_out = slmadv(i, 
                                 y_rec_gt, 
                                 y_rec_gt_pred, 
                                 waves, 
                                 mel_input_length,
                                 ref_texts, 
                                 ref_lengths, use_ind, s_trg.detach(), ref if multispeaker else None)

                if slm_out is not None:
                    d_loss_slm, loss_gen_lm, y_pred = slm_out

                    # SLM generator loss
                    optimizer.zero_grad()
                    loss_gen_lm.backward()

                    # compute the gradient norm
                    total_norm = {}
                    for key in model.keys():
                        total_norm[key] = 0
                        parameters = [p for p in model[key].parameters() if p.grad is not None and p.requires_grad]
                        for p in parameters:
                            param_norm = p.grad.detach().data.norm(2)
                            total_norm[key] += param_norm.item() ** 2
                        total_norm[key] = total_norm[key] ** 0.5

                    # gradient scaling
                    if total_norm['predictor'] > slmadv_params.thresh:
                        for key in model.keys():
                            for p in model[key].parameters():
                                if p.grad is not None:
                                    p.grad *= (1 / total_norm['predictor'])

                    for p in model.predictor.duration_proj.parameters():
                        if p.grad is not None:
                            p.grad *= slmadv_params.scale

                    for p in model.predictor.lstm.parameters():
                        if p.grad is not None:
                            p.grad *= slmadv_params.scale

                    for p in model.diffusion.parameters():
                        if p.grad is not None:
                            p.grad *= slmadv_params.scale
                    
                    if _clip_and_check(model, ['bert_encoder', 'bert', 'predictor', 'diffusion']):
                        optimizer.step('bert_encoder')
                        optimizer.step('bert')
                        optimizer.step('predictor')
                        optimizer.step('diffusion')
                        for _wk in ['predictor']:
                            _clamp_weight_norm_vectors(model[_wk])

                    # SLM discriminator loss
                    if d_loss_slm != 0:
                        optimizer.zero_grad()
                        d_loss_slm.backward(retain_graph=True)
                        if _clip_and_check(model, ['wd']):
                            optimizer.step('wd')
                            _clamp_weight_norm_vectors(model['wd'])

            iters = iters + 1
            
            if (i+1)%log_interval == 0:
                _stat_line = ('Epoch [%d/%d], Step [%d/%d], Loss: %.5f, Disc Loss: %.5f, Dur Loss: %.5f, CE Loss: %.5f, Norm Loss: %.5f, F0 Loss: %.5f, LM Loss: %.5f, Gen Loss: %.5f, Sty Loss: %.5f, Diff Loss: %.5f, DiscLM Loss: %.5f, GenLM Loss: %.5f, SLoss: %.5f, S2S Loss: %.5f, Mono Loss: %.5f'
                    %(epoch+1, epochs, i+1, len(train_list)//batch_size, running_loss / log_interval, d_loss, loss_dur, loss_ce, loss_norm_rec, loss_F0_rec, loss_lm, loss_gen_all, loss_sty, loss_diff, d_loss_slm, loss_gen_lm, s_loss, loss_s2s, loss_mono))
                logger.info(_stat_line)
                _stats_log.write(_stat_line + '\n')
                _stats_log.flush()
                
                writer.add_scalar('train/mel_loss', running_loss / log_interval, iters)
                writer.add_scalar('train/gen_loss', loss_gen_all, iters)
                writer.add_scalar('train/d_loss', d_loss, iters)
                writer.add_scalar('train/ce_loss', loss_ce, iters)
                writer.add_scalar('train/dur_loss', loss_dur, iters)
                writer.add_scalar('train/slm_loss', loss_lm, iters)
                writer.add_scalar('train/norm_loss', loss_norm_rec, iters)
                writer.add_scalar('train/F0_loss', loss_F0_rec, iters)
                writer.add_scalar('train/sty_loss', loss_sty, iters)
                writer.add_scalar('train/diff_loss', loss_diff, iters)
                writer.add_scalar('train/d_loss_slm', d_loss_slm, iters)
                writer.add_scalar('train/gen_loss_slm', loss_gen_lm, iters)
                
                running_loss = 0
                
                print('Time elasped:', time.time()-start_time)
            
        loss_test = 0
        loss_align = 0
        loss_f = 0
        _ = [model[key].eval() for key in model]

        with torch.no_grad():
            iters_test = 0
            for batch_idx, batch in enumerate(val_dataloader):
                optimizer.zero_grad()

                try:
                    waves = batch[0]
                    batch = [b.to(device) for b in batch[1:]]
                    texts, input_lengths, ref_texts, ref_lengths, mels, mel_input_length, ref_mels = batch
                    with torch.no_grad():
                        mask = length_to_mask(mel_input_length // (2 ** n_down)).to(device)
                        text_mask = length_to_mask(input_lengths).to(texts.device)

                        _, _, s2s_attn = model.text_aligner(mels, mask, texts)
                        s2s_attn = s2s_attn.transpose(-1, -2)
                        s2s_attn = s2s_attn[..., 1:]
                        s2s_attn = s2s_attn.transpose(-1, -2)

                        mask_ST = mask_from_lens(s2s_attn, input_lengths, mel_input_length // (2 ** n_down))
                        s2s_attn_mono = maximum_path(s2s_attn, mask_ST)

                        # encode
                        t_en = model.text_encoder(texts, input_lengths, text_mask)
                        asr = (t_en @ s2s_attn_mono)

                        d_gt = s2s_attn_mono.sum(axis=-1).detach()

                    ss = []
                    gs = []

                    for bib in range(len(mel_input_length)):
                        mel_length = int(mel_input_length[bib].item())
                        mel = mels[bib, :, :mel_input_length[bib]]
                        s = model.predictor_encoder(mel.unsqueeze(0).unsqueeze(1))
                        ss.append(s)
                        s = model.style_encoder(mel.unsqueeze(0).unsqueeze(1))
                        gs.append(s)

                    s = torch.stack(ss).squeeze()
                    gs = torch.stack(gs).squeeze()
                    s_trg = torch.cat([s, gs], dim=-1).detach()

                    bert_dur = model.bert(texts, attention_mask=(~text_mask).int())
                    d_en = model.bert_encoder(bert_dur).transpose(-1, -2) 
                    d, p = model.predictor(d_en, s, 
                                                        input_lengths, 
                                                        s2s_attn_mono, 
                                                        text_mask)
                    # get clips
                    mel_len = int(mel_input_length.min().item() / 2 - 1)
                    en = []
                    gt = []

                    p_en = []
                    wav = []

                    for bib in range(len(mel_input_length)):
                        mel_length = int(mel_input_length[bib].item() / 2)

                        random_start = np.random.randint(0, mel_length - mel_len)
                        en.append(asr[bib, :, random_start:random_start+mel_len])
                        p_en.append(p[bib, :, random_start:random_start+mel_len])

                        gt.append(mels[bib, :, (random_start * 2):((random_start+mel_len) * 2)])
                        y = waves[bib][(random_start * 2) * 300:((random_start+mel_len) * 2) * 300]
                        wav.append(torch.from_numpy(y).float().to(device))

                    wav = torch.stack(wav).float().detach()

                    en = torch.stack(en)
                    p_en = torch.stack(p_en)
                    gt = torch.stack(gt).detach()
                    s = model.predictor_encoder(gt.unsqueeze(1))

                    F0_fake, N_fake = model.predictor.F0Ntrain(p_en, s)

                    loss_dur = 0
                    for _s2s_pred, _text_input, _text_length in zip(d, (d_gt), input_lengths):
                        _s2s_pred = _s2s_pred[:_text_length, :]
                        _text_input = _text_input[:_text_length].long()
                        _s2s_trg = torch.zeros_like(_s2s_pred)
                        for bib in range(_s2s_trg.shape[0]):
                            _s2s_trg[bib, :_text_input[bib]] = 1
                        _dur_pred = torch.sigmoid(_s2s_pred).sum(axis=1)
                        loss_dur += F.l1_loss(_dur_pred[1:_text_length-1], 
                                               _text_input[1:_text_length-1])

                    loss_dur /= texts.size(0)

                    s = model.style_encoder(gt.unsqueeze(1))

                    y_rec = model.decoder(en, F0_fake, N_fake, s)
                    loss_mel = stft_loss(y_rec.squeeze(), wav.detach())

                    F0_real, _, F0 = model.pitch_extractor(gt.unsqueeze(1)) 

                    loss_F0 = F.l1_loss(F0_real, F0_fake) / 10

                    loss_test += (loss_mel).mean()
                    loss_align += (loss_dur).mean()
                    loss_f += (loss_F0).mean()

                    iters_test += 1
                except:
                    continue

        print('Epochs:', epoch + 1, flush=True)
        if iters_test > 0:
            _val_line = 'Validation loss: %.3f, Dur loss: %.3f, F0 loss: %.3f' % (loss_test / iters_test, loss_align / iters_test, loss_f / iters_test)
            logger.info(_val_line + '\n\n\n')
            _stats_log.write(_val_line + '\n')
            _stats_log.flush()
            writer.add_scalar('eval/mel_loss', loss_test / iters_test, epoch + 1)
            writer.add_scalar('eval/dur_loss', loss_test / iters_test, epoch + 1)
            writer.add_scalar('eval/F0_loss', loss_f / iters_test, epoch + 1)
        print('\n\n\n')
        
        
        _stop_requested = osp.exists(osp.join(log_dir, '.stop_training'))
        if (epoch + 1) % save_freq == 0 or _stop_requested:
            val_loss = (loss_test / iters_test) if iters_test > 0 else float('inf')
            if val_loss < best_loss:
                best_loss = val_loss
            print('Saving..')
            state = {
                'net':  {key: model[key].state_dict() for key in model},
                'optimizer': optimizer.state_dict(),
                'iters': iters,
                'val_loss': val_loss,
                'epoch': epoch,
            }
            save_path = osp.join(log_dir, 'epoch_2nd_%05d.pth' % epoch)
            torch.save(state, save_path)

            # if estimate sigma, save the estimated simga
            if model_params.diffusion.dist.estimate_sigma_data:
                config['model_params']['diffusion']['dist']['sigma_data'] = float(np.mean(running_std))

                with open(osp.join(log_dir, osp.basename(config_path)), 'w') as outfile:
                    yaml.dump(config, outfile, default_flow_style=True)

            if _stop_requested:
                try: os.remove(osp.join(log_dir, '.stop_training'))
                except: pass
                logger.info("Pause requested — checkpoint saved at epoch %d, exiting cleanly." % (epoch + 1))
                print('Paused:', epoch + 1, flush=True)
                break

    _stats_log.close()


if __name__=="__main__":
    main()
