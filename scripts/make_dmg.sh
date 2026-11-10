#!/bin/zsh
# Packages build/NavTune.app into a drag-to-install DMG.
# For public release: build with SIGN_IDENTITY set, then notarize the DMG:
#   xcrun notarytool submit build/NavTune-<ver>.dmg --keychain-profile <profile> --wait
#   xcrun stapler staple build/NavTune-<ver>.dmg
set -euo pipefail
cd "$(dirname "$0")/.."

[[ -d build/NavTune.app ]] || ./scripts/build_app.sh

VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)"
DMG="build/NavTune-$VERSION.dmg"
STAGING="$(mktemp -d)"
cp -R build/NavTune.app "$STAGING/"
ln -s /Applications "$STAGING/Applications"

rm -f "$DMG"
hdiutil create -volname "NavTune" -srcfolder "$STAGING" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGING"

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
    codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
fi

echo "Built $DMG"
