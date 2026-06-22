using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public class StyleTtsSynthesiser(string styleTtsPath, string modelPath, string configPath, string refAudioPath, ILogger? logger = null) : IDisposable
{
    private const string SERVER_SCRIPT        = "serve.py";
    private const int    SERVER_PORT          = 8020;
    private const int    POLL_INTERVAL_MS     = 2000;
    private const int    STARTUP_TIMEOUT_SECS = 120;

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Process? server;

    public async Task Start(CancellationToken ct = default)
    {
        string python = Path.Combine(styleTtsPath, "venv", "bin", "python");
        string script = Path.Combine(styleTtsPath, SERVER_SCRIPT);

        KillPortOwner(SERVER_PORT);
        WriteServerScript(script);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{script}\" --model \"{modelPath}\" --config \"{configPath}\" --ref_audio \"{refAudioPath}\" --port {SERVER_PORT}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        server = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start StyleTTS2 inference server.");

        _ = Task.Run(() => StreamErrors(server.StandardError), ct);
        _ = Task.Run(() => DrainOutput(server.StandardOutput), ct);

        await WaitUntilReady(ct);
    }

    public async Task Warmup(CancellationToken ct = default)
    {
        await Speak("Voice synthesis is ready.", ct);
    }

    public async Task<byte[]> Speak(string text, CancellationToken ct = default)
    {
        string url     = $"http://localhost:{SERVER_PORT}/synthesise";
        string payload = JsonSerializer.Serialize(new { text });

        using StringContent body = new(payload, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, body, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose()
    {
        http.Dispose();
        try { server?.Kill(entireProcessTree: true); } catch { }
        server?.Dispose();
    }

    private static void KillPortOwner(int port)
    {
        try
        {
            ProcessStartInfo info = new()
            {
                FileName               = "bash",
                Arguments              = $"-c \"lsof -ti:{port} | xargs kill -9 2>/dev/null; true\"",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            Process.Start(info)?.WaitForExit(3000);
        }
        catch { }
    }

    private async Task WaitUntilReady(CancellationToken ct)
    {
        string healthUrl   = $"http://localhost:{SERVER_PORT}/health";
        int    elapsedSecs = 0;

        while (elapsedSecs < STARTUP_TIMEOUT_SECS)
        {
            await Task.Delay(POLL_INTERVAL_MS, ct);
            elapsedSecs += POLL_INTERVAL_MS / 1000;

            try
            {
                HttpResponseMessage resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not ready yet */ }

            if (server?.HasExited == true)
                throw new InvalidOperationException($"StyleTTS2 server exited unexpectedly (code {server.ExitCode}).");
        }

        throw new TimeoutException($"StyleTTS2 server did not become ready within {STARTUP_TIMEOUT_SECS}s.");
    }

    private async Task StreamErrors(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("HTTP/1.1")) continue;
            logger?.LogWarning("[StyleTTS2] {Line}", line);
        }
    }

    private static async Task DrainOutput(StreamReader reader)
    {
        while (await reader.ReadLineAsync() != null) { }
    }

    private static void WriteServerScript(string path)
    {
        string script = """
import argparse, io, logging, sys, os
import numpy as np
import soundfile as sf
from flask import Flask, request, Response, jsonify

logging.getLogger('werkzeug').setLevel(logging.ERROR)

parser = argparse.ArgumentParser()
parser.add_argument('--model',     required=True)
parser.add_argument('--config',    required=True)
parser.add_argument('--ref_audio', required=True)
parser.add_argument('--port',      type=int, default=8020)
args = parser.parse_args()

# Run from the StyleTTS2 repo root so local imports resolve
repo_root = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, repo_root)
os.chdir(repo_root)

# Exit when the parent process (ARI) dies, even on SIGKILL.
import threading, time as _time
def _watch_parent(ppid=os.getppid()):
    while True:
        _time.sleep(2)
        try:
            os.kill(ppid, 0)
        except ProcessLookupError:
            os._exit(0)
threading.Thread(target=_watch_parent, daemon=True).start()

import torch
_orig_load = torch.load
torch.load = lambda *a, **kw: _orig_load(*a, **{**kw, 'weights_only': False})
import yaml
import librosa
import torchaudio
from munch import Munch
from models import *
from utils import *
from Modules.diffusion.sampler import DiffusionSampler, ADPM2Sampler, KarrasSchedule
from Utils.PLBERT.util import load_plbert

if torch.cuda.is_available():
    device = 'cuda'
elif torch.backends.mps.is_available():
    device = 'mps'
else:
    device = 'cpu'
config = yaml.safe_load(open(args.config))

# Load utility models (paths relative to repo root)
text_aligner    = load_ASR_models(config['ASR_path'], config['ASR_config'])
pitch_extractor = load_F0_models(config['F0_path'])
plbert          = load_plbert(config['PLBERT_dir'])

model_params = recursive_munch(config['model_params'])
model = build_model(model_params, text_aligner, pitch_extractor, plbert)

# Drop training-only components NOW — before moving anything to device — so they
# never occupy MPS memory. Also clear the local refs so Python can GC them.
# Saves ~700MB: WavLM discriminator (~350MB), ASR aligner (~150MB), mpd/msd, JDC.
_TRAINING_ONLY = {'text_aligner', 'pitch_extractor', 'mpd', 'msd', 'wd'}
for _k in _TRAINING_ONLY:
    if _k in model: del model[_k]
del text_aligner, pitch_extractor  # drop local refs so GC can reclaim them

_ = [model[k].eval().to(device) for k in model]

ckpt = torch.load(args.model, map_location='cpu')
params = ckpt['net'] if 'net' in ckpt else ckpt
for k in model:
    if k in params:
        try:
            model[k].load_state_dict(params[k])
        except Exception:
            from collections import OrderedDict
            sd = OrderedDict((n[7:], v) for n, v in params[k].items())
            model[k].load_state_dict(sd, strict=False)

# Free the 2GB checkpoint dict — it's fully consumed into model params now.
import gc as _gc
del ckpt, params
_gc.collect()
if torch.backends.mps.is_available():
    torch.mps.empty_cache()

# Validate checkpoint — fail loudly if any component has NaN weights.
import sys as _sys
_nan_components = [_k for _k in model
                   if any(_p.is_floating_point() and torch.isnan(_p).any()
                          for _p in model[_k].parameters())]
if _nan_components:
    _msg = f'[serve] CORRUPT CHECKPOINT: NaN weights in {_nan_components} — retrain or use an earlier checkpoint'
    _sys.stderr.write(_msg + '\n'); _sys.stderr.flush()
    _sys.exit(1)

# Fix zero-norm weight_v vectors. weight_norm computes g * v / ||v||; if ||v|| == 0
# the result is NaN even when no stored parameter contains NaN. Pretrained checkpoints
# can ship with zero-norm rows (LibriTTS base had 83 in decode[3].pool), and the
# training clamp only fires after optimizer steps so they survive into inference.
_FLOAT32_TINY = torch.finfo(torch.float32).tiny
from torch.nn.utils.weight_norm import WeightNorm as _WeightNorm

def _fix_weight_norm_zeros(module):
    fixed = []
    for m in module.modules():
        for hook in list(getattr(m, '_forward_pre_hooks', {}).values()):
            if isinstance(hook, _WeightNorm):
                v = getattr(m, hook.name + '_v')
                norms = v.data.view(v.shape[0], -1).norm(dim=1)
                bad = (norms == 0) | (norms < _FLOAT32_TINY)
                if bad.any():
                    with torch.no_grad():
                        for idx in bad.nonzero(as_tuple=False).squeeze(1):
                            v.data[idx].fill_(1e-4)
                    fixed.append(f'{type(m).__name__}.{hook.name}_v[{bad.sum().item()}]')
    return fixed

for _k in model:
    _fixed = _fix_weight_norm_zeros(model[_k])
    if _fixed:
        _sys.stderr.write(f'[serve] Fixed zero weight_v norms in {_k}: {_fixed}\n')
        _sys.stderr.flush()

sampler = DiffusionSampler(
    model.diffusion.diffusion,
    sampler=ADPM2Sampler(),
    sigma_schedule=KarrasSchedule(sigma_min=0.0001, sigma_max=3.0, rho=9.0),
    clamp=False,
)

# Pre-compute style vector from reference audio once at startup
to_mel = torchaudio.transforms.MelSpectrogram(n_mels=80, n_fft=2048, win_length=1200, hop_length=300)
mean, std = -4, 4

def compute_style(path):
    wave, sr = librosa.load(path, sr=24000)
    audio, _ = librosa.effects.trim(wave, top_db=30)
    mel = to_mel(torch.from_numpy(audio).float())
    mel = (torch.log(1e-5 + mel.unsqueeze(0)) - mean) / std
    with torch.no_grad():
        ref_s = model.style_encoder(mel.unsqueeze(1).to(device))
        ref_p = model.predictor_encoder(mel.unsqueeze(1).to(device))
    return torch.cat([ref_s, ref_p], dim=1)

ref_s = compute_style(args.ref_audio)

from text_utils import TextCleaner
import gruut

textcleaner = TextCleaner()

def phonemize(text):
    words = []
    for sentence in gruut.sentences(text, lang='en-us'):
        for word in sentence:
            if hasattr(word, 'phonemes') and word.phonemes:
                words.append(''.join(word.phonemes))
    return ' '.join(words)

def length_to_mask(lengths):
    mask = torch.arange(lengths.max()).unsqueeze(0).expand(lengths.shape[0], -1).type_as(lengths)
    return torch.gt(mask + 1, lengths.unsqueeze(1))

def synthesise_text(text, alpha=0.3, beta=0.7, diffusion_steps=5, embedding_scale=1.0):
    text = text.strip().replace('"', '')
    tokens = textcleaner(phonemize(text))
    tokens.insert(0, 0)
    tokens = torch.LongTensor(tokens).to(device).unsqueeze(0)

    with torch.no_grad():
        input_lengths = torch.LongTensor([tokens.shape[-1]]).to(device)
        text_mask = length_to_mask(input_lengths).to(device)

        t_en = model.text_encoder(tokens, input_lengths, text_mask)
        bert_dur = model.bert(tokens, attention_mask=(~text_mask).int())
        d_en = model.bert_encoder(bert_dur).transpose(-1, -2)

        import hashlib as _hl
        _seed = int(_hl.md5(text.encode()).hexdigest()[:8], 16) % (2**31)
        torch.manual_seed(_seed)
        s_pred = sampler(
            noise=torch.randn((1, 256)).unsqueeze(1).to(device),
            embedding=bert_dur, embedding_scale=embedding_scale,
            features=ref_s, num_steps=diffusion_steps,
        ).squeeze(1)

        s   = beta  * s_pred[:, 128:] + (1 - beta)  * ref_s[:, 128:]
        ref = alpha * s_pred[:, :128] + (1 - alpha) * ref_s[:, :128]

        d = model.predictor.text_encoder(d_en, s, input_lengths, text_mask)
        x, _ = model.predictor.lstm(d)
        duration = torch.sigmoid(model.predictor.duration_proj(x)).sum(axis=-1)
        pred_dur = torch.nan_to_num(torch.round(duration.squeeze()), nan=1.0).clamp(min=1, max=10)

        pred_aln = torch.zeros(input_lengths, int(pred_dur.sum().data))
        c = 0
        for i in range(pred_aln.size(0)):
            pred_aln[i, c:c + int(pred_dur[i].data)] = 1
            c += int(pred_dur[i].data)

        en = d.transpose(-1, -2) @ pred_aln.unsqueeze(0).to(device)
        F0_pred, N_pred = model.predictor.F0Ntrain(en, s)
        asr = t_en @ pred_aln.unsqueeze(0).to(device)
        out = model.decoder(asr, F0_pred, N_pred, ref.squeeze().unsqueeze(0))

    return out.squeeze().cpu().numpy()[..., :-50]

app = Flask(__name__)

@app.errorhandler(500)
def handle_500(e):
    return jsonify({'error': str(e.description)}), 500

@app.route('/health')
def health():
    return jsonify({'status': 'ok'})

@app.route('/synthesise', methods=['POST'])
def synthesise():
    text = request.json['text']
    import sys as _sys
    _sys.stderr.write(f'[synthesise] text={repr(text)}\n'); _sys.stderr.flush()
    wav = synthesise_text(text)
    _raw_peak = np.abs(wav).max()
    _raw_nans = int(np.isnan(wav).sum())
    _sys.stderr.write(f'[synthesise] raw shape={wav.shape} peak={_raw_peak:.4f} nans={_raw_nans}\n'); _sys.stderr.flush()
    if _raw_nans > 1:
        _sys.stderr.write(f'[synthesise] ERROR: {_raw_nans} NaN samples in output — model weights may be corrupted\n'); _sys.stderr.flush()
        from flask import abort
        abort(500, description=f'NaN audio output: {_raw_nans} NaN samples. Model weights may be corrupted or undertrained.')
    wav = np.nan_to_num(wav, nan=0.0, posinf=0.0, neginf=0.0)
    peak = np.abs(wav).max()
    if peak > 0:
        wav = wav / peak * 0.9
    wav_np = (np.clip(wav, -1, 1) * 32767).astype(np.int16)
    buf = io.BytesIO()
    sf.write(buf, wav_np, 24000, format='WAV', subtype='PCM_16')
    wav_bytes = buf.getvalue()
    _sys.stderr.write(f'[synthesise] peak_after_norm={peak:.4f} duration_s={len(wav_np)/24000:.2f}s bytes={len(wav_bytes)}\n'); _sys.stderr.flush()
    return Response(wav_bytes, mimetype='audio/wav')

app.run(port=args.port)
""";
        File.WriteAllText(path, script);
    }
}
