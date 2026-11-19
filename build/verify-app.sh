#!/usr/bin/env bash
# Checks dist/Tandem.app: bundle layout, Info.plist values, architecture, signature, bundled resources, and a
# headless start-up smoke test. Prints one PASS/FAIL/SKIPPED line per check; exits non-zero if any check failed.
#
# Read-only for the system: it never wakes the display and never changes keychain, power or display settings.
# The smoke test runs the app with HOME pointed at a fresh temporary folder, so real settings are not touched.
#
# Usage: build/verify-app.sh            (from any directory)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/dist/Tandem.app"
CONTENTS="$APP/Contents"
PLIST="$CONTENTS/Info.plist"
EXE="$CONTENTS/MacOS/Tandem.App"

failures=0
SMOKE_HOME=""
SMOKE_PID=""

pass() { printf 'PASS     %s\n' "$1"; }
fail() { printf 'FAIL     %s\n' "$1"; failures=$((failures + 1)); }
skip() { printf 'SKIPPED  %s\n' "$1"; }

cleanup() {
    if [[ -n "$SMOKE_PID" ]] && kill -0 "$SMOKE_PID" 2>/dev/null; then
        kill -TERM "$SMOKE_PID" 2>/dev/null || true
        sleep 1
        kill -KILL "$SMOKE_PID" 2>/dev/null || true
    fi
    if [[ -n "$SMOKE_HOME" && -d "$SMOKE_HOME" ]]; then
        rm -rf "$SMOKE_HOME"
    fi
}
trap cleanup EXIT

echo "Verifying $APP"

# --- Bundle structure ------------------------------------------------------------------------------------------
if [[ ! -d "$APP" ]]; then
    fail "bundle exists: $APP not found (run build/make-app.sh first)"
    echo
    echo "RESULT: FAIL ($failures check(s) failed)"
    exit 1
fi
pass "bundle exists"

for dir in "$CONTENTS/MacOS" "$CONTENTS/Resources"; do
    if [[ -d "$dir" ]]; then pass "folder ${dir#"$APP/"} exists"; else fail "folder ${dir#"$APP/"} missing"; fi
done

if [[ -f "$EXE" && -x "$EXE" ]]; then
    pass "main executable Contents/MacOS/Tandem.App exists and is executable"
else
    fail "main executable Contents/MacOS/Tandem.App missing or not executable"
fi

# --- Info.plist ------------------------------------------------------------------------------------------------
if [[ -f "$PLIST" ]] && plutil -lint "$PLIST" >/dev/null; then
    pass "plutil -lint Info.plist"
else
    fail "plutil -lint Info.plist"
fi

check_plist() { # key, expected raw value
    local key="$1" expected="$2" actual
    if actual="$(plutil -extract "$key" raw -o - "$PLIST" 2>/dev/null)" && [[ "$actual" == "$expected" ]]; then
        pass "Info.plist $key = $expected"
    else
        fail "Info.plist $key: expected '$expected', got '${actual:-<missing>}'"
    fi
}

check_plist_type() { # key, expected plutil type
    local key="$1" expected="$2" actual
    if actual="$(plutil -type "$key" "$PLIST" 2>/dev/null)" && [[ "$actual" == "$expected" ]]; then
        pass "Info.plist $key is of type $expected"
    else
        fail "Info.plist $key: expected type '$expected', got '${actual:-<missing>}'"
    fi
}

if [[ -f "$PLIST" ]]; then
    check_plist CFBundleIdentifier dev.tandem.Tandem
    check_plist CFBundleName Tandem
    check_plist CFBundleExecutable Tandem.App
    check_plist CFBundlePackageType APPL
    check_plist CFBundleShortVersionString 0.1.0
    check_plist CFBundleVersion 1
    check_plist CFBundleInfoDictionaryVersion 6.0
    check_plist CFBundleDevelopmentRegion en
    check_plist LSMinimumSystemVersion 14.0
    # Version numbers must be strings: an <integer> or <real> CFBundleVersion is read wrongly by macOS.
    check_plist_type CFBundleShortVersionString string
    check_plist_type CFBundleVersion string
    check_plist_type LSMinimumSystemVersion string
    check_plist NSHighResolutionCapable true
    check_plist_type NSHighResolutionCapable bool
    check_plist NSPrincipalClass NSApplication
    check_plist LSApplicationCategoryType public.app-category.utilities
    localizations="$(plutil -extract CFBundleLocalizations json -o - "$PLIST" 2>/dev/null || true)"
    if [[ "$localizations" == '["en","ru"]' ]]; then
        pass "Info.plist CFBundleLocalizations = [en, ru]"
    else
        fail "Info.plist CFBundleLocalizations: expected [\"en\",\"ru\"], got '${localizations:-<missing>}'"
    fi
fi

# --- Architecture ----------------------------------------------------------------------------------------------
if [[ -f "$EXE" ]] && archs="$(lipo -archs "$EXE" 2>/dev/null)" && [[ "$archs" == "arm64" ]]; then
    pass "lipo -archs Tandem.App = arm64"
else
    fail "lipo -archs Tandem.App: expected 'arm64', got '${archs:-<unreadable>}'"
fi

# --- Signature -------------------------------------------------------------------------------------------------
if codesign_out="$(codesign --verify --deep --strict --verbose=2 "$APP" 2>&1)"; then
    pass "codesign --verify --deep --strict"
else
    fail "codesign --verify --deep --strict"
    printf '%s\n' "$codesign_out" | sed 's/^/         /'
fi
authority="$(codesign -dvv "$APP" 2>&1 | grep -E '^(Authority|Signature)=' | head -n 1 || true)"
echo "info     signed with: ${authority:-unknown}"

# --- Bundled resources -----------------------------------------------------------------------------------------
if [[ -f "$CONTENTS/MacOS/Tandem.App.dll" ]]; then
    pass "main assembly Tandem.App.dll present (English resources)"
else
    fail "main assembly Tandem.App.dll missing"
fi
if [[ -f "$CONTENTS/MacOS/ru/Tandem.App.resources.dll" ]]; then
    pass "Russian resources ru/Tandem.App.resources.dll present"
else
    fail "Russian resources ru/Tandem.App.resources.dll missing"
fi

unwanted="$(find "$APP" \( -name '*.pdb' -o -name '*.Tests.dll' -o -name 'xunit*' -o -name 'testhost*' \
    -o -name 'Microsoft.TestPlatform*' -o -name 'Microsoft.VisualStudio.TestPlatform*' \) -print)"
if [[ -z "$unwanted" ]]; then
    pass "no .pdb files or test assemblies in the bundle"
else
    fail "unwanted files in the bundle:"
    printf '%s\n' "$unwanted" | sed 's/^/         /'
fi

# --- Headless start-up smoke test ------------------------------------------------------------------------------
# Avalonia's platform start-up is known to crash while the display is asleep, so that case is SKIPPED, not FAILED.
# CGDisplayIsAsleep is a read-only query; nothing here wakes the display.
display_asleep() {
    local answer
    answer="$(osascript -l JavaScript -e 'ObjC.import("CoreGraphics"); $.CGDisplayIsAsleep($.CGMainDisplayID())' \
        2>/dev/null)" || return 1
    [[ "$answer" == "1" || "$answer" == "true" ]]
}

if [[ ! -x "$EXE" ]]; then
    fail "smoke test: no executable to start"
elif display_asleep; then
    skip "smoke test (display asleep: the app cannot start its window system now; re-run with the display on)"
else
    SMOKE_HOME="$(mktemp -d "${TMPDIR:-/tmp}/tandem-smoke.XXXXXX")"
    echo "info     smoke test: starting Tandem.App with HOME=$SMOKE_HOME for 8 seconds"
    HOME="$SMOKE_HOME" CFFIXED_USER_HOME="$SMOKE_HOME" "$EXE" >"$SMOKE_HOME/stdout.txt" 2>"$SMOKE_HOME/stderr.txt" &
    SMOKE_PID=$!
    sleep 8
    log="$SMOKE_HOME/Library/Logs/Tandem/tandem.log"
    if kill -0 "$SMOKE_PID" 2>/dev/null; then
        pass "smoke test: app still running after 8 seconds"
    else
        exit_code=0
        wait "$SMOKE_PID" || exit_code=$?
        SMOKE_PID=""
        fail "smoke test: app exited within 8 seconds (exit code $exit_code)"
        tail -n 20 "$SMOKE_HOME/stderr.txt" | sed 's/^/         /'
    fi
    if [[ ! -s "$log" ]]; then
        pass "smoke test: error log absent or empty"
    else
        fail "smoke test: error log is not empty:"
        tail -n 20 "$log" | sed 's/^/         /'
    fi
    if [[ -n "$SMOKE_PID" ]]; then
        kill -TERM "$SMOKE_PID" 2>/dev/null || true
        for _ in 1 2 3 4 5; do kill -0 "$SMOKE_PID" 2>/dev/null || break; sleep 1; done
        kill -KILL "$SMOKE_PID" 2>/dev/null || true
        wait "$SMOKE_PID" 2>/dev/null || true
        SMOKE_PID=""
    fi
    rm -rf "$SMOKE_HOME"
    SMOKE_HOME=""
fi

echo
if [[ $failures -eq 0 ]]; then
    echo "RESULT: PASS"
    exit 0
fi
echo "RESULT: FAIL ($failures check(s) failed)"
exit 1
