#!/bin/sh
set -eu
package=$1
game=$2
saves=$3
base=/opt/mirthwood-echoes
state=/var/lib/mirthwood-echoes
unit=mirthwood-echoes.service
console=mirthwood-echoes-console.service
config_hash(){
    { sha256sum "/etc/systemd/system/$unit" /etc/mirthwood-echoes.env 2>/dev/null || true; } | sha256sum | cut -d' ' -f1
}
test -f "$package/app/Mirthwood.Echoes.Server.dll"
test -f "$package/app/bundle/manifest.json"
test -f "$package/upgrade-identity.py"
test -f "$game/Mirthwood.exe"
for tool in dotnet psql curl python3 flock sudo visudo; do command -v "$tool" >/dev/null || { echo "Missing WSL prerequisite: $tool" >&2;exit 1; };done
test -f "$package/app/web-control.py"
test -x /usr/bin/dotnet || { echo "Expected .NET runtime at /usr/bin/dotnet." >&2;exit 1; }
exec 9>/run/lock/mirthwood-echoes-installer.lock
flock -x 9
if [ -f /opt/mirthwoodai/.install.lock ]; then exec 8<>/opt/mirthwoodai/.install.lock;flock -x 8;fi
old_config=$(config_hash)
# The previous release enabled two boot services. The new console exists only while its Windows window holds a pipe.
old_console="/etc/systemd/system/$console"
if [ -e "$old_console" ]; then
    grep -Fqx 'ExecStart=/usr/bin/python3 -B /opt/mirthwood-echoes/web-control.py' "$old_console" || { echo "Unrecognized Echoes console unit." >&2;exit 1; }
    systemctl stop "$unit"
    systemctl disable --now "$console"
    rm -- "$old_console"
fi
if [ -f "/etc/systemd/system/$unit" ]; then
    systemctl disable "$unit" >/dev/null
    if ! systemctl is-active --quiet "$console"; then systemctl stop "$unit";fi
fi
systemctl start postgresql
python3 "$package/upgrade-identity.py"
id mirthwood_echoes >/dev/null 2>&1 || useradd --system --user-group --create-home --home-dir "$state" --shell /bin/bash mirthwood_echoes
install -d -m 700 -o mirthwood_echoes -g mirthwood_echoes "$state"
if ! runuser -u postgres -- psql -Atqc "SELECT 1 FROM pg_roles WHERE rolname='mirthwood_echoes'" | grep -qx 1; then runuser -u postgres -- createuser mirthwood_echoes; fi
if ! runuser -u postgres -- psql -Atqc "SELECT 1 FROM pg_database WHERE datname='mirthwood_echoes'" | grep -qx 1; then runuser -u postgres -- createdb -O mirthwood_echoes mirthwood_echoes; fi
install -d "$base/releases"
id mirthwood_echoes_console >/dev/null 2>&1 || useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin mirthwood_echoes_console
digest=$(cd "$package/app" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -c1-32)
previous=$(readlink "$base/current" || true)
release="$base/releases/$digest"
if [ ! -d "$release" ]; then
    staging=$(mktemp -d "$base/releases/.staged.XXXXXX")
    trap 'if [ -n "$staging" ]; then rm -rf -- "$staging"; fi' EXIT
    cp -r "$package/app/." "$staging/"
    chmod -R u=rwX,go=rX "$staging"
    check=$(cd "$staging" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -c1-32)
    [ "$check" = "$digest" ]
    mv "$staging" "$release"
    staging=''
fi
# Quote only administrator-selected filesystem paths for systemd's EnvironmentFile grammar.
python3 - "$game" "$saves" <<'PY'
import sys
from pathlib import Path
def quote(value):
    if any(c in value for c in '\r\n\0'): raise ValueError('Invalid path')
    return '"' + value.replace('\\','\\\\').replace('"','\\"') + '"'
Path('/etc/mirthwood-echoes.env').write_text('MIRTHWOOD_ECHOES_GAME='+quote(sys.argv[1])+'\nMIRTHWOOD_ECHOES_SAVES='+quote(sys.argv[2])+'\n')
PY
cat >/etc/systemd/system/$unit <<'UNIT'
[Unit]
Description=Mirthwood: Echoes
After=postgresql.service mirthwood-echoes-console.service
Requires=postgresql.service
BindsTo=mirthwood-echoes-console.service
[Service]
User=mirthwood_echoes
Group=mirthwood_echoes
SyslogIdentifier=mirthwood-echoes
Environment=HOME=/var/lib/mirthwood-echoes
Environment=MIRTHWOOD_ECHOES_STATE=/var/lib/mirthwood-echoes
Environment=MIRTHWOOD_ECHOES_PORT=17842
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_PROCESSOR_COUNT=2
Environment=DOTNET_gcServer=0
EnvironmentFile=/etc/mirthwood-echoes.env
WorkingDirectory=/opt/mirthwood-echoes/current
ExecStart=/usr/bin/dotnet /opt/mirthwood-echoes/current/Mirthwood.Echoes.Server.dll
Restart=on-failure
RestartSec=3
TimeoutStopSec=10
UMask=0077
UNIT
permission=$(mktemp)
printf '%s\n' 'mirthwood_echoes_console ALL=(root) NOPASSWD: /usr/bin/systemctl start mirthwood-echoes.service, /usr/bin/systemctl stop mirthwood-echoes.service' >"$permission"
visudo -cf "$permission" >/dev/null
install -m 0440 "$permission" /etc/sudoers.d/mirthwood-echoes-console
rm "$permission"
activate(){
    ln -sfn "$1" "$base/.next"
    mv -Tf "$base/.next" "$base/current"
}
activate "$release"
systemctl daemon-reload
if systemctl is-active --quiet "$unit" && systemctl is-active --quiet "$console"; then
    if [ "$previous" != "$release" ] || [ "$old_config" != "$(config_hash)" ]; then systemctl try-restart "$unit";fi
else
    # Migrate storage and reconcile the game module without opening either HTTP listener.
    if ! systemd-run --quiet --wait --pipe --collect -p User=mirthwood_echoes -p EnvironmentFile=/etc/mirthwood-echoes.env \
        --setenv=HOME="$state" --setenv=MIRTHWOOD_ECHOES_STATE="$state" --setenv=DOTNET_PROCESSOR_COUNT=2 \
        /usr/bin/dotnet "$release/Mirthwood.Echoes.Server.dll" --prepare-install; then
        if [ -n "$previous" ] && [ -f "$previous/Mirthwood.Echoes.Server.dll" ]; then activate "$previous";fi
        echo "Echoes installation checks failed." >&2; exit 1
    fi
fi
rm -f /var/lib/.mirthwood-echoes-identity-upgrade
# Keep the running release and one prior release. Only installer-named hashes qualify.
if [ "$previous" != "$release" ]; then
    for old in "$base"/releases/*;do
        [ -d "$old" ] && [ "$old" != "$release" ] && [ "$old" != "$previous" ] || continue
        name=$(basename "$old")
        if echo "$name" | grep -Eq '^[0-9a-f]{32}$'; then rm -rf -- "$old";fi
    done
fi
echo "Mirthwood: Echoes is installed. Release $digest."
