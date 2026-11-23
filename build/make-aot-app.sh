#!/usr/bin/env bash
# Builds dist/Tandem.app from a Native AOT publish of src/Tandem.App for osx-arm64 and packs it into
# dist/Tandem-<version>-macOS-arm64.zip for a release.
#
# The executable is compiled ahead of time to native code: no .NET runtime and no JIT inside the bundle, only
# Tandem.App and the native Avalonia/Skia/HarfBuzz libraries. Signing works as in build/make-app.sh: the
# "Tandem Local Dev" identity if the keychain has it, otherwise ad-hoc.
#
# Every run removes and recreates publish/aot-arm64, dist/Tandem.app and the zip. Nothing outside the repository
# is changed.
#
# Usage: build/make-aot-app.sh [version]     (version defaults to CFBundleShortVersionString from build/Info.plist)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Tandem.App/Tandem.App.csproj"
PUBLISH_DIR="$ROOT/publish/aot-arm64"
APP="$ROOT/dist/Tandem.app"
IDENTITY_NAME="Tandem Local Dev"
VERSION="${1:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$ROOT/build/Info.plist")}"
ZIP="$ROOT/dist/Tandem-$VERSION-macOS-arm64.zip"

step() { printf '\n==> %s\n' "$1"; }

step "Cleaning $PUBLISH_DIR, $APP and $ZIP"
rm -rf "$PUBLISH_DIR" "$APP" "$ZIP"

step "Publishing src/Tandem.App (Release, osx-arm64, Native AOT)"
dotnet publish "$PROJECT" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishAot=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$PUBLISH_DIR"

step "Assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/ru.lproj"
cp "$ROOT/build/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/build/Tandem.icns" "$APP/Contents/Resources/Tandem.icns"
cp "$ROOT/build/ru.lproj/InfoPlist.strings" "$APP/Contents/Resources/ru.lproj/InfoPlist.strings"
cp "$PUBLISH_DIR/Tandem.App" "$APP/Contents/MacOS/Tandem.App"
cp "$PUBLISH_DIR"/*.dylib "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/Tandem.App"
xattr -cr "$APP"

step "Choosing a signing identity"
if security find-identity -v -p codesigning 2>/dev/null | grep -Fq "\"$IDENTITY_NAME\""; then
    SIGN_IDENTITY="$IDENTITY_NAME"
else
    SIGN_IDENTITY="-"
    echo "No \"$IDENTITY_NAME\" identity in the keychain: signing ad-hoc."
fi

step "Signing"
for lib in "$APP"/Contents/MacOS/*.dylib; do
    codesign --force --timestamp=none --sign "$SIGN_IDENTITY" "$lib"
done
codesign --force --timestamp=none --sign "$SIGN_IDENTITY" "$APP"
codesign --verify --deep --strict "$APP"
echo "codesign --verify --deep --strict: OK"

step "Packing $ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

step "Done"
echo "Built $APP and $ZIP"
