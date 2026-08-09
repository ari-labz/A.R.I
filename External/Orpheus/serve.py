"""Orpheus TTS inference server.

Sits between ARI and a running llama-server instance.  Accepts text + emotion,
sends a prompt to llama-server's /completion endpoint, decodes the returned
SNAC audio tokens into a 24 kHz WAV, and streams it back.

Usage:
    python serve.py --model <path-to-gguf> --port 8021

The script starts its own llama-server subprocess unless --llama-url is given
(useful when the model is already loaded on a shared server).
"""
from __future__ import annotations

import argparse, io, json, os, re, signal, struct, subprocess, sys, time
from pathlib import Path

import numpy as np
from flask import Flask, request, jsonify

app = Flask(__name__)

# ── globals filled at startup ────────────────────────────────────────────────
llama_url: str = ""
llama_proc: subprocess.Popen | None = None
snac_model = None
voice_name: str = "tara"

SAMPLE_RATE = 24_000
SNAC_TOKENS_PER_FRAME = 7

# Orpheus special tokens — must match training/prepare_dataset.py exactly.
START_OF_HUMAN = 128259
END_OF_TEXT    = 128009
END_OF_HUMAN   = 128260
START_OF_AUDIO = 128257
END_OF_AUDIO   = 128258

# Each of the 7 slots in a SNAC frame lives in its own 4096-wide band.
AUDIO_OFFSET  = 128266
CODEBOOK_SIZE = 4096

SUPPORTED_EMOTIONS = ["<laugh>", "<chuckle>", "<sigh>", "<cough>",
                       "<sniffle>", "<groan>", "<yawn>", "<gasp>"]


class LegacyEncodingError(RuntimeError):
    """Raised when a voice predates the per-slot SNAC token banding."""


def load_snac():
    global snac_model
    from snac import SNAC
    import torch
    device = "cpu"
    snac_model = SNAC.from_pretrained("hubertsiuzdak/snac_24khz").to(device)
    snac_model.eval()
    print(f"[Orpheus] SNAC decoder loaded on {device}", flush=True)


def tokenise(text: str) -> list[int]:
    """Tokenise via llama-server without adding a BOS the training data lacked."""
    import urllib.request

    payload = json.dumps({"content": text, "add_special": False}).encode()
    req = urllib.request.Request(f"{llama_url}/tokenize", data=payload,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read())["tokens"]


def format_prompt(text: str, voice: str | None = None) -> list[int]:
    """Build the prompt as token ids, mirroring training/train.py exactly.

    Returned as ids rather than a string so llama-server tokenises nothing on
    our behalf — a stray BOS here shifts the whole sequence off what the model
    was trained on.
    """
    v = voice or voice_name
    return ([START_OF_HUMAN]
            + tokenise(f"{v}: {text}")
            + [END_OF_TEXT, END_OF_HUMAN])


def tokens_to_audio(token_ids: list[int]) -> np.ndarray:
    """Decode Orpheus SNAC tokens into a float32 PCM waveform."""
    import torch

    # The model emits the audio block between START_OF_AUDIO and END_OF_AUDIO.
    # Collect strictly inside it so a stray text token can never shift the
    # 7-token frame boundaries and turn the whole clip into noise.
    audio_ids = []
    collecting = False
    for t in token_ids:
        if t == START_OF_AUDIO:
            collecting = True
            audio_ids.clear()   # keep only the last audio block
            continue
        if not collecting:
            continue
        if t in (END_OF_AUDIO, END_OF_TEXT):
            break
        if t >= AUDIO_OFFSET:
            audio_ids.append(t - AUDIO_OFFSET)

    # trim to multiple of 7
    n = (len(audio_ids) // SNAC_TOKENS_PER_FRAME) * SNAC_TOKENS_PER_FRAME
    if n == 0:
        return np.zeros(0, dtype=np.float32)
    audio_ids = audio_ids[:n]

    # A model trained by an older prepare_dataset.py wrote every slot into band
    # 0 instead of its own band, so it can never voice the text it is given.
    # Say so plainly rather than emitting a clip of noise.
    if max(audio_ids) < CODEBOOK_SIZE:
        raise LegacyEncodingError(
            "This voice was trained with the old flat SNAC token encoding, so it "
            "cannot follow the text it is given. Regenerate the dataset with "
            "training/prepare_dataset.py and retrain the voice."
        )

    # undo the per-slot band offset, then redistribute into 3 codebook layers
    layer1, layer2, layer3 = [], [], []
    dropped = 0
    for i in range(0, n, SNAC_TOKENS_PER_FRAME):
        frame = [audio_ids[i + slot] - slot * CODEBOOK_SIZE
                 for slot in range(SNAC_TOKENS_PER_FRAME)]
        if any(c < 0 or c >= CODEBOOK_SIZE for c in frame):
            dropped += 1        # slot landed outside its band; skip the frame
            continue
        layer1.append(frame[0])
        layer2.append(frame[1])
        layer3.append(frame[2])
        layer3.append(frame[3])
        layer2.append(frame[4])
        layer3.append(frame[5])
        layer3.append(frame[6])

    if dropped:
        print(f"[Orpheus] dropped {dropped}/{n // SNAC_TOKENS_PER_FRAME} malformed frames",
              flush=True)
    if not layer1:
        return np.zeros(0, dtype=np.float32)

    device = next(snac_model.parameters()).device
    codes = [
        torch.tensor(layer1, dtype=torch.long, device=device).unsqueeze(0),
        torch.tensor(layer2, dtype=torch.long, device=device).unsqueeze(0),
        torch.tensor(layer3, dtype=torch.long, device=device).unsqueeze(0),
    ]

    with torch.no_grad():
        audio = snac_model.decode(codes)

    return audio.squeeze().cpu().numpy().astype(np.float32)


def pcm_to_wav(pcm: np.ndarray, sr: int = SAMPLE_RATE) -> bytes:
    """Pack float32 PCM into a 16-bit WAV."""
    pcm = np.clip(pcm, -1.0, 1.0)
    pcm_int16 = (pcm * 32767).astype(np.int16)
    buf = io.BytesIO()
    # WAV header
    data_size = len(pcm_int16) * 2
    buf.write(b"RIFF")
    buf.write(struct.pack("<I", 36 + data_size))
    buf.write(b"WAVE")
    buf.write(b"fmt ")
    buf.write(struct.pack("<IHHIIHH", 16, 1, 1, sr, sr * 2, 2, 16))
    buf.write(b"data")
    buf.write(struct.pack("<I", data_size))
    buf.write(pcm_int16.tobytes())
    return buf.getvalue()


def call_llama(prompt: list[int], max_tokens: int = 1200, temperature: float = 0.6,
               top_p: float = 0.9, repetition_penalty: float = 1.1) -> list[int]:
    """Call llama-server /completion (streaming) and return generated token IDs.

    `prompt` is a list of token ids so llama-server does no tokenising of its
    own.  cache_prompt is off because slot reuse lets a previous request's
    audio block satisfy the prefix, after which the model emits an immediate
    end-of-text and the request comes back with no audio at all.
    """
    import urllib.request, urllib.error

    payload = json.dumps({
        "prompt": prompt,
        "n_predict": max_tokens,
        "temperature": temperature,
        "top_p": top_p,
        "repeat_penalty": repetition_penalty,
        "stream": True,
        "cache_prompt": False,
    }).encode()

    req = urllib.request.Request(
        f"{llama_url}/completion",
        data=payload,
        headers={"Content-Type": "application/json"},
    )
    try:
        token_ids = []
        with urllib.request.urlopen(req, timeout=120) as resp:
            for raw_line in resp:
                line = raw_line.decode("utf-8").strip()
                if not line:
                    continue
                # strip SSE prefix if present
                json_str = line[6:] if line.startswith("data: ") else line
                try:
                    chunk = json.loads(json_str)
                except json.JSONDecodeError:
                    continue
                # newer llama.cpp reports generated ids as 'tokens' per chunk
                toks = chunk.get("tokens")
                if isinstance(toks, list):
                    token_ids.extend(toks)
                elif isinstance(toks, int):
                    token_ids.append(toks)
                if END_OF_AUDIO in token_ids:
                    break
                if chunk.get("stop"):
                    break
        return token_ids
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors="replace")
        print(f"[Orpheus] llama-server error {e.code}: {body}", file=sys.stderr, flush=True)
        raise


# ── Flask routes ─────────────────────────────────────────────────────────────

@app.route("/health", methods=["GET"])
def health():
    import urllib.request
    try:
        with urllib.request.urlopen(f"{llama_url}/health", timeout=3) as r:
            data = json.loads(r.read())
            if data.get("status") != "ok":
                return jsonify({"status": "error", "reason": "llama-server not ready"}), 503
    except Exception as e:
        return jsonify({"status": "error", "reason": f"llama-server unreachable: {e}"}), 503
    return jsonify({"status": "ok", "engine": "Orpheus"})


@app.route("/synthesise", methods=["POST"])
def synthesise():
    data = request.get_json(force=True)
    text = data.get("text", "").strip()
    if not text:
        return jsonify({"error": "text is required"}), 400

    voice       = data.get("voice", voice_name)
    temperature = float(data.get("temperature", 0.6))
    top_p       = float(data.get("top_p", 0.9))
    rep_penalty = float(data.get("repetition_penalty", 1.1))
    max_tokens  = int(data.get("max_tokens", 1200))

    t0 = time.perf_counter()
    prompt = format_prompt(text, voice)
    tokens = call_llama(prompt, max_tokens, temperature, top_p, rep_penalty)
    t_llm = time.perf_counter() - t0

    try:
        pcm = tokens_to_audio(tokens)
    except LegacyEncodingError as e:
        print(f"[Orpheus] {e}", file=sys.stderr, flush=True)
        return jsonify({"error": str(e)}), 500
    t_decode = time.perf_counter() - t0 - t_llm

    if pcm.size == 0:
        return jsonify({"error": "no audio produced"}), 500

    wav = pcm_to_wav(pcm)
    duration = len(pcm) / SAMPLE_RATE
    print(f"[synthesise] {len(text)} chars → {len(tokens)} tokens → {duration:.2f}s audio "
          f"(LLM {t_llm:.2f}s, decode {t_decode:.2f}s)", flush=True)

    return wav, 200, {"Content-Type": "audio/wav"}


@app.route("/voices", methods=["GET"])
def list_voices():
    return jsonify({"voices": ["tara", "leah", "jess", "leo", "dan", "mia", "zac", "zoe"]})


@app.route("/emotions", methods=["GET"])
def list_emotions():
    return jsonify({"emotions": SUPPORTED_EMOTIONS})


# ── llama-server management ──────────────────────────────────────────────────

def start_llama_server(model_path: str, port: int = 8022, gpu_layers: int = 99,
                       ctx_size: int = 2048, llama_exe: str = "llama-server") -> subprocess.Popen:
    cmd = [
        llama_exe,
        "-m", model_path,
        "--port", str(port),
        "-ngl", str(gpu_layers),
        "-c", str(ctx_size),
        "--n-predict", "1200",
    ]
    print(f"[Orpheus] Starting llama-server: {' '.join(cmd)}", flush=True)
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    import threading
    def _log_stderr(p):
        for line in p.stderr:
            print(f"[llama-server] {line.decode(errors='replace').rstrip()}", file=sys.stderr, flush=True)
    threading.Thread(target=_log_stderr, args=(proc,), daemon=True).start()
    # wait for it to be ready
    import urllib.request
    for _ in range(120):
        time.sleep(1)
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as r:
                data = json.loads(r.read())
                if data.get("status") == "ok":
                    print(f"[Orpheus] llama-server ready on port {port}", flush=True)
                    return proc
        except Exception:
            if proc.poll() is not None:
                raise RuntimeError(f"llama-server exited with code {proc.returncode}")
    raise TimeoutError("llama-server did not start within 120s")


def cleanup(signum=None, frame=None):
    if llama_proc:
        llama_proc.terminate()
        llama_proc.wait(timeout=5)
    sys.exit(0)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=False, help="Path to Orpheus GGUF model")
    parser.add_argument("--port", type=int, default=8021, help="Port for this Flask server")
    parser.add_argument("--llama-port", type=int, default=8024, help="Port for llama-server")
    parser.add_argument("--llama-url", default="", help="URL of already-running llama-server")
    parser.add_argument("--voice", default="", help="Default voice name (defaults to the model's own name)")
    parser.add_argument("--gpu-layers", type=int, default=0)
    parser.add_argument("--ctx-size", type=int, default=1024)
    parser.add_argument("--llama-exe", default="llama-server", help="Path to llama-server executable")
    args = parser.parse_args()

    # A fine-tune only answers to the speaker name its dataset was built with,
    # and prepare_dataset.py uses the model's own name.  Falling back to a stock
    # voice the fine-tune never saw leaves the model unconditioned.
    voice_name = args.voice or (Path(args.model).stem if args.model else "tara")
    print(f"[Orpheus] Voice: {voice_name}", flush=True)

    if args.llama_url:
        llama_url = args.llama_url.rstrip("/")
    elif args.model:
        llama_proc = start_llama_server(args.model, args.llama_port, args.gpu_layers, args.ctx_size, args.llama_exe)
        llama_url = f"http://127.0.0.1:{args.llama_port}"
        signal.signal(signal.SIGTERM, cleanup)
        signal.signal(signal.SIGINT, cleanup)
    else:
        print("Error: either --model or --llama-url is required", file=sys.stderr)
        sys.exit(1)

    load_snac()

    print(f"[Orpheus] Starting synthesis server on port {args.port}", flush=True)
    app.run(host="127.0.0.1", port=args.port, threaded=True)
