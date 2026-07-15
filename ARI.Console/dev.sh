#!/usr/bin/env bash
set -e
export PATH="$HOME/.bun/bin:$PATH"

if ! command -v bun &>/dev/null; then
  echo "[ARI.Console] Bun not found — installing..."
  curl -fsSL https://bun.sh/install | bash
  export PATH="$HOME/.bun/bin:$PATH"
fi

# Dev launches the server through the console, so build ARI.Core first (into APP_INSTALL_ROOT).
echo "[ARI.Console] Building ARI.Core..."
( cd "$(dirname "$0")/.." && dotnet build ARI.Core/ARI.Core.csproj -c Debug --nologo -v q )

echo "[ARI.Console] Installing dependencies..."
bun install

echo "[ARI.Console] Launching Electron (dev)..."
exec bun run dev
