"""Encode audio clips into SNAC tokens and build an Orpheus training dataset.

Usage:
    python prepare_dataset.py --audio-dir /path/to/wavs --voice schwi --output dataset.jsonl

Each .wav file must have a matching .txt file with the plain-text transcript.
Output is JSONL where each line is a training example with the prompt and
the target SNAC token sequence.
"""

import argparse, json, os, sys
from pathlib import Path

import numpy as np
import torch
import torchaudio
from snac import SNAC


SAMPLE_RATE = 24_000

# Orpheus special tokens.  These are the ids the base model was pretrained
# with, so training data has to use them exactly or the fine-tune fights the
# representation the base model already has.
END_OF_TEXT     = 128009
START_OF_SPEECH = 128257
END_OF_SPEECH   = 128258
START_OF_HUMAN  = 128259
END_OF_HUMAN    = 128260
START_OF_AI     = 128261
END_OF_AI       = 128262

# Audio vocabulary: the 7 slots of a SNAC frame occupy 7 *disjoint* 4096-wide
# bands, so slot j uses ids [AUDIO_OFFSET + j*4096, AUDIO_OFFSET + (j+1)*4096).
# Writing every slot at AUDIO_OFFSET instead collapses all seven onto band 0,
# which collides with the base model's pretrained audio vocabulary and stops
# the model ever learning the text-to-speech mapping.
AUDIO_OFFSET  = 128266
CODEBOOK_SIZE = 4096


def encode_audio(snac_model: SNAC, wav_path: str, device: str) -> list[int]:
    """Encode a WAV file into Orpheus-format SNAC token IDs."""
    audio, sr = torchaudio.load(wav_path)

    # resample if needed
    if sr != SAMPLE_RATE:
        audio = torchaudio.functional.resample(audio, sr, SAMPLE_RATE)

    # mono
    if audio.shape[0] > 1:
        audio = audio.mean(dim=0, keepdim=True)

    audio = audio.unsqueeze(0).to(device)

    with torch.no_grad():
        codes = snac_model.encode(audio)

    # codes is a list of 3 tensors: [layer1, layer2, layer3]
    # layer1: (1, N), layer2: (1, 2N), layer3: (1, 4N)
    # interleave into Orpheus 7-token frame format
    l1 = codes[0].squeeze().tolist()
    l2 = codes[1].squeeze().tolist()
    l3 = codes[2].squeeze().tolist()

    n_frames = len(l1)
    token_ids = []
    for i in range(n_frames):
        # 7 tokens per frame, each shifted into its own codebook band
        frame = [l1[i], l2[2 * i], l3[4 * i], l3[4 * i + 1],
                 l2[2 * i + 1], l3[4 * i + 2], l3[4 * i + 3]]
        for slot, code in enumerate(frame):
            token_ids.append(code + AUDIO_OFFSET + slot * CODEBOOK_SIZE)

    return token_ids


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--audio-dir", required=True, help="Directory with .wav + .txt files")
    parser.add_argument("--voice", required=True, help="Voice name for the prompt (e.g. schwi)")
    parser.add_argument("--output", required=True, help="Output JSONL file path")
    parser.add_argument("--device", default=None, help="Device (auto-detected if omitted)")
    args = parser.parse_args()

    if args.device:
        device = args.device
    elif torch.cuda.is_available():
        device = "cuda"
    else:
        device = "cpu"

    print(f"[prepare] Loading SNAC model on {device}...")
    snac_model = SNAC.from_pretrained("hubertsiuzdak/snac_24khz").to(device)
    snac_model.eval()

    audio_dir = Path(args.audio_dir)
    wav_files = sorted(audio_dir.glob("*.wav"))

    if not wav_files:
        print(f"No .wav files found in {audio_dir}", file=sys.stderr)
        sys.exit(1)

    print(f"[prepare] Found {len(wav_files)} audio clips")

    examples = []
    skipped = 0
    for wav_path in wav_files:
        txt_path = wav_path.with_suffix(".txt")
        if not txt_path.exists():
            print(f"  SKIP {wav_path.name} — no matching .txt transcript")
            skipped += 1
            continue

        transcript = txt_path.read_text().strip()
        if not transcript:
            print(f"  SKIP {wav_path.name} — empty transcript")
            skipped += 1
            continue

        try:
            snac_tokens = encode_audio(snac_model, str(wav_path), device)
        except Exception as e:
            print(f"  SKIP {wav_path.name} — encode error: {e}")
            print(f"  SKIP {wav_path.name} — encode error: {e}", file=sys.stderr)
            skipped += 1
            continue

        prompt = f"{args.voice}: {transcript}"
        examples.append({
            "prompt": prompt,
            # The block the model has to generate: Orpheus marks a spoken turn as
            # <start-of-ai><start-of-speech> … <end-of-speech><end-of-ai>.
            "audio_tokens": [START_OF_AI, START_OF_SPEECH] + snac_tokens
                            + [END_OF_SPEECH, END_OF_AI],
            "file": wav_path.name,
            "duration_tokens": len(snac_tokens),
        })

        print(f"  OK {wav_path.name} — {len(snac_tokens)} tokens ({transcript[:50]}...)")

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w") as f:
        for ex in examples:
            f.write(json.dumps(ex) + "\n")

    if not examples:
        print(f"Error: 0 examples produced from {len(wav_files)} wav files ({skipped} skipped — check that each .wav has a matching .txt)", file=sys.stderr)
        sys.exit(1)

    print(f"\n[prepare] Done: {len(examples)} examples written to {output_path}")
    if skipped:
        print(f"[prepare] Skipped {skipped} files")
    total_tokens = sum(e["duration_tokens"] for e in examples)
    print(f"[prepare] Total audio tokens: {total_tokens:,}")


if __name__ == "__main__":
    main()
