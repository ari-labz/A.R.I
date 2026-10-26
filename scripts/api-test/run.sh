#!/bin/bash
# Builds a throwaway copy of ARI, starts it through the Console in development mode (so Engram never
# writes to a brain), runs the API tests against it, and stops it again.
#
#   scripts/api-test/run.sh              build, start, test, stop
#   scripts/api-test/run.sh --keep       leave the server running afterwards
#   scripts/api-test/run.sh --no-build   skip the build
#
# Everything lives in $ARI_TEST_DIR (default /tmp/ari-api-test): the build, the data folder and the
# stand-in model server. Your real ARI data is never touched.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
SERVER="$(cd "$HERE/../.." && pwd)"
T="${ARI_TEST_DIR:-/tmp/ari-api-test}"
PORT="${ARI_TEST_PORT:-5199}"
MODEL_PORT=8199
KEEP=0; BUILD=1
for a in "$@"; do case "$a" in --keep) KEEP=1;; --no-build) BUILD=0;; esac; done

stop_server() {
  if [ -f "$T/console.pid" ]; then
    PID="$(cat "$T/console.pid")"
    kill "$PID" 2>/dev/null
    for _ in $(seq 1 30); do kill -0 "$PID" 2>/dev/null || break; sleep 0.5; done
    kill -9 "$PID" 2>/dev/null
    rm -f "$T/console.pid"
  fi
  pkill -f "$T/bin/fake-llama" 2>/dev/null
  pkill -f "fake-llama-server.py --port $MODEL_PORT" 2>/dev/null
  true
}
stop_server

if [ "$BUILD" = 1 ]; then
  mkdir -p "$T/build"
  dotnet build "$SERVER/ARI.Core/ARI.Core.csproj" -c Debug -p:AppInstallRoot="$T/build" 2>&1 | grep -E "error|Error\(s\)" | grep -v "0 Error" && { echo "BUILD FAILED"; exit 2; }
fi

# ── fresh data folder ─────────────────────────────────────────────────────────
rm -rf "$T/data"; mkdir -p "$T/data/Server" "$T/data/Models/fake" "$T/bin"
: > "$T/data/Models/fake/fake.gguf"
cat > "$T/bin/llama-server" <<WRAP
#!/bin/bash
exec python3 "$HERE/fake-llama-server.py" "\$@"
WRAP
chmod +x "$T/bin/llama-server"

python3 - "$SERVER" "$T" "$PORT" "$MODEL_PORT" <<'PY'
import json, sys, uuid
server, t, port, model_port = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
d = f"{t}/data/Server"
cfg = json.load(open(f"{server}/AppDataDefaults/AriConfig.json"))
cfg["Modules"]["API"]["Port"] = port
cfg["Modules"]["LLM"]["Enabled"] = True
cfg["Modules"]["Brain"]["Enabled"] = True
cfg["Modules"]["Calendar"]["Enabled"] = True
json.dump(cfg, open(f"{d}/AriConfig.json", "w"), indent=2)
json.dump({"Servers": [{
    "id": str(uuid.uuid4()), "name": "Test Server", "endpoint": "http://127.0.0.1", "port": model_port,
    "kvCache": 8192, "kvCacheQuantK": 4, "kvCacheQuantV": 4,
    "slots": [{"id": str(uuid.uuid4()), "name": "Default", "contextLimit": 8192}],
    "currentModelName": "fake", "bootStartup": True}]}, open(f"{d}/Servers.json", "w"), indent=2)
json.dump({"Models": [{"name": "fake", "path": "fake/fake.gguf", "downloadLink": ""}], "ModelNotes": {}}, open(f"{d}/Models.json", "w"), indent=2)
agents = json.load(open(f"{server}/AppDataDefaults/Agents.json"))
for a in agents["Agents"]:
    a["ServerName"] = "Test Server"; a["SlotName"] = "Default"
json.dump(agents, open(f"{d}/Agents.json", "w"), indent=2)
PY

# ── start through the Console ────────────────────────────────────────────────
cd "$SERVER/ARI.Console" || exit 2
( NODE_ENV=development ARI_DEV_SERVER="$T/build" APP_DATA_ROOT="$T/data" MODELS_PATH="$T/data/Models" \
  ARI_DEVMODE=1 PATH="$T/bin:$PATH" ./node_modules/.bin/electron . > "$T/console.out" 2>&1 & echo $! > "$T/console.pid" )

echo "Waiting for ARI on port $PORT..."
READY=0
for _ in $(seq 1 120); do
  if grep -qs "ARI is ready" "$T"/data/Server/Logs/SystemLogs/ARI_*.log 2>/dev/null; then READY=1; break; fi
  sleep 1
done
if [ "$READY" != 1 ]; then echo "ARI did not start. Last log lines:"; tail -30 "$T"/data/Server/Logs/SystemLogs/ARI_*.log 2>/dev/null; tail -20 "$T/console.out"; stop_server; exit 3; fi

# ── tests ────────────────────────────────────────────────────────────────────
ARI_TEST_URL="http://localhost:$PORT" ARI_TEST_DATA="$T/data" node "$HERE/tests.mjs"
RESULT=$?

if [ "$KEEP" = 1 ]; then echo "Left running at http://localhost:$PORT (stop with: kill \$(cat $T/console.pid))"; else stop_server; fi
exit $RESULT
