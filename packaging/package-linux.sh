#!/usr/bin/env bash
set -euo pipefail
version=$1
publish=$(realpath "$2")
packages=$(realpath "$3")
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid version' >&2; exit 1; }
root=$(cd "$(dirname "$0")/.." && pwd)
for tool in dpkg-deb tar zstd; do command -v "$tool" >/dev/null || { echo "Missing packaging tool: $tool" >&2; exit 1; }; done
chmod +x "$publish/Atoll" "$publish/Atoll.ClaudeHook"
stage="$packages/../linux-stage"
mkdir "$stage"
mkdir -p "$stage/opt/atoll" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/256x256/apps"
cp -a "$publish/." "$stage/opt/atoll/"
ln -s /opt/atoll/Atoll "$stage/usr/bin/atoll"
cp "$root/packaging/atoll.desktop" "$stage/usr/share/applications/atoll.desktop"
cp "$root/packaging/identity/Assets/Square150x150Logo.png" "$stage/usr/share/icons/hicolor/256x256/apps/atoll.png"
tar -czf "$packages/Atoll-$version-linux-x64.tar.gz" -C "$publish" .
deb="$packages/../deb-stage"
mkdir "$deb"
cp -a "$stage/." "$deb/"
mkdir "$deb/DEBIAN"
cat > "$deb/DEBIAN/control" <<EOF
Package: atoll
Version: $version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Mazon3442 <noreply@github.com>
Homepage: https://github.com/Mazon3442/Atoll
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, libssl3t64 | libssl3, libicu74 | libicu70 | libicu72 | libicu76, zlib1g, libgssapi-krb5-2, libx11-6, libice6, libsm6, libfontconfig1, libxrandr2, libxi6, libxcursor1, libxext6, libxrender1, libxtst6, libxfixes3, playerctl, pulseaudio-utils
Recommends: xwayland
Suggests: gnome-screenshot, x11-xserver-utils
Description: ATOLL desktop island
 Desktop overlay for coding-agent activity, media, timers, calendars,
 weather and files. The .NET runtime is bundled.
EOF
dpkg-deb --root-owner-group --build "$deb" "$packages/atoll_${version}_amd64.deb"
size=$(du -sb "$stage" | cut -f1)
cat > "$stage/.PKGINFO" <<EOF
pkgname = atoll
pkgbase = atoll
pkgver = $version-1
pkgdesc = Desktop island for coding agents, media and daily tools
url = https://github.com/Mazon3442/Atoll
builddate = $(date +%s)
packager = Mazon3442
size = $size
arch = x86_64
depend = glibc
depend = gcc-libs
depend = openssl
depend = icu
depend = zlib
depend = krb5
depend = libx11
depend = libice
depend = libsm
depend = fontconfig
depend = libxrandr
depend = libxi
depend = libxcursor
depend = libxext
depend = libxrender
depend = libxtst
depend = libxfixes
depend = playerctl
depend = libpulse
optdepend = xorg-xwayland: run under a Wayland compositor
optdepend = gnome-screenshot: screenshot action
optdepend = xorg-xset: display-off action on X11
EOF
tar --zstd --owner=0 --group=0 -cf "$packages/atoll-$version-1-x86_64.pkg.tar.zst" -C "$stage" .PKGINFO opt usr
dpkg-deb --info "$packages/atoll_${version}_amd64.deb"
tar --zstd -tf "$packages/atoll-$version-1-x86_64.pkg.tar.zst" >/dev/null
