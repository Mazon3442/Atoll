#!/usr/bin/env bash
set -euo pipefail
version=$1
rid=$2
publish=$3
packages=$4
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 1
[[ "$rid" == osx-x64 || "$rid" == osx-arm64 ]] || exit 1
root=$(cd "$(dirname "$0")/.." && pwd)
stage="$packages/../mac-stage"
app="$stage/ATOLL.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -a "$publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/Atoll" "$app/Contents/MacOS/Atoll.ClaudeHook"
iconset="$packages/../Atoll.iconset"
mkdir "$iconset"
for size in 16 32 128 256 512; do
    sips -s format png -z "$size" "$size" "$root/packaging/identity/Assets/Square150x150Logo.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -s format png -z "$double" "$double" "$root/packaging/identity/Assets/Square150x150Logo.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Atoll.icns"
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.mazon3442.atoll</string>
<key>CFBundleName</key><string>ATOLL</string>
<key>CFBundleDisplayName</key><string>ATOLL</string>
<key>CFBundleExecutable</key><string>Atoll</string>
<key>CFBundleIconFile</key><string>Atoll.icns</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$version</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>LSUIElement</key><true/>
<key>NSHighResolutionCapable</key><true/>
<key>NSAppleEventsUsageDescription</key><string>ATOLL reads and controls playback in Music or Spotify when media is enabled.</string>
</dict></plist>
EOF
plutil -lint "$app/Contents/Info.plist"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
ln -s /Applications "$stage/Applications"
hdiutil create -volname ATOLL -srcfolder "$stage" -ov -format UDZO "$packages/Atoll-$version-$rid.dmg"
ditto -c -k --sequesterRsrc --keepParent "$app" "$packages/Atoll-$version-$rid.zip"
