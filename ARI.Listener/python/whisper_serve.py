#!/usr/bin/env python3
"""
whisper_serve.py — ARI.Listener's speech-to-text worker.

A tiny WebSocket server that receives raw PCM (16-bit LE, mono, 16 kHz), segments it into utterances with
VAD (WebRTC VAD, energy fallback), transcribes each finished utterance with faster-whisper, and sends back
JSON: {"type": "partial"|"final", "text": "..."} (finals add "clarity" and a "marked" text) (partials every ~0.5 s while speaking). The C# ListenerSession pipes the browser mic to this and forwards
transcripts to the awareness gate.

Run: python whisper_serve.py --port 8123 --model base.en
Deps: pip install faster-whisper websockets numpy webrtcvad   (webrtcvad optional; energy VAD used if absent)
"""
import argparse
import asyncio
import json
import re
import sys

import numpy as np
import websockets
from faster_whisper import WhisperModel

try:
    import webrtcvad
    HAVE_VAD = True
except Exception:
    HAVE_VAD = False

SAMPLE_RATE = 16000
FRAME_MS = 30
FRAME_BYTES = int(SAMPLE_RATE * FRAME_MS / 1000) * 2  # 480 samples * 2 bytes = 960
SILENCE_MS_END = 600      # trailing silence that ends an utterance
MAX_UTTERANCE_MS = 15000  # hard cap so a long monologue still flushes
MIN_SPEECH_MS = 350       # require this much actual speech before transcribing (rejects blips/noise)

# Common whisper hallucinations on silence/noise — dropped outright.
HALLUCINATIONS = {
    "you", "thank you", "thank you.", "thanks for watching", "thanks for watching!",
    "okay", "ok", "bye", "bye.", "so", "uh", "um", "hmm", "mm", "yeah", ".", "the",
    "please subscribe", "subscribe", "i'm sorry",
}

model = None


def log(*a):
    print(*a, file=sys.stderr, flush=True)


def is_junk(text: str) -> bool:
    """True if a transcript looks like a hallucination rather than real speech."""
    t = text.strip().lower().strip(" .,!?-—…")
    if not t or not any(c.isalpha() for c in t):
        return True
    if t in HALLUCINATIONS:
        return True
    words = t.split()
    # highly repetitive output (e.g. "okay okay okay …") = hallucination loop
    if len(words) >= 6 and len(set(words)) <= max(2, len(words) // 4):
        return True
    return False


class Segmenter:
    """Frame-by-frame VAD endpointing: accumulate speech, emit an utterance on trailing silence."""

    def __init__(self):
        self.vad = webrtcvad.Vad(3) if HAVE_VAD else None  # 3 = most aggressive: rejects more non-speech
        self.buf = bytearray()    # leftover bytes not yet a full frame
        self.utter = bytearray()  # accumulated speech (+ trailing silence)
        self.silence_ms = 0
        self.had_speech = False
        self.utter_ms = 0
        self.speech_ms = 0        # how much of the utterance was actually speech
        self.utterance_id = 0     # bumps on every reset, so a late partial can tell its utterance has ended
        self.extra_ms = 0         # extra silence to wait when the latest partial sounds unfinished

    def _is_speech(self, frame: bytes) -> bool:
        if self.vad is not None:
            return self.vad.is_speech(frame, SAMPLE_RATE)
        arr = np.frombuffer(frame, dtype=np.int16).astype(np.float32)
        return float(np.sqrt(np.mean(arr * arr))) > 500.0  # energy fallback

    def add(self, data: bytes):
        """Feed PCM bytes; return a list of finalized utterance byte-blobs (usually 0 or 1)."""
        self.buf.extend(data)
        out = []
        while len(self.buf) >= FRAME_BYTES:
            frame = bytes(self.buf[:FRAME_BYTES])
            del self.buf[:FRAME_BYTES]
            if self._is_speech(frame):
                self.utter.extend(frame)
                self.had_speech = True
                self.silence_ms = 0
                self.utter_ms += FRAME_MS
                self.speech_ms += FRAME_MS
            elif self.had_speech:
                self.utter.extend(frame)
                self.utter_ms += FRAME_MS
                self.silence_ms += FRAME_MS
                if self.silence_ms >= SILENCE_MS_END + self.extra_ms:
                    if self.speech_ms >= MIN_SPEECH_MS:
                        out.append(bytes(self.utter))
                    self._reset()
            if self.had_speech and self.utter_ms >= MAX_UTTERANCE_MS:
                if self.speech_ms >= MIN_SPEECH_MS:
                    out.append(bytes(self.utter))
                self._reset()
        return out

    def flush(self):
        u = bytes(self.utter) if self.speech_ms >= MIN_SPEECH_MS else None
        self._reset()
        return u

    def _reset(self):
        self.utterance_id += 1
        self.extra_ms = 0
        self.utter = bytearray()
        self.silence_ms = 0
        self.had_speech = False
        self.utter_ms = 0
        self.speech_ms = 0


def transcribe(pcm: bytes) -> str:
    """Partials: quick text only, low-confidence segments dropped."""
    return transcribe_final(pcm, partial=True)["text"]


# How sure Whisper was of a final, from its own scores. avg_logprob is about -0.25 for clear speech and falls to
# -0.5..-1.3 when wind, noise or a cutting-out mic garbles it; a word below LOW_WORD_PROB is one it was guessing at.
UNCLEAR_LOGPROB  = -0.55   # below this the turn is "unclear": the gist is probably there, some words may be wrong
GARBLED_LOGPROB  = -1.0    # below this it is "garbled": mostly guesswork
DROP_LOGPROB     = -1.8    # below this it is noise, not speech
LOW_WORD_PROB    = 0.5
UNCLEAR_LOW_FRAC = 0.25    # this share of guessed words also makes a turn unclear


def transcribe_final(pcm: bytes, partial: bool = False) -> dict:
    """Returns {"text", "clarity": clear|unclear|garbled, "marked"}: marked is the text with guessed words flagged (?)."""
    audio = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    segments, _ = model.transcribe(
        audio,
        language="en",
        beam_size=1,
        vad_filter=False,                  # we already VAD-segment upstream
        temperature=0.0,                   # deterministic; no creative fallbacks
        condition_on_previous_text=False,  # stops "okay okay okay…" runaway loops
        no_speech_threshold=0.6,           # skip segments the model thinks are silence
        log_prob_threshold=-1.0,
        compression_ratio_threshold=2.4,   # drop repetitive/degenerate segments
        word_timestamps=not partial,       # per-word confidence; adds next to nothing on short turns
    )
    floor = -1.0 if partial else DROP_LOGPROB
    kept = []
    for s in segments:
        if getattr(s, "no_speech_prob", 0.0) > 0.6:
            continue
        if getattr(s, "avg_logprob", 0.0) < floor:
            continue
        if getattr(s, "compression_ratio", 1.0) > 2.4:
            continue
        kept.append(s)
    text = " ".join(s.text.strip() for s in kept).strip()
    if partial or not kept:
        return {"text": text, "clarity": "clear", "marked": text}

    # Weight each segment's score by its length so one short shaky segment doesn't decide a long turn.
    lengths = [max(1, len(s.text)) for s in kept]
    logprob = sum(s.avg_logprob * n for s, n in zip(kept, lengths)) / sum(lengths)
    words = [w for s in kept for w in (s.words or [])]
    low = [w for w in words if w.probability < LOW_WORD_PROB]
    low_frac = len(low) / len(words) if words else 0.0
    if logprob < GARBLED_LOGPROB:
        clarity = "garbled"
    elif logprob < UNCLEAR_LOGPROB or low_frac >= UNCLEAR_LOW_FRAC:
        clarity = "unclear"
    else:
        clarity = "clear"
    marked = text
    if clarity != "clear" and words:
        marked = "".join(w.word + ("(?)" if w.probability < LOW_WORD_PROB else "") for w in words).strip()
    log(f"[whisper_serve] clarity={clarity} logprob={logprob:.2f} guessed={len(low)}/{len(words)}")
    return {"text": text, "clarity": clarity, "marked": marked}


# A pause after "and", "um", "so" or a trailing comma usually isn't the end of the turn; wait this much longer.
UNFINISHED_EXTRA_MS = 600
UNFINISHED_TAIL = re.compile(r"(,|\b(and|but|so|or|um+|uh+|er+|like|because|the|a|to|of|with|if|that|my|your|i)\.?)\s*$", re.I)

PARTIAL_EVERY_MS = 500  # re-transcribe the growing utterance this often, so Ari can start on it before it ends


async def handle(ws, *_):
    seg = Segmenter()
    loop = asyncio.get_event_loop()
    partial = {"task": None, "at_ms": 0}

    async def send_partial(pcm: bytes, uid: int):
        text = await loop.run_in_executor(None, transcribe, pcm)
        # Never send a partial once its utterance has been finalised — it would arrive after the final.
        if text and uid == seg.utterance_id and not is_junk(text):
            seg.extra_ms = UNFINISHED_EXTRA_MS if UNFINISHED_TAIL.search(text) else 0
            await ws.send(json.dumps({"type": "partial", "text": text}))

    async def send_final(pcm: bytes):
        r = await loop.run_in_executor(None, transcribe_final, pcm)
        if r["text"] and not is_junk(r["text"]):
            log(f'[whisper_serve] transcript: "{r["text"]}"')
            await ws.send(json.dumps({"type": "final", **r}))
        elif r["text"]:
            log(f'[whisper_serve] dropped (hallucination): "{r["text"]}"')

    try:
        async for msg in ws:
            if isinstance(msg, (bytes, bytearray)):
                finals = seg.add(bytes(msg))
                if partial.get("uid") != seg.utterance_id:
                    partial["uid"], partial["at_ms"] = seg.utterance_id, 0
                if not finals and seg.had_speech and seg.speech_ms >= MIN_SPEECH_MS \
                        and seg.utter_ms - partial["at_ms"] >= PARTIAL_EVERY_MS \
                        and (partial["task"] is None or partial["task"].done()):
                    partial["at_ms"] = seg.utter_ms
                    partial["task"] = asyncio.ensure_future(send_partial(bytes(seg.utter), seg.utterance_id))
                for utter in finals:
                    await send_final(utter)
            else:
                try:
                    obj = json.loads(msg)
                except Exception:
                    continue
                if obj.get("type") == "end":
                    u = seg.flush()
                    if u:
                        await send_final(u)
    except websockets.ConnectionClosed:
        pass


async def main(port: int):
    async with websockets.serve(handle, "127.0.0.1", port, max_size=None):
        log(f"[whisper_serve] listening on ws://127.0.0.1:{port}/ws  (vad={'webrtc' if HAVE_VAD else 'energy'})")
        await asyncio.Future()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8123)
    ap.add_argument("--model", default="base.en")
    ap.add_argument("--device", default="cpu")           # CTranslate2 has no MPS; CPU int8 is the portable path
    ap.add_argument("--compute-type", default="int8")
    ap.add_argument("--cpu-threads", type=int, default=6)  # main speed knob for CPU decoding
    ap.add_argument("--silence-ms", type=int, default=400)  # trailing silence that ends an utterance
    args = ap.parse_args()

    SILENCE_MS_END = args.silence_ms

    log(f"[whisper_serve] loading model {args.model} ({args.device}/{args.compute_type}, {args.cpu_threads} threads)...")
    model = WhisperModel(args.model, device=args.device, compute_type=args.compute_type, cpu_threads=args.cpu_threads)
    # Warm up so the first real utterance isn't a cold-start (graph/kernels get compiled here instead).
    try:
        list(model.transcribe(np.zeros(SAMPLE_RATE, dtype=np.float32), language="en", beam_size=1)[0])
    except Exception:
        pass
    log(f"[whisper_serve] model ready (silence gate {SILENCE_MS_END}ms).")
    asyncio.run(main(args.port))
