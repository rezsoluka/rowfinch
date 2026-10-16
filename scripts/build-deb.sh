#!/usr/bin/env bash
set -euo pipefail
umask 022

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
project="$repo_root/src/Rowfinch.Desktop/Rowfinch.Desktop.csproj"
runtime=linux-x64
version=
output="$repo_root/artifacts/packages"
usage() {
    cat <<'HELP'
Build a self-contained Rowfinch .deb for Ubuntu 24.04 and newer.

Usage: scripts/build-deb.sh [--runtime linux-x64|linux-arm64] [--version X.Y.Z[-prerelease]] [--output DIRECTORY]

The default version comes from Directory.Build.props. Build with the .NET 10 SDK,
dpkg-deb, Python 3, and desktop-file-validate on Linux. No sudo is required.
HELP
}
while (($#)); do
    case "$1" in
        --runtime|--version|--output)
            if (($# < 2)); then printf 'Missing value for %s\n' "$1" >&2; exit 2; fi
            case "$1" in
                --runtime) runtime=$2 ;;
                --version) version=$2 ;;
                --output) output=$2 ;;
            esac
            shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done
case "$runtime" in
    linux-x64) architecture=amd64 ;;
    linux-arm64) architecture=arm64 ;;
    *) printf 'Unsupported runtime: %s\n' "$runtime" >&2; exit 2 ;;
esac
for tool in dotnet dpkg-deb python3 desktop-file-validate; do
    command -v "$tool" >/dev/null || { printf 'Required tool missing: %s\n' "$tool" >&2; exit 1; }
done
mkdir -p -- "$output"
output=$(cd -- "$output" && pwd)
cd "$repo_root"
if [[ -z "$version" ]]; then
    version=$(dotnet msbuild "$project" -nologo -getProperty:Version)
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$ ]]; then
    printf 'Use a version such as 0.1.0 or 0.2.0-beta.1: %s\n' "$version" >&2; exit 2
fi
# Debian sorts ~beta before the corresponding final release.
deb_version=${version/-/'~'}
desktop-file-validate "$repo_root/packaging/linux/rowfinch.desktop"
stage=$(mktemp -d "${TMPDIR:-/tmp}/rowfinch-deb.XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT
package_root="$stage/root"
publish="$package_root/usr/lib/rowfinch"
doc="$package_root/usr/share/doc/rowfinch"
mkdir -p "$publish" "$doc" "$package_root/DEBIAN"

dotnet publish "$project" -c Release -r "$runtime" --self-contained true \
    -p:Version="$version" -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -p:DebugType=None -p:DebugSymbols=false -o "$publish"

install -Dm755 "$repo_root/packaging/linux/rowfinch" "$package_root/usr/bin/rowfinch"
install -Dm644 "$repo_root/packaging/linux/rowfinch.desktop" "$package_root/usr/share/applications/rowfinch.desktop"
install -Dm644 "$repo_root/assets/rowfinch.svg" "$package_root/usr/share/icons/hicolor/scalable/apps/rowfinch.svg"
install -Dm644 "$repo_root/src/Rowfinch.Desktop/Assets/rowfinch.png" "$package_root/usr/share/icons/hicolor/256x256/apps/rowfinch.png"
gzip -n -c "$repo_root/README.md" > "$doc/README.md.gz"
gzip -n -c "$repo_root/PRIVACY.md" > "$doc/PRIVACY.md.gz"
gzip -n -c "$repo_root/packaging/linux/README.md" > "$doc/ubuntu-packaging.md.gz"
install -m644 "$repo_root/packaging/linux/copyright" "$doc/copyright"
install -m644 "$repo_root/LICENSE" "$doc/LICENSE"
python3 "$repo_root/scripts/collect-notices.py" \
    "$repo_root/src/Rowfinch.Desktop/obj/project.assets.json" "$publish/Rowfinch.runtimeconfig.json" "$runtime" "$doc"
find "$package_root/usr" -type d -exec chmod 755 {} +
find "$package_root/usr" -type f -exec chmod 644 {} +
chmod 755 "$publish/Rowfinch" "$package_root/usr/bin/rowfinch"
if [[ -f "$publish/createdump" ]]; then chmod 755 "$publish/createdump"; fi
installed_size=$(du -sk "$package_root/usr" | cut -f1)
cat > "$package_root/DEBIAN/control" <<CONTROL
Package: rowfinch
Version: $deb_version
Section: database
Priority: optional
Architecture: $architecture
Maintainer: Ernesto Rodriguez <150947391+rezsoluka@users.noreply.github.com>
Installed-Size: $installed_size
Depends: ca-certificates, libc6 (>= 2.39), libgcc-s1, libstdc++6, libgssapi-krb5-2, libicu74 | libicu76 | libicu78, libssl3t64, tzdata, zlib1g, libx11-6, libice6, libsm6, libxrandr2, libxi6, libxcursor1, libfontconfig1, libfreetype6, libgl1, libsecret-1-0, libdbus-1-3, libuuid1, libcurl4t64, libwebkit2gtk-4.1-0, fonts-dejavu-core, hicolor-icon-theme, xdg-utils
Recommends: gnome-keyring, xdg-desktop-portal, xdg-desktop-portal-gtk
Description: SQL workspace for Azure SQL Database
 Rowfinch is an Ubuntu desktop SQL client with independent query tabs,
 schema browsing, SQL definitions, result grids, CSV export, and optional
 desktop-keyring password storage. Includes its own .NET runtime.
CONTROL
(
    cd "$package_root"
    find usr -type f -print0 | LC_ALL=C sort -z | xargs -0 md5sum > DEBIAN/md5sums
)
package_name="rowfinch_${deb_version}_${architecture}.deb"
dpkg-deb --root-owner-group --uniform-compression -Zxz -z6 --build "$package_root" "$stage/$package_name"
install -m644 "$stage/$package_name" "$output/$package_name"
(
    cd "$output"
    sha256sum "$package_name" > "$package_name.sha256"
)
printf '\nPackage: %s\nChecksum: %s\n' "$output/$package_name" "$output/$package_name.sha256"
