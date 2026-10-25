#!/usr/bin/env bash
# macOS release: dist/ByeDPI-PC-<version>-<rid>.dmg with a .app bundle (app + engine/).
# Usage: scripts/publish-mac.sh [osx-arm64|osx-x64]
set -euo pipefail
rid="${1:-osx-arm64}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if [ ! -x "engine/$rid/ciadpi" ]; then bash scripts/fetch-engines.sh "$rid"; fi

version="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props | head -1)"
if [ -z "$version" ]; then version="1.0.0"; fi
name="ByeDPI-PC"
out="$root/dist/$name-$version-$rid"
app="$out/$name.app"
rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

echo "Publishing $rid ..."
dotnet publish src/ByeDpiPc.App -c Release -r "$rid" --self-contained true -p:DebugType=none -o "$app/Contents/MacOS" >/dev/null

if command -v sips >/dev/null && command -v iconutil >/dev/null && [ -f docs/icon.png ]; then
  iconset="$(mktemp -d)/icon.iconset"
  mkdir -p "$iconset"
  for s in 16 32 128 256 512; do
    sips -z "$s" "$s" docs/icon.png --out "$iconset/icon_${s}x${s}.png" >/dev/null 2>&1 || true
    d=$((s*2))
    sips -z "$d" "$d" docs/icon.png --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null 2>&1 || true
  done
  iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns" 2>/dev/null || true
fi

plist="$app/Contents/Info.plist"
{
  echo '<?xml version="1.0" encoding="UTF-8"?>'
  echo '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">'
  echo '<plist version="1.0"><dict>'
  echo '<key>CFBundleName</key><string>ByeDPI PC</string>'
  echo '<key>CFBundleDisplayName</key><string>ByeDPI PC</string>'
  echo '<key>CFBundleIdentifier</key><string>com.koscheev.byedpipc</string>'
  echo "<key>CFBundleVersion</key><string>$version</string>"
  echo "<key>CFBundleShortVersionString</key><string>$version</string>"
  echo '<key>CFBundleExecutable</key><string>ByeDPI-PC</string>'
  echo '<key>CFBundleIconFile</key><string>AppIcon</string>'
  echo '<key>CFBundlePackageType</key><string>APPL</string>'
  echo '<key>LSMinimumSystemVersion</key><string>12.0</string>'
  echo '<key>NSHighResolutionCapable</key><true/>'
  echo '</dict></plist>'
} > "$plist"

cp LICENSE README.md "$out/" 2>/dev/null || true
codesign --force --deep --sign - "$app" 2>/dev/null || true

dmg="$root/dist/$name-$version-$rid.dmg"
rm -f "$dmg"
ln -sf /Applications "$out/Applications"
hdiutil create -volname "$name" -srcfolder "$out" -ov -format UDZO "$dmg" >/dev/null
echo "Done: $dmg"
