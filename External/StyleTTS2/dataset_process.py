#!/usr/bin/env python3
"""Dataset builder backend. Invoked as: python dataset_process.py <stageDir>

For every uploaded clip in <stageDir>: isolate vocals with demucs, split the clip
at the silences in the vocal stem, and cut BOTH the original and the vocal stem at
the same boundaries so part N of each lines up. Whisper transcribes every part.

Writes to <stageDir>:
  original/<stem>_partNN.wav   processed/<stem>_partNN.wav   manifest.json

Reads uploaded clips read-only. Prints "PROGRESS done/total <clip>" per clip so the
C# DatasetBuilder can drive a progress bar.
"""
import contextlib
import glob
import json
import os
import subprocess
import sys
import wave

import numpy as np
from faster_whisper import WhisperModel

# Recurring German words spelled the way they should sound to English espeak, so the
# phonemes match the audio. Add entries as they come up (e.g. "Einzig": "Eintsig").
LEXICON = {}

SILENCE_DB      = "-35dB"
MIN_SILENCE_S   = 0.4
PART_PAD_S      = 0.1     # keep a little air around each part so speech isn't clipped
MIN_PART_S      = 0.4     # drop fragments shorter than this
SAMPLE_RATE     = 24000
TOO_SHORT_S     = 1.0
HIGH_NOSPEECH   = 0.5


def wav_rms(path):
    try:
        with contextlib.closing(wave.open(path, "rb")) as w:
            frames = w.readframes(w.getnframes())
            width  = w.getsampwidth()
        dtype  = {1: np.int8, 2: np.int16, 4: np.int32}[width]
        sample = np.frombuffer(frames, dtype=dtype).astype(np.float64)
        if sample.size == 0:
            return 0.0
        sample /= 2 ** (8 * width - 1)
        return float(np.sqrt(np.mean(sample ** 2)))
    except Exception:
        return 0.0


def clip_duration(path):
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "csv=p=0", path], capture_output=True, text=True)
    return float(out.stdout.strip() or 0)


def speech_segments(vocal_path, duration):
    """Speech intervals between the silences detected in the vocal stem."""
    out = subprocess.run(
        ["ffmpeg", "-i", vocal_path, "-af",
         f"silencedetect=noise={SILENCE_DB}:d={MIN_SILENCE_S}", "-f", "null", "-"],
        capture_output=True, text=True)
    segments, cursor = [], 0.0
    for line in out.stderr.splitlines():
        if "silence_start:" in line:
            start = float(line.split("silence_start:")[1])
            if start - cursor > MIN_PART_S:
                segments.append((cursor, start))
        if "silence_end:" in line:
            cursor = float(line.split("silence_end:")[1].split("|")[0])
    if duration - cursor > MIN_PART_S:
        segments.append((cursor, duration))
    return segments or [(0.0, duration)]


def cut(src, start, end, dest):
    start = max(0.0, start - PART_PAD_S)
    subprocess.run(
        ["ffmpeg", "-y", "-i", src, "-ss", f"{start}", "-to", f"{end + PART_PAD_S}",
         "-ac", "1", "-ar", str(SAMPLE_RATE), dest], capture_output=True)


def apply_lexicon(text):
    for word, spelling in LEXICON.items():
        text = text.replace(word, spelling)
    return text


def write_manifest(path, manifest):
    # Atomic replace so the C# side never reads a half-written file mid-poll.
    tmp = path + ".tmp"
    with open(tmp, "w") as fh:
        json.dump(manifest, fh)
    os.replace(tmp, path)


def main():
    stage_dir = sys.argv[1]
    original_dir  = os.path.join(stage_dir, "original")
    processed_dir = os.path.join(stage_dir, "processed")
    demucs_dir    = os.path.join(stage_dir, "_demucs")
    for folder in (original_dir, processed_dir, demucs_dir):
        os.makedirs(folder, exist_ok=True)

    manifest_path = os.path.join(stage_dir, "manifest.json")
    manifest, done_clips = [], set()
    if os.path.exists(manifest_path):
        try:
            manifest = json.load(open(manifest_path))
            done_clips = {entry["clip"] for entry in manifest}
        except Exception:
            manifest = []

    clips = sorted(glob.glob(os.path.join(stage_dir, "*.wav")))
    print(f"Loading whisper large-v3 for {len(clips)} clips", flush=True)
    model = WhisperModel("large-v3", device="cpu", compute_type="int8")

    for index, clip in enumerate(clips, 1):
        stem = os.path.splitext(os.path.basename(clip))[0]
        print(f"PROGRESS {index - 1}/{len(clips)} {stem}", flush=True)
        if stem in done_clips:
            continue

        vocal     = os.path.join(demucs_dir, "htdemucs", stem, "vocals.wav")
        no_vocal  = os.path.join(demucs_dir, "htdemucs", stem, "no_vocals.wav")
        if not os.path.exists(vocal):   # reuse cached separation on a resume
            subprocess.run([sys.executable, "-m", "demucs", "--two-stems=vocals",
                            "-d", "mps", "-o", demucs_dir, clip], capture_output=True)
        if not os.path.exists(vocal):
            print(f"demucs produced no vocals for {stem}, skipping", flush=True)
            continue

        bg_ratio = wav_rms(no_vocal) / (wav_rms(vocal) + 1e-9)
        duration = clip_duration(vocal)
        segments = speech_segments(vocal, duration)

        for part, (start, end) in enumerate(segments, 1):
            name      = f"{stem}_part{part:02d}"
            orig_out  = os.path.join(original_dir,  f"{name}.wav")
            proc_out  = os.path.join(processed_dir, f"{name}.wav")
            cut(clip,  start, end, orig_out)
            cut(vocal, start, end, proc_out)

            pieces, info = model.transcribe(proc_out, beam_size=5, vad_filter=False)
            pieces = list(pieces)
            text   = apply_lexicon(" ".join(p.text.strip() for p in pieces).strip())
            nsp    = sum(p.no_speech_prob for p in pieces) / len(pieces) if pieces else 1.0
            length = clip_duration(proc_out)

            flags = []
            if length < TOO_SHORT_S: flags.append("TOO_SHORT")
            if not text:             flags.append("EMPTY")
            if nsp > HIGH_NOSPEECH:  flags.append("HIGH_NOSPEECH")

            manifest.append({
                "clip": stem, "name": name, "part": part,
                "duration": round(length, 2), "language": info.language,
                "transcript": text, "no_speech": round(nsp, 2),
                "bg_ratio": round(bg_ratio, 2), "flags": flags,
            })
            print(f"  {name} {length:.1f}s {info.language} "
                  f"{'|'.join(flags)} :: {text[:50]}", flush=True)

        # Publish after every clip so the panel can show parts as they finish.
        write_manifest(manifest_path, manifest)

    write_manifest(manifest_path, manifest)
    print(f"PROGRESS {len(clips)}/{len(clips)} done", flush=True)
    print(f"Wrote manifest with {len(manifest)} parts", flush=True)


if __name__ == "__main__":
    main()
