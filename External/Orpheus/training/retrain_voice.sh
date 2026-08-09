#!/usr/bin/env bash
# Rebuild an Orpheus voice from its recordings: encode -> LoRA fine-tune -> GGUF.
#
# Voices built before the per-slot SNAC token banding landed have to go through
# this again. Their dataset.jsonl holds the old flat encoding, which no amount
# of extra training can rescue: every slot of a frame was written into codebook
# band 0, so the fine-tune fights the audio vocabulary orpheus-3b was
# pretrained with and never learns to voice the text it is given.
#
#   ./retrain_voice.sh "Schwi v1"
#
# Voices live in <AppData>/Server/Voices/Orpheus/<name>/ with training_data/
# holding the .wav + .txt pairs.
set -euo pipefail

VOICE="${1:-}"
if [ -z "$VOICE" ]; then
  echo "usage: $0 <voice-name>   (e.g. \"Schwi v1\")" >&2
  exit 1
fi

HERE="$(cd "$(dirname "$0")" && pwd)"
VOICE_DIR="${ARI_APPDATA:-$HOME/ARI}/Server/Voices/Orpheus/$VOICE"
AUDIO_DIR="$VOICE_DIR/training_data"
DATASET="$VOICE_DIR/dataset.jsonl"
LORA_DIR="$VOICE_DIR/lora"
GGUF="$VOICE_DIR/$VOICE.gguf"
EPOCHS="${EPOCHS:-3}"

# The training venv is separate from the inference one — it carries transformers
# and peft, which serve.py has no use for.
PY="$HERE/venv/bin/python"
if [ ! -x "$PY" ]; then
  echo "[retrain] Creating training venv..."
  python3 -m venv "$HERE/venv"
  PY="$HERE/venv/bin/python"
fi

# Check up front rather than failing an hour into the run.
if ! "$PY" -c "import transformers, peft, snac, torch" 2>/dev/null; then
  echo "[retrain] Installing training dependencies (this downloads a few GB)..."
  "$PY" -m pip install -q --upgrade pip
  "$PY" -m pip install -q -r "$HERE/requirements.txt"
fi

if [ ! -d "$AUDIO_DIR" ]; then
  echo "No recordings at $AUDIO_DIR" >&2
  exit 1
fi

echo "[retrain] Voice:      $VOICE"
echo "[retrain] Recordings: $AUDIO_DIR"
echo

echo "[retrain] 1/3 Encoding audio into SNAC tokens..."
"$PY" "$HERE/prepare_dataset.py" --audio-dir "$AUDIO_DIR" --voice "$VOICE" --output "$DATASET"

echo "[retrain] 2/3 Fine-tuning ($EPOCHS epochs)..."
"$PY" "$HERE/train.py" --dataset "$DATASET" --output "$LORA_DIR" --epochs "$EPOCHS"

echo "[retrain] 3/3 Merging LoRA and exporting GGUF..."
"$PY" "$HERE/export_gguf.py" --lora "$LORA_DIR" --output "$GGUF"

echo
echo "[retrain] Done — $GGUF"
echo "[retrain] Restart Ari, then switch to this voice on the Voice Settings tab."
