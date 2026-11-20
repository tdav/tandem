#!/usr/bin/env bash
# Builds dist/Tandem.app: a self-contained Release publish of src/Tandem.App for osx-arm64, wrapped in an app bundle
# and code-signed.
#
# Signing: if the login keychain holds a code-signing identity named exactly "Tandem Local Dev" (created once by
# build/create-dev-cert.sh), the bundle is signed with it, and folder permissions granted to Tandem survive rebuilds.
# Otherwise the bundle is signed ad-hoc; it runs the same, but macOS asks for folder permissions again after every
# rebuild.
#
# Every run removes and recreates publish/Tandem.App and dist/Tandem.app. Nothing outside the repository is changed.
#
# Usage: build/make-app.sh            (from any directory)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Tandem.App/Tandem.App.csproj"
PUBLISH_DIR="$ROOT/publish/Tandem.App"
APP="$ROOT/dist/Tandem.app"
IDENTITY_NAME="Tandem Local Dev"

step() { printf '\n==> %s\n' "$1"; }

step "Cleaning $PUBLISH_DIR and $APP"
rm -rf "$PUBLISH_DIR" "$APP"

# No trimming and no single-file (the UI relies on reflection; correctness over size). ReadyToRun precompiles the
# app to native code for fast start-up while keeping the JIT as a fallback; full NativeAOT is not used because
# Avalonia and the view models rely on reflection. No debug symbols.
step "Publishing src/Tandem.App (Release, osx-arm64, self-contained)"
dotnet publish "$PROJECT" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishTrimmed=false \
    -p:PublishSingleFile=false \
    -p:PublishReadyToRun=true \
    -p:TieredPGO=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$PUBLISH_DIR"

step "Assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$ROOT/build/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/build/Tandem.icns" "$APP/Contents/Resources/Tandem.icns"
# ditto keeps permissions and symlinks; the trailing slashes copy the folder's contents.
ditto "$PUBLISH_DIR/" "$APP/Contents/MacOS/"
find "$APP" -name '*.pdb' -delete
chmod +x "$APP/Contents/MacOS/Tandem.App"
xattr -cr "$APP"   # stray extended attributes (e.g. Finder info) make codesign refuse the bundle

step "Choosing a signing identity"
if security find-identity -v -p codesigning 2>/dev/null | grep -Fq "\"$IDENTITY_NAME\""; then
    SIGN_IDENTITY="$IDENTITY_NAME"
    echo "Signing with the local certificate \"$IDENTITY_NAME\"."
else
    SIGN_IDENTITY="-"
    cat <<EOF
WARNING: no code-signing identity "$IDENTITY_NAME" was found in the keychain, so the app is signed ad-hoc.
         Tandem works normally, but macOS ties folder permissions (Desktop, Documents, Downloads, removable and
         network volumes) to the signature: after every rebuild macOS will ask for those permissions again.
         To keep them across rebuilds, run build/create-dev-cert.sh once, then run this script again.
EOF
fi

# Inside-out: every file in Contents/MacOS first, then the bundle itself. codesign treats everything in
# Contents/MacOS as nested code, including the managed .dll files and the ru/ satellite assembly; files that are
# not Mach-O get their signature in extended attributes. codesign --deep is not used for signing.
step "Signing nested files in Contents/MacOS"
nested=0
while IFS= read -r -d '' file; do
    [[ "$file" == "$APP/Contents/MacOS/Tandem.App" ]] && continue
    # Quiet on success (each pre-signed dylib would print "replacing existing signature"); loud on failure.
    if ! out="$(codesign --force --timestamp=none --sign "$SIGN_IDENTITY" "$file" 2>&1)"; then
        printf '%s\n' "$out" >&2
        echo "ERROR: could not sign $file" >&2
        exit 1
    fi
    nested=$((nested + 1))
done < <(find "$APP/Contents/MacOS" -type f -print0)
echo "Signed $nested nested file(s)."

step "Signing the bundle"
codesign --force --timestamp=none --sign "$SIGN_IDENTITY" "$APP"

step "Checking the signature"
codesign --verify --deep --strict "$APP"
echo "codesign --verify --deep --strict: OK"

step "Done"
echo "Built $APP"
echo "Check it with build/verify-app.sh; start it with: open \"$APP\""
