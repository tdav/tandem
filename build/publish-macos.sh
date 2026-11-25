#!/usr/bin/env bash
# Publishes Tandem for macOS as Native AOT and assembles Tandem.app bundles, each also packed into a zip:
#
#   arm64       dist/macos-arm64/Tandem.app      dist/Tandem-<version>-macOS-arm64.zip
#   x64         dist/macos-x64/Tandem.app        dist/Tandem-<version>-macOS-x64.zip
#   universal   dist/macos-universal/Tandem.app  dist/Tandem-<version>-macOS-universal.zip   (default)
#   all         all three
#
# "universal" publishes osx-arm64 and osx-x64 and joins them with lipo into one bundle that runs natively on Apple
# silicon and Intel Macs. The executable is compiled ahead of time: no .NET runtime and no JIT inside the bundle,
# only Tandem.App and the native Avalonia/Skia/HarfBuzz libraries.
#
# Signing: the "Tandem Local Dev" identity if the keychain has it (see build/create-dev-cert.sh), otherwise ad-hoc
# (codesign --sign -). SIGN_IDENTITY=<name or -> overrides the choice. The bundles are not notarized.
#
# Needs macOS with the .NET 11 SDK and the Xcode command line tools (clang, lipo, codesign: xcode-select --install).
# Native AOT does not cross-compile between operating systems, so this cannot run on Windows or Linux; on an Apple
# silicon Mac it builds the Intel slice too, and the other way round.
#
# Every run removes and recreates publish/aot-osx-<arch>, the chosen dist/macos-*/Tandem.app and their zips.
# Nothing outside the repository is changed. build/make-aot-app.sh (arm64 only, into dist/Tandem.app) still works.
#
# Usage: build/publish-macos.sh [arm64|x64|universal|all]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Tandem.App/Tandem.App.csproj"
EXE_NAME="Tandem.App"
IDENTITY_NAME="Tandem Local Dev"

step() { printf '\n==> %s\n' "$1"; }
die() { printf 'error: %s\n' "$1" >&2; exit 1; }

usage() {
    sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed -e '$d' -e 's/^# \{0,1\}//'
}

TARGET="${1:-universal}"
case "$TARGET" in
    arm64) ARCHS=(arm64); BUNDLES=(arm64) ;;
    x64) ARCHS=(x64); BUNDLES=(x64) ;;
    universal) ARCHS=(arm64 x64); BUNDLES=(universal) ;;
    all) ARCHS=(arm64 x64); BUNDLES=(arm64 x64 universal) ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown target '$TARGET' (expected arm64, x64, universal or all)" ;;
esac

# --- Prerequisites ----------------------------------------------------------------------------------------------
if [[ "$(uname -s)" != "Darwin" ]]; then
    die "this script runs only on macOS (found $(uname -s)). Native AOT compiles with the host's toolchain and
       cannot cross-compile a macOS app from another OS. On Windows use build\\publish-windows.bat."
fi
command -v dotnet >/dev/null 2>&1 || die "'dotnet' not found in PATH. Install the .NET 11 SDK: https://dot.net/download"
xcrun --find clang >/dev/null 2>&1 || die "clang not found. Install the Xcode command line tools: xcode-select --install"
for tool in lipo codesign ditto /usr/libexec/PlistBuddy; do
    command -v "$tool" >/dev/null 2>&1 || die "'$tool' not found. Install the Xcode command line tools: xcode-select --install"
done
echo "==> .NET SDK $(dotnet --version), host $(uname -m)"

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$ROOT/build/Info.plist")"

if [[ -n "${SIGN_IDENTITY:-}" ]]; then
    echo "Signing identity from SIGN_IDENTITY: $SIGN_IDENTITY"
elif security find-identity -v -p codesigning 2>/dev/null | grep -Fq "\"$IDENTITY_NAME\""; then
    SIGN_IDENTITY="$IDENTITY_NAME"
    echo "Signing identity: $SIGN_IDENTITY"
else
    SIGN_IDENTITY="-"
    echo "No \"$IDENTITY_NAME\" identity in the keychain: signing ad-hoc."
fi

publish_dir() { printf '%s/publish/aot-osx-%s' "$ROOT" "$1"; }

# lipo calls the Intel slice x86_64, .NET calls it x64.
lipo_arch() { if [[ "$1" == "x64" ]]; then echo x86_64; else echo "$1"; fi; }

# --- Publish ----------------------------------------------------------------------------------------------------
# publish <arm64|x64>
publish() {
    local arch="$1" out started
    out="$(publish_dir "$arch")"
    step "Publishing src/Tandem.App (Release, osx-$arch, Native AOT) to $out"
    rm -rf "$out"
    started=$SECONDS
    dotnet publish "$PROJECT" \
        -c Release \
        -r "osx-$arch" \
        --self-contained true \
        -p:PublishAot=true \
        -p:DebugType=none \
        -p:DebugSymbols=false \
        -o "$out"
    [[ -f "$out/$EXE_NAME" ]] || die "$out/$EXE_NAME was not produced"
    # Native AOT leaves no managed assemblies; a Tandem.App.dll means the build fell back to an apphost.
    [[ ! -f "$out/$EXE_NAME.dll" ]] || die "$out contains $EXE_NAME.dll: this is not a Native AOT build"
    echo "osx-$arch published in $((SECONDS - started)) s"
}

# --- Bundle -----------------------------------------------------------------------------------------------------
# new_bundle <app> : the bundle skeleton with Info.plist, icon and localized strings, but no binaries yet.
new_bundle() {
    local app="$1"
    rm -rf "$app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/ru.lproj"
    cp "$ROOT/build/Info.plist" "$app/Contents/Info.plist"
    cp "$ROOT/build/Tandem.icns" "$app/Contents/Resources/Tandem.icns"
    cp "$ROOT/build/ru.lproj/InfoPlist.strings" "$app/Contents/Resources/ru.lproj/InfoPlist.strings"
}

# fill_single <app> <arch> : the binaries of one architecture.
fill_single() {
    local app="$1" src
    src="$(publish_dir "$2")"
    cp "$src/$EXE_NAME" "$app/Contents/MacOS/$EXE_NAME"
    cp "$src"/*.dylib "$app/Contents/MacOS/"
}

# fill_universal <app> : the arm64 and x64 binaries joined with lipo.
fill_universal() {
    local app="$1" arm x64 file name archs
    arm="$(publish_dir arm64)"
    x64="$(publish_dir x64)"
    lipo -create "$arm/$EXE_NAME" "$x64/$EXE_NAME" -output "$app/Contents/MacOS/$EXE_NAME"
    for file in "$arm"/*.dylib; do
        name="$(basename "$file")"
        [[ -f "$x64/$name" ]] || die "$name is in the arm64 publish but not in the x64 one"
        archs="$(lipo -archs "$file")"
        if [[ " $archs " == *" arm64 "* && " $archs " == *" x86_64 "* ]]; then
            # NuGet often ships these libraries universal already; lipo refuses to join two identical fat files.
            cp "$file" "$app/Contents/MacOS/$name"
        else
            lipo -create "$file" "$x64/$name" -output "$app/Contents/MacOS/$name"
        fi
    done
    for file in "$x64"/*.dylib; do
        [[ -f "$arm/$(basename "$file")" ]] || die "$(basename "$file") is in the x64 publish but not in the arm64 one"
    done
}

# sign_and_pack <app> <label> <expected lipo archs>
sign_and_pack() {
    local app="$1" label="$2" expected="$3" zip lib archs arch
    zip="$ROOT/dist/Tandem-$VERSION-macOS-$label.zip"
    chmod +x "$app/Contents/MacOS/$EXE_NAME"
    xattr -cr "$app"

    for lib in "$app"/Contents/MacOS/*.dylib; do
        codesign --force --timestamp=none --sign "$SIGN_IDENTITY" "$lib"
    done
    codesign --force --timestamp=none --sign "$SIGN_IDENTITY" "$app"
    codesign --verify --deep --strict "$app"
    echo "codesign --verify --deep --strict: OK"

    archs="$(lipo -archs "$app/Contents/MacOS/$EXE_NAME")"
    for arch in $expected; do
        [[ " $archs " == *" $arch "* ]] || die "$app: $EXE_NAME has architectures '$archs', expected '$expected'"
    done
    echo "lipo -archs $EXE_NAME: $archs"

    rm -f "$zip"
    ditto -c -k --keepParent "$app" "$zip"
    echo "Built $app"
    echo "      $zip ($(du -h "$zip" | cut -f1 | tr -d ' '))"
}

for arch in "${ARCHS[@]}"; do
    publish "$arch"
done

mkdir -p "$ROOT/dist"
for bundle in "${BUNDLES[@]}"; do
    app="$ROOT/dist/macos-$bundle/Tandem.app"
    step "Assembling $app"
    new_bundle "$app"
    case "$bundle" in
        universal)
            fill_universal "$app"
            sign_and_pack "$app" universal "arm64 x86_64"
            ;;
        *)
            fill_single "$app" "$bundle"
            sign_and_pack "$app" "$bundle" "$(lipo_arch "$bundle")"
            ;;
    esac
done

step "Done"
