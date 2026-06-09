#!/usr/bin/env bash
# ARI.App bootstrap — installs dependencies and launches the desktop client.
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UI_DIR="$(cd "$SCRIPT_DIR/../ARI.UI" && pwd)"

log() { echo "[ARI.App] $*"; }

# ── 1. Bun ────────────────────────────────────────────────────────────────────
if ! command -v bun &>/dev/null && [ ! -f "$HOME/.bun/bin/bun" ]; then
  log "Bun not found — installing..."
  curl -fsSL https://bun.sh/install | bash
fi
export PATH="$HOME/.bun/bin:$PATH"

# ── 2. ARI.UI dependencies ────────────────────────────────────────────────────
log "Installing ARI.UI dependencies..."
bun install --cwd "$UI_DIR"

# ── 3. ARI.App dependencies ───────────────────────────────────────────────────
log "Installing ARI.App dependencies..."
bun install --cwd "$SCRIPT_DIR"

# ── 4. Build ARI.UI ───────────────────────────────────────────────────────────
log "Building ARI.UI..."
(cd "$UI_DIR" && bun run build)
mkdir -p "$SCRIPT_DIR/ui"
cp -r "$UI_DIR/dist/." "$SCRIPT_DIR/ui/"

# ── 5. Wait for ARI to be ready ───────────────────────────────────────────────
ARI_PORT="${ARI_BASE_URL##*:}"
ARI_PORT="${ARI_PORT%%/*}"
ARI_PORT="${ARI_PORT:-5074}"

log "Waiting for ARI on port $ARI_PORT..."
until curl -sf "http://localhost:$ARI_PORT/api/threads" -o /dev/null 2>/dev/null; do
  sleep 1
done
log "ARI is online."

# ── 6. Launch ─────────────────────────────────────────────────────────────────
log "Starting ARI.App..."
cd "$SCRIPT_DIR"
exec bunx electron .
