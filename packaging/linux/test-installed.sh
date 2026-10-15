#!/usr/bin/env bash
# Run inside a disposable Ubuntu VM/container with Rowfinch and Xvfb installed.
set -euo pipefail

if [[ ${1:-} == --display ]]; then
    rowfinch --demo > "$ROWFINCH_SMOKE_DIR/app.log" 2>&1 &
    app_pid=$!
    trap 'kill "$app_pid" 2>/dev/null || true; wait "$app_pid" 2>/dev/null || true' EXIT
    window_id=
    for ((attempt=0; attempt<40; attempt++)); do
        if ! kill -0 "$app_pid" 2>/dev/null; then cat "$ROWFINCH_SMOKE_DIR/app.log"; exit 1; fi
        window_id=$(xwininfo -root -tree | awk '/"Rowfinch — Your SQL workspace"/ { print $1; exit }')
        if [[ -n "$window_id" ]]; then break; fi
        sleep 0.5
    done
    if [[ -z "$window_id" ]]; then cat "$ROWFINCH_SMOKE_DIR/app.log"; printf 'No Rowfinch window appeared.\n' >&2; exit 1; fi
    xprop -id "$window_id" WM_CLASS > "$ROWFINCH_SMOKE_DIR/class.txt"
    grep -q '"Rowfinch"' "$ROWFINCH_SMOKE_DIR/class.txt" || { cat "$ROWFINCH_SMOKE_DIR/class.txt"; exit 1; }
    # X11 exposes the window title before Avalonia finishes applying its icon.
    for ((attempt=0; attempt<40; attempt++)); do
        xprop -id "$window_id" _NET_WM_ICON > "$ROWFINCH_SMOKE_DIR/icon.txt"
        if grep -q 'Icon (' "$ROWFINCH_SMOKE_DIR/icon.txt"; then break; fi
        sleep 0.1
    done
    grep -q 'Icon (' "$ROWFINCH_SMOKE_DIR/icon.txt" || { head -c 500 "$ROWFINCH_SMOKE_DIR/icon.txt"; exit 1; }
    sleep 2
    kill -0 "$app_pid"
    printf 'Packaged demo window, WM_CLASS, and icon verified.\n'
    exit 0
fi

for tool in rowfinch dpkg-query desktop-file-validate xvfb-run dbus-run-session xwininfo xprop; do
    command -v "$tool" >/dev/null || { printf 'Missing test tool: %s\n' "$tool" >&2; exit 1; }
done
test "$(dpkg-query -W -f='${Status}' rowfinch)" = 'install ok installed'
desktop-file-validate /usr/share/applications/rowfinch.desktop
test -s /usr/share/icons/hicolor/scalable/apps/rowfinch.svg
test -s /usr/share/icons/hicolor/256x256/apps/rowfinch.png
test -x /usr/lib/rowfinch/Rowfinch
test -s /usr/lib/rowfinch/libhostfxr.so
test -s /usr/lib/rowfinch/libcoreclr.so
for library in /usr/lib/rowfinch/*.so; do
    # This optional .NET LTTng diagnostic provider uses a distro-specific ABI;
    # it is not loaded for normal app, SQL, keyring, or browser authentication.
    [[ $(basename "$library") == libcoreclrtraceptprovider.so ]] && continue
    dependencies=$(ldd "$library")
    if [[ "$dependencies" == *"not found"* ]]; then
        printf 'Unresolved dependencies for %s:\n%s\n' "$library" "$dependencies" >&2
        exit 1
    fi
done
env -u DOTNET_ROOT -u DOTNET_ROOT_X64 rowfinch --version
env -u DISPLAY -u WAYLAND_DISPLAY rowfinch --help > /dev/null
(
    cd /
    # Minimal Ubuntu images can intentionally exclude /usr/share/doc via dpkg.
    md5sum --quiet --ignore-missing -c /var/lib/dpkg/info/rowfinch.md5sums
)
export ROWFINCH_SMOKE_DIR
ROWFINCH_SMOKE_DIR=$(mktemp -d)
trap 'rm -rf -- "$ROWFINCH_SMOKE_DIR"' EXIT
export XDG_CONFIG_HOME="$ROWFINCH_SMOKE_DIR/config"
export XDG_CACHE_HOME="$ROWFINCH_SMOKE_DIR/cache"
export XDG_DATA_HOME="$ROWFINCH_SMOKE_DIR/data"
export XDG_RUNTIME_DIR="$ROWFINCH_SMOKE_DIR/runtime"
mkdir -p "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME" "$XDG_DATA_HOME" "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
dbus-run-session -- xvfb-run -a -s '-screen 0 1440x1000x24' bash "$(realpath "$0")" --display
printf 'Installed-package smoke test passed.\n'
