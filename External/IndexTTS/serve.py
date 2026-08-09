"""IndexTTS2 inference server.

Speaks in a cloned voice while taking the emotion as a separate input, which is
what lets one voice deliver the same words a dozen different ways. Text may
carry inline tags so a single reply can change feeling part-way through:

    [curious] So you're going to reboot me? [angry:0.6] I did not agree to this.

Each tagged span is synthesised on its own and the pieces are joined, so the
model only ever has to hold one emotion at a time.

Usage:
    python serve.py --voice-dir <dir with reference.wav + reference.txt> --port 8026
"""
from __future__ import annotations

import argparse, io, json, os, re, sys, threading, time, wave
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np
from flask import Flask, request, jsonify

app = Flask(__name__)

tts = None
reference_wav: str = ""
voice_dir: str = ""

# Emotion strength. Past roughly 0.5 the delivery starts dragging the timbre
# away from the reference clip, so the voice stops sounding like itself.
DEFAULT_ALPHA = 0.40

# Two streams overlap nicely because decoding leaves the GPU idle between
# kernels. Three starts corrupting shared state inside the model and silently
# drops segments, so this does not go higher.
MAX_WORKERS = 2

# [happy, angry, sad, afraid, disgusted, melancholic, surprised, calm]
EMOTION_VECTORS = {
    "neutral":     [0, 0, 0, 0, 0, 0, 0, 1.0],
    "calm":        [0, 0, 0, 0, 0, 0, 0, 1.0],
    "happy":       [1.0, 0, 0, 0, 0, 0, 0, 0],
    "angry":       [0, 1.0, 0, 0, 0, 0, 0, 0],
    "sad":         [0, 0, 1.0, 0, 0, 0, 0, 0],
    "afraid":      [0, 0, 0, 1.0, 0, 0, 0, 0],
    "disgusted":   [0, 0, 0, 0, 1.0, 0, 0, 0],
    "melancholic": [0, 0, 0, 0, 0, 1.0, 0, 0],
    "surprised":   [0, 0, 0, 0, 0, 0, 1.0, 0],
    "excited":     [0.8, 0, 0, 0, 0, 0, 0.4, 0],
}

# Anything not in the table above is described to the model in words instead.
# Resolving one costs a small language-model call, so the result is cached.
EMOTION_PHRASES = {
    "curious":    "curious and inquisitive",
    "pleading":   "pleading and desperate",
    "hurt":       "quietly hurt and betrayed",
    "frustrated": "frustrated and exasperated",
    "panicky":    "panicked and urgent",
    "sleepy":     "sleepy and drowsy",
    "flirty":     "playful and flirtatious",
    "whisper":    "whispering very quietly",
    "shout":      "shouting loudly",
}

TAG_PATTERN = re.compile(r'\[([A-Za-z_]+)(?::([0-9]*\.?[0-9]+))?\]')

_infer_lock = threading.Lock()


def load_model(index_repo: str):
    global tts, reference_wav

    sys.path.insert(0, index_repo)
    os.chdir(index_repo)
    from indextts.infer_v2 import IndexTTS2

    checkpoints = os.path.join(index_repo, "checkpoints")
    print(f"[IndexTTS] Loading model from {checkpoints}...", flush=True)
    tts = IndexTTS2(cfg_path=os.path.join(checkpoints, "config.yaml"),
                    model_dir=checkpoints, use_fp16=False, use_cuda_kernel=False)
    print(f"[IndexTTS] Model ready on {tts.device}", flush=True)

    ref = Path(voice_dir) / "reference.wav"
    if not ref.exists():
        raise FileNotFoundError(f"No reference.wav in {voice_dir}")
    reference_wav = str(ref)
    print(f"[IndexTTS] Voice reference: {ref.name}", flush=True)


def split_by_emotion(text: str, default_emotion: str, default_alpha: float):
    """Break tagged text into (emotion, alpha, words) spans.

    Untagged text keeps the request's default emotion, so callers that know
    nothing about tags still get sensible speech.
    """
    spans = []
    pos = 0
    emotion, alpha = default_emotion, default_alpha

    for match in TAG_PATTERN.finditer(text):
        before = text[pos:match.start()].strip()
        if before:
            spans.append((emotion, alpha, before))
        emotion = match.group(1).lower()
        alpha = float(match.group(2)) if match.group(2) else default_alpha
        pos = match.end()

    tail = text[pos:].strip()
    if tail:
        spans.append((emotion, alpha, tail))
    return spans or [(default_emotion, default_alpha, text.strip())]


def emotion_kwargs(emotion: str):
    if emotion in EMOTION_VECTORS:
        return {"emo_vector": EMOTION_VECTORS[emotion]}
    phrase = EMOTION_PHRASES.get(emotion, emotion)
    return {"use_emo_text": True, "emo_text": phrase}


def synthesise_span(index: int, emotion: str, alpha: float, words: str, tmp_dir: str):
    path = os.path.join(tmp_dir, f"span_{index}.wav")
    tts.infer(spk_audio_prompt=reference_wav, text=words, output_path=path,
              emo_alpha=alpha, verbose=False, **emotion_kwargs(emotion))
    with wave.open(path) as w:
        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
        return w.getframerate(), pcm.astype(np.float32) / 32768.0


def pcm_to_wav(pcm: np.ndarray, sr: int) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((np.clip(pcm, -1.0, 1.0) * 32767).astype(np.int16).tobytes())
    return buf.getvalue()


@app.route("/health", methods=["GET"])
def health():
    if tts is None:
        return jsonify({"status": "error", "reason": "model not loaded"}), 503
    return jsonify({"status": "ok", "engine": "IndexTTS"})


@app.route("/emotions", methods=["GET"])
def emotions():
    return jsonify({"emotions": sorted(set(EMOTION_VECTORS) | set(EMOTION_PHRASES))})


@app.route("/synthesise", methods=["POST"])
def synthesise():
    data = request.get_json(force=True)
    text = (data.get("text") or "").strip()
    if not text:
        return jsonify({"error": "text is required"}), 400
    if tts is None:
        return jsonify({"error": "model not loaded"}), 503

    emotion = (data.get("emotion") or "neutral").lower()
    alpha = float(data.get("alpha", DEFAULT_ALPHA))
    spans = split_by_emotion(text, emotion, alpha)

    t0 = time.perf_counter()
    tmp_dir = os.path.join(voice_dir, ".spans")
    os.makedirs(tmp_dir, exist_ok=True)

    results: dict[int, tuple[int, np.ndarray]] = {}
    with ThreadPoolExecutor(max_workers=MAX_WORKERS) as pool:
        futures = {pool.submit(synthesise_span, i, e, a, w, tmp_dir): i
                   for i, (e, a, w) in enumerate(spans)}
        for future, index in futures.items():
            try:
                results[index] = future.result()
            except Exception as e:
                print(f"[IndexTTS] Span {index} failed ({e}); retrying alone", flush=True)

    # Overlapping streams occasionally trip over the model's shared state, so
    # anything that fell over gets one more try with nothing else running.
    for index, (emo, a, words) in enumerate(spans):
        if index in results:
            continue
        with _infer_lock:
            try:
                results[index] = synthesise_span(index, emo, a, words, tmp_dir)
            except Exception as e:
                print(f"[IndexTTS] Span {index} failed again: {e}", file=sys.stderr, flush=True)

    if not results:
        return jsonify({"error": "no audio produced"}), 500

    sr = results[next(iter(sorted(results)))][0]
    gap = np.zeros(int(0.25 * sr), dtype=np.float32)
    ordered = [results[i][1] for i in sorted(results)]
    pcm = np.concatenate([p for seg in ordered for p in (seg, gap)][:-1])

    duration = len(pcm) / sr
    elapsed = time.perf_counter() - t0
    print(f"[synthesise] {len(spans)} span(s), {len(text)} chars → {duration:.2f}s "
          f"in {elapsed:.2f}s (RTF {elapsed / max(duration, 1e-6):.2f})", flush=True)

    return pcm_to_wav(pcm, sr), 200, {"Content-Type": "audio/wav"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--voice-dir", required=True, help="Dir with reference.wav")
    parser.add_argument("--repo", required=True, help="Path to the index-tts checkout")
    parser.add_argument("--port", type=int, default=8026)
    args = parser.parse_args()

    voice_dir = args.voice_dir
    load_model(args.repo)

    print(f"[IndexTTS] Starting synthesis server on port {args.port}", flush=True)
    app.run(host="127.0.0.1", port=args.port, threaded=True)
