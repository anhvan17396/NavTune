#!/bin/zsh
# Builds NavTune.app into ./build
# Set SIGN_IDENTITY="Developer ID Application: ..." to sign for public distribution.
set -euo pipefail
cd "$(dirname "$0")/.."

ICON="Resources/AppIcon.icns"
if [[ ! -f "$ICON" ]]; then
    echo "Generating app icon…"
    TMP="$(mktemp -d)"
    swift scripts/make_icon.swift "$TMP/icon.png"
    ICONSET="$TMP/AppIcon.iconset"
    mkdir -p "$ICONSET"
    for s in 16 32 128 256 512; do
        sips -z $s $s "$TMP/icon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
        sips -z $((s * 2)) $((s * 2)) "$TMP/icon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$ICON"
    rm -rf "$TMP"
fi

swift build -c release
BIN="$(swift build -c release --show-bin-path)/NavTune"

APP="build/NavTune.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/NavTune"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp "$ICON" "$APP/Contents/Resources/AppIcon.icns"
cp -R Resources/*.lproj "$APP/Contents/Resources/"

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
    codesign --force --options runtime --timestamp --sign "$SIGN_IDENTITY" "$APP"
else
    # Ad-hoc signature: fine on this Mac, other Macs will show a Gatekeeper warning.
    codesign --force --sign - "$APP"
fi

echo "Built $APP"
