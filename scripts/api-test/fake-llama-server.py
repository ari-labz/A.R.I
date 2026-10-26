#!/usr/bin/env python3
"""A stand-in for llama-server, used only by the API tests.

It speaks just enough of llama-server's HTTP interface for ARI to treat it as a real model server:
  GET  /health                 -> {"status": "ok"}
  GET  /slots                  -> []
  POST /v1/chat/completions    -> streamed (SSE) or plain JSON reply

The reply to a chat message is "Echo: <the last user message>" so a test can tell its own message
came back. A request that is not streamed (the summary agent's call) returns a short summary
with a "TITLE:" line, like the real model would.

Run as `llama-server --port N ...`; `--version` prints a version line.
"""
import json
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

if "--version" in sys.argv:
    print("version: 9999 (fake)")
    sys.exit(0)

port = 8081
for i, arg in enumerate(sys.argv):
    if arg == "--port" and i + 1 < len(sys.argv):
        port = int(sys.argv[i + 1])


def last_user_text(body):
    for message in reversed(body.get("messages", [])):
        if message.get("role") != "user":
            continue
        content = message.get("content")
        if isinstance(content, list):
            content = " ".join(part.get("text", "") for part in content if isinstance(part, dict))
        text = str(content or "")
        # ARI wraps the message as "# Prompt:\n\nname: message"; hand back just the message.
        found = re.search(r"# Prompt:\s*(?:[^:\n]{1,40}:\s*)?(.*)$", text, re.S)
        return found.group(1).strip() if found else text
    return ""


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _json(self, status, payload):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path.startswith("/health"):
            self._json(200, {"status": "ok"})
        elif self.path.startswith("/slots"):
            self._json(200, [])
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        if not self.path.startswith("/v1/chat/completions"):
            self._json(404, {"error": "not found"})
            return

        user_text = last_user_text(body)
        usage = {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}

        if not body.get("stream"):
            text = "Test summary of the conversation.\nTITLE: Test chat"
            self._json(200, {"choices": [{"index": 0, "message": {"role": "assistant", "content": text}, "finish_reason": "stop"}], "usage": usage})
            return

        # A message beginning "slow:" streams slowly so a test can stop or interject mid-reply.
        slow = user_text.startswith("slow:")
        reply = f"Echo: {user_text}"
        words = reply.split(" ")

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()

        def send(obj):
            line = ("data: " + (obj if isinstance(obj, str) else json.dumps(obj)) + "\n\n").encode()
            self.wfile.write(f"{len(line):x}\r\n".encode() + line + b"\r\n")
            self.wfile.flush()

        try:
            count = 40 if slow else len(words)
            for i in range(count):
                word = words[i] if i < len(words) else "more"
                send({"choices": [{"index": 0, "delta": {"content": (" " if i else "") + word}, "finish_reason": None}]})
                if slow:
                    time.sleep(0.25)
            send({"choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}], "usage": usage,
                  "timings": {"prompt_n": 10, "prompt_per_second": 100.0}})
            send("[DONE]")
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass


ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
