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
START_TOKEN = 128259
END_TOKEN   = 128009
AUDIO_OFFSET = 128266


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
        # 7 tokens per frame: l1[i], l2[2i], l3[4i], l3[4i+1], l2[2i+1], l3[4i+2], l3[4i+3]
        token_ids.append(l1[i] + AUDIO_OFFSET)
        token_ids.append(l2[2 * i] + AUDIO_OFFSET)
        token_ids.append(l3[4 * i] + AUDIO_OFFSET)
        token_ids.append(l3[4 * i + 1] + AUDIO_OFFSET)
        token_ids.append(l2[2 * i + 1] + AUDIO_OFFSET)
        token_ids.append(l3[4 * i + 2] + AUDIO_OFFSET)
        token_ids.append(l3[4 * i + 3] + AUDIO_OFFSET)

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
            "audio_tokens": [START_TOKEN] + snac_tokens + [END_TOKEN],
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
