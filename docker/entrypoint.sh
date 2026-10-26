#!/bin/sh
# Container entrypoint: make sure the data volume is writable, then run ARI as an unprivileged user.
# PUID/PGID let a bind-mounted /data (instead of the default named volume) belong to a host user.
set -eu

# UMASK decides who can touch what ARI creates. 022 (the default) = only the ARI user can write; 000 = every file
# and folder ARI creates is readable and writable by everyone, which is what you want when /data is shared over SMB.
UMASK="${UMASK:-022}"
umask "$UMASK"

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
    # Folders Docker made for the bind mounts (and anything already inside them) predate the umask, so open them up
    # once, at the same moment ownership is fixed.
    if [ "$UMASK" = "000" ]; then
        chmod 0777 "$DATA" "$MODELS"
        [ -d /projects ] && chmod 0777 /projects
        if [ ! -e "$DATA/.ari-opened" ]; then
            chmod -R a+rwX "$DATA" 2>/dev/null || true
            [ -d /projects ] && chmod -R a+rwX /projects 2>/dev/null || true
            touch "$DATA/.ari-opened" && chmod 0666 "$DATA/.ari-opened"
        fi
    fi
    export HOME="$DATA"
    exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups "$@"
fi

exec "$@"
