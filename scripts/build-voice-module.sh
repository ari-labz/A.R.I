#!/usr/bin/env bash
set -euo pipefail

MODULE_NAME="${1:?Usage: build-voice-module.sh <ModuleName>}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

SOURCE="$REPO_ROOT/External/$MODULE_NAME"
TARGET="$HOME/ARI/Server/VoiceModules/$MODULE_NAME"

if [ ! -d "$SOURCE" ]; then
    echo "ERROR: $SOURCE does not exist."
    echo "Did you init submodules?  git submodule update --init External/$MODULE_NAME"
    exit 1
fi

echo "Building voice module: $MODULE_NAME"
echo "  Source: $SOURCE"
echo "  Target: $TARGET"

mkdir -p "$TARGET"

rsync -av --delete \
    --exclude='venv/' \
    --exclude='__pycache__/' \
    --exclude='.git/' \
    --exclude='*.pyc' \
    --exclude='.deps-installed' \
    --exclude='Data/' \
    --exclude='Models/' \
    "$SOURCE/" "$TARGET/"

echo ""
echo "Done. $MODULE_NAME copied to $TARGET"
echo "ARI will run setup.py on next launch if dependencies are missing."
