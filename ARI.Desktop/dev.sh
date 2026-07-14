#!/usr/bin/env bash
set -e
export PATH="$HOME/.bun/bin:$PATH"

if ! command -v bun &>/dev/null; then
  echo "[ARI.Desktop] Bun not found — installing..."
  curl -fsSL https://bun.sh/install | bash
  export PATH="$HOME/.bun/bin:$PATH"
fi

echo "[ARI.Desktop] Installing dependencies..."
bun install

echo "[ARI.Desktop] Launching Electron..."
exec bun run dev
