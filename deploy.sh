#!/bin/bash
# Developer helper: build C-3.o and install it straight onto your own Jellyfin server over SSH.
# (To install a released version, use the plugin repository instead: see README "Install".)
#
#   ./deploy.sh                    build + copy the files (takes effect at the next Jellyfin restart)
#   ./deploy.sh --restart          same, then restart Jellyfin, unless someone is watching or a
#                                  C-3.o run is in progress
#   ./deploy.sh --restart --force  restart regardless
#
# Settings come from .local/deploy.env (not in git). Copy deploy.env.example there and edit it.
# On Windows, run this from Git Bash.
set -euo pipefail
cd "$(dirname "$0")"

C3O_SSH="ssh"                              # ssh client to use
C3O_HOST=""                                # ssh destination, e.g. root@jellyfin.lan
C3O_EXEC=""                                # prefix that runs a command where Jellyfin lives, e.g. "pct exec 101 --"
C3O_PLUGINS="/var/lib/jellyfin/plugins"    # Jellyfin's plugin folder
C3O_USER="jellyfin"                        # owner of the plugin files
C3O_MEDIA="/mnt/|/media/"                  # where media lives; a file open there means playback
C3O_RESTART="systemctl restart jellyfin"   # how to restart Jellyfin

if [ -f .local/deploy.env ]; then
    # shellcheck disable=SC1091
    . .local/deploy.env
fi
[ -n "$C3O_HOST" ] || { echo "Set C3O_HOST in .local/deploy.env (see deploy.env.example)"; exit 1; }

# Runs a bash snippet on the Jellyfin machine. The snippet must not contain single quotes.
remote() { "$C3O_SSH" -o BatchMode=yes "$C3O_HOST" "$C3O_EXEC bash -c '$1'"; }

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Jellyfin.Plugin.C3o/Jellyfin.Plugin.C3o.csproj)
[ -n "$VERSION" ] || { echo "Could not read <Version> from the csproj"; exit 1; }
DIR="$C3O_PLUGINS/C-3.o_$VERSION"

dotnet build Jellyfin.Plugin.C3o -c Release -nologo -v q

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp Jellyfin.Plugin.C3o/bin/Release/net10.0/Jellyfin.Plugin.C3o.dll "$STAGE/"
# The plugin image (art/c3o.png, rendered from art/c3o.svg by art/render.ps1). meta.json points
# at it with an absolute path, the way Jellyfin itself records it for plugins from a repository.
[ -f art/c3o.png ] || { echo "art/c3o.png is missing: run art/render.ps1"; exit 1; }
cp art/c3o.png "$STAGE/"
sed -e "s/@VERSION@/$VERSION/" -e "s|@DIR@|$DIR|" -e "s/@TIMESTAMP@/$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)/" meta.template.json > "$STAGE/meta.json"
LOCAL_SUM=$(sha256sum "$STAGE/Jellyfin.Plugin.C3o.dll" | cut -d' ' -f1)

# Older versions are removed so the server never has two copies to choose from.
# --no-same-owner: an unprivileged container cannot take the local uid from the archive.
REMOTE=$(tar -C "$STAGE" -cf - Jellyfin.Plugin.C3o.dll meta.json c3o.png | remote "set -e; for d in $C3O_PLUGINS/C-3.o_*; do [ \"\$d\" = \"$DIR\" ] || rm -rf \"\$d\"; done; mkdir -p $DIR; tar --no-same-owner -C $DIR -xf -; chown -R $C3O_USER:$C3O_USER $DIR; chmod 644 $DIR/*; sha256sum $DIR/Jellyfin.Plugin.C3o.dll")

REMOTE_SUM=$(echo "$REMOTE" | cut -d' ' -f1)
if [ "$LOCAL_SUM" != "$REMOTE_SUM" ]; then
    echo "Checksum mismatch after copy (local $LOCAL_SUM, remote $REMOTE_SUM)"; exit 1
fi
echo "Installed C-3.o $VERSION to $DIR (checksum verified)."

if [ "${1:-}" = "--restart" ]; then
    # A restart stops playback and cancels a C-3.o run, so look first. A media file held open
    # means someone is watching; an unfinished LastRun in the state file means a run is in
    # progress. --force skips the check (a crashed run leaves LastRun unfinished).
    STATE="$C3O_PLUGINS/configurations/Jellyfin.Plugin.C3o/state.json"
    BUSY=$(remote "pid=\$(pgrep -x jellyfin); if [ -n \"\$pid\" ] && ls -l /proc/\$pid/fd 2>/dev/null | grep -q -E \"$C3O_MEDIA\"; then echo someone is watching; fi; if grep -q FinishedUtc.:null $STATE 2>/dev/null; then echo a C-3.o run is in progress; fi")
    if [ -n "$BUSY" ] && [ "${2:-}" != "--force" ]; then
        echo "Installed, but NOT restarted:"; echo "$BUSY"
        echo "Try again later, or use: ./deploy.sh --restart --force"
        exit 1
    fi
    remote "$C3O_RESTART"
    echo "Jellyfin restarted."
else
    echo "Restart Jellyfin for it to take effect: ./deploy.sh --restart"
fi
