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
START_TOKEN = 128259
END_TOKEN   = 128009
AUDIO_OFFSET = 128266

SUPPORTED_EMOTIONS = ["<laugh>", "<chuckle>", "<sigh>", "<cough>",
                       "<sniffle>", "<groan>", "<yawn>", "<gasp>"]


def load_snac():
    global snac_model
    from snac import SNAC
    import torch
    device = "cpu"
    snac_model = SNAC.from_pretrained("hubertsiuzdak/snac_24khz").to(device)
    snac_model.eval()
    print(f"[Orpheus] SNAC decoder loaded on {device}", flush=True)


def format_prompt(text: str, voice: str | None = None) -> str:
    v = voice or voice_name
    return (
        "<|start_header_id|>system<|end_header_id|>\n\n"
        "<|eot_id|>"
        "<|start_header_id|>user<|end_header_id|>\n\n"
        f"{v}: {text}"
        "<|eot_id|>"
        "<|start_header_id|>assistant<|end_header_id|>\n\n"
    )


def tokens_to_audio(token_ids: list[int]) -> np.ndarray:
    """Decode Orpheus SNAC tokens into a float32 PCM waveform."""
    import torch

    # Find first START_TOKEN; only collect audio tokens strictly after it
    audio_ids = []
    collecting = False
    for t in token_ids:
        if t == START_TOKEN:
            collecting = True
            continue
        if not collecting:
            continue
        if t == END_TOKEN:
            break
        if t >= AUDIO_OFFSET:
            audio_ids.append(t - AUDIO_OFFSET)

    if len(audio_ids) == 0:
        return np.zeros(0, dtype=np.float32)

    # trim to multiple of 7
    n = (len(audio_ids) // SNAC_TOKENS_PER_FRAME) * SNAC_TOKENS_PER_FRAME
    audio_ids = audio_ids[:n]
    if n == 0:
        return np.zeros(0, dtype=np.float32)

    # redistribute into 3 codebook layers
    layer1, layer2, layer3 = [], [], []
    for i in range(0, n, SNAC_TOKENS_PER_FRAME):
        layer1.append(audio_ids[i])
        layer2.append(audio_ids[i + 1])
        layer3.append(audio_ids[i + 2])
        layer3.append(audio_ids[i + 3])
        layer2.append(audio_ids[i + 4])
        layer3.append(audio_ids[i + 5])
        layer3.append(audio_ids[i + 6])

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


def call_llama(prompt: str, max_tokens: int = 1200, temperature: float = 0.6,
               top_p: float = 0.9, repetition_penalty: float = 1.1) -> list[int]:
    """Call llama-server /completion (streaming) and return generated token IDs."""
    import urllib.request, urllib.error

    payload = json.dumps({
        "prompt": prompt,
        "n_predict": max_tokens,
        "temperature": temperature,
        "top_p": top_p,
        "repeat_penalty": repetition_penalty,
        "stop": ["<|eot_id|>"],
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
        first_logged = False
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
                    if not first_logged:
                        print(f"[Orpheus] raw line (not JSON): {repr(line[:120])}", flush=True)
                        first_logged = True
                    continue
                if not first_logged:
                    print(f"[Orpheus] first chunk keys: {list(chunk.keys())}, tokens val: {repr(chunk.get('tokens'))[:120]}", flush=True)
                    first_logged = True
                # newer llama.cpp uses 'tokens' (list of ints) per chunk
                toks = chunk.get("tokens")
                if isinstance(toks, list):
                    token_ids.extend(toks)
                elif isinstance(toks, int):
                    token_ids.append(toks)
                if chunk.get("stop"):
                    break
        audio_count = sum(1 for t in token_ids if t >= AUDIO_OFFSET)
        print(f"[Orpheus] stream: {len(token_ids)} tokens, {audio_count} audio, first 10: {token_ids[:10]}", flush=True)
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

    pcm = tokens_to_audio(tokens)
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
    parser.add_argument("--voice", default="tara", help="Default voice name")
    parser.add_argument("--gpu-layers", type=int, default=0)
    parser.add_argument("--ctx-size", type=int, default=1024)
    parser.add_argument("--llama-exe", default="llama-server", help="Path to llama-server executable")
    args = parser.parse_args()

    voice_name = args.voice

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
