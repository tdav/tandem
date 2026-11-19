#!/usr/bin/env bash
# Creates a self-signed code-signing certificate "Tandem Local Dev" in YOUR LOGIN KEYCHAIN. Run it once, by hand.
#
# Why: macOS remembers folder permissions you grant to Tandem (Desktop, Documents, Downloads, removable and network
# volumes) by the app's signature. Without a stable certificate, build/make-app.sh signs ad-hoc, every rebuild has
# a new signature, and macOS asks for the permissions again. With this certificate the signature stays the same.
# Tandem works without it; this only saves the repeated questions. Stage 4 replaces it with Apple Developer
# certificates.
#
# What it adds (and nothing else):
#   1. To the login keychain: a certificate "Tandem Local Dev" (self-signed, RSA 2048, valid 10 years, usable only
#      for code signing) and its private key. Only /usr/bin/codesign is allowed to use the key without asking.
#   2. A user-level trust setting: "Tandem Local Dev" is trusted for code signing only. macOS shows a password
#      dialog for this step. It is needed so that the certificate counts as a valid signing identity.
# It never touches the System keychain, never uses sudo, and changes no other certificates or settings.
#
# How to remove it again (certificate, private key and trust setting):
#   security delete-identity -t -c "Tandem Local Dev" ~/Library/Keychains/login.keychain-db
# or in Keychain Access: login keychain -> "My Certificates" -> "Tandem Local Dev" -> Delete (removes the key too).
# After removal, build/make-app.sh signs ad-hoc again.
#
# Usage: build/create-dev-cert.sh [--yes]
#   --yes   do not ask for the typed confirmation
set -euo pipefail

NAME="Tandem Local Dev"
OPENSSL=/usr/bin/openssl   # the system LibreSSL: its PKCS#12 output is readable by `security import`

assume_yes=false
for arg in "$@"; do
    case "$arg" in
        --yes) assume_yes=true ;;
        -h|--help) sed -n '2,23p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $arg (use --yes or --help)" >&2; exit 2 ;;
    esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This script is for macOS only." >&2
    exit 1
fi
if [[ "$(id -u)" -eq 0 ]]; then
    echo "Do not run this script as root or with sudo: it must change only your own login keychain." >&2
    exit 1
fi

# The login keychain of the current user (never the System keychain).
KEYCHAIN="$(security login-keychain 2>/dev/null | sed -e 's/^[[:space:]]*"//' -e 's/"[[:space:]]*$//')"
if [[ -z "$KEYCHAIN" || ! -f "$KEYCHAIN" ]]; then
    KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"
fi
if [[ ! -f "$KEYCHAIN" ]]; then
    echo "Could not find your login keychain (looked for $KEYCHAIN). Nothing was changed." >&2
    exit 1
fi
case "$KEYCHAIN" in
    /Library/Keychains/*|/System/*) echo "Refusing to use a system keychain: $KEYCHAIN" >&2; exit 1 ;;
esac

# Whether "$NAME" is a valid code-signing identity (trusted, with its key): what build/make-app.sh looks for.
# (The list is read first, so grep -q stopping early cannot fail the pipeline under pipefail.)
is_usable() {
    local identities
    identities="$(security find-identity -v -p codesigning "$KEYCHAIN" 2>/dev/null || true)"
    grep -Fq "\"$NAME\"" <<<"$identities"
}

# Whether the keychain holds a certificate or identity named "$NAME" at all, usable or not.
exists() {
    local identities
    security find-certificate -c "$NAME" "$KEYCHAIN" >/dev/null 2>&1 && return 0
    identities="$(security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null || true)"
    grep -Fq "\"$NAME\"" <<<"$identities"
}

# How to finish or undo by hand, for a certificate that is in the keychain but cannot sign.
print_manual_steps() {
    echo "To trust it by hand: Keychain Access -> login -> My Certificates -> \"$NAME\" -> Trust ->"
    echo "  Code Signing: Always Trust. Then check: security find-identity -v -p codesigning"
    echo "To remove it (certificate, private key and trust setting):"
    echo "  security delete-identity -t -c \"$NAME\" \"$KEYCHAIN\""
}

# Already there? Then there is nothing to do, unless it cannot be used for signing.
if exists; then
    if is_usable; then
        echo "A certificate named \"$NAME\" already exists in $KEYCHAIN and is a valid code-signing identity."
        echo "Nothing was changed. build/make-app.sh signs with it."
        echo "To recreate it, remove it first:"
        echo "  security delete-identity -t -c \"$NAME\" \"$KEYCHAIN\""
        exit 0
    fi
    echo "A certificate named \"$NAME\" exists in $KEYCHAIN but is not usable for signing:" >&2
    echo "it is not listed by: security find-identity -v -p codesigning (not trusted, or its key is missing)." >&2
    echo "Nothing was changed. build/make-app.sh signs ad-hoc until this is fixed." >&2
    print_manual_steps >&2
    echo "Or remove it and run this script again." >&2
    exit 1
fi

cat <<EOF
This script will add to your login keychain ($KEYCHAIN):
  - a self-signed code-signing certificate "$NAME" (valid 10 years) and its private key;
    only /usr/bin/codesign may use the key without asking;
  - a user trust setting: "$NAME" is trusted for code signing only (macOS asks for your password).
It does not touch the System keychain, does not use sudo, and changes nothing else.

To remove it later:
  security delete-identity -t -c "$NAME" "$KEYCHAIN"
  (or Keychain Access -> login -> My Certificates -> "$NAME" -> Delete)

EOF

if [[ "$assume_yes" != true ]]; then
    # No terminal to ask on, or nothing could be read (end of input): treat it as a no.
    answer=""
    if [[ ! -t 0 ]] || ! read -r -p 'Type "yes" to continue: ' answer || [[ "$answer" != "yes" ]]; then
        echo "Cancelled. Nothing was changed." >&2
        exit 1
    fi
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/tandem-cert.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
chmod 700 "$WORK"

cat >"$WORK/cert.cnf" <<EOF
[ req ]
distinguished_name = dn
prompt             = no
x509_extensions    = ext

[ dn ]
CN = $NAME

[ ext ]
basicConstraints     = critical, CA:false
keyUsage             = critical, digitalSignature
extendedKeyUsage     = critical, codeSigning
subjectKeyIdentifier = hash
EOF

echo "==> Generating the key and the self-signed certificate (in $WORK, removed afterwards)"
# Its progress output goes to a file; on failure the whole of it, errors included, is shown.
if ! "$OPENSSL" req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -config "$WORK/cert.cnf" -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>"$WORK/req.log"; then
    cat "$WORK/req.log" >&2
    echo "Could not generate the certificate (see above). Nothing was changed." >&2
    exit 1
fi

# A throw-away passphrase protects the temporary .p12 file only; it is deleted with the folder.
P12_PASS="$("$OPENSSL" rand -hex 16)"
"$OPENSSL" pkcs12 -export -name "$NAME" -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
    -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 \
    -passout "pass:$P12_PASS" -out "$WORK/identity.p12"

echo "==> Importing the certificate and key into $KEYCHAIN"
security import "$WORK/identity.p12" -k "$KEYCHAIN" -f pkcs12 -P "$P12_PASS" -T /usr/bin/codesign

echo "==> Trusting \"$NAME\" for code signing (user setting; macOS asks for your password)"
# -k: the certificate is the one just imported into the login keychain; nothing goes to another default keychain.
if ! security add-trusted-cert -r trustRoot -p codeSign -k "$KEYCHAIN" "$WORK/cert.pem"; then
    echo >&2
    echo "The certificate \"$NAME\" and its key WERE imported into $KEYCHAIN, but setting the trust failed" >&2
    echo "(see the message above; for example the password dialog was cancelled). It is NOT trusted, so it cannot" >&2
    echo "sign yet, and build/make-app.sh keeps signing ad-hoc." >&2
    print_manual_steps >&2
    exit 1
fi

echo
if is_usable; then
    echo "Done: \"$NAME\" is a valid code-signing identity."
    echo "Run build/make-app.sh: it will sign Tandem with this certificate."
    echo "The first time, macOS may ask whether codesign may use the key: choose \"Always Allow\"."
    echo "To remove it: security delete-identity -t -c \"$NAME\" \"$KEYCHAIN\""
else
    echo "The certificate was imported and trusted, but macOS does not list it as a valid signing identity." >&2
    print_manual_steps >&2
    exit 1
fi
