#!/usr/bin/env bash
set -e
export PATH="$HOME/.bun/bin:$PATH"
cd "$(dirname "$0")"
bun install --frozen-lockfile 2>/dev/null || bun install
exec bun run build:all
