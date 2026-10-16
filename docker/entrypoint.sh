#!/bin/sh
# Container entrypoint: make sure the data volume is writable, then run ARI as an unprivileged user.
# PUID/PGID let a bind-mounted /data (instead of the default named volume) belong to a host user.
set -eu

PUID="${PUID:-10001}"
PGID="${PGID:-10001}"
DATA="${APP_DATA_ROOT:-/data}"

MODELS="${MODELS_PATH:-$DATA/Models}"
mkdir -p "$DATA" "$MODELS"

if [ "$(id -u)" = "0" ]; then
    # Only walk the whole tree when ownership is actually wrong, so a multi-hundred-GB models folder
    # doesn't get re-chowned on every start. (Checked before the folders created above are handed over.)
    if [ "$(stat -c '%u:%g' "$DATA")" != "$PUID:$PGID" ]; then
        echo "[entrypoint] Setting ownership of $DATA to $PUID:$PGID (first start or changed PUID/PGID)…"
        chown -R "$PUID:$PGID" "$DATA"
    fi
    chown "$PUID:$PGID" "$DATA" "$MODELS"
    export HOME="$DATA"
    exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups "$@"
fi

exec "$@"
