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

# Already there? Then there is nothing to do.
if security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null | grep -Fq "\"$NAME\"" \
    || security find-certificate -c "$NAME" "$KEYCHAIN" >/dev/null 2>&1; then
    echo "A certificate named \"$NAME\" already exists in $KEYCHAIN. Nothing was changed."
    echo "build/make-app.sh will sign with it if it is listed by: security find-identity -v -p codesigning"
    echo "To recreate it, remove it first:"
    echo "  security delete-identity -t -c \"$NAME\" \"$KEYCHAIN\""
    exit 0
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
    read -r -p 'Type "yes" to continue: ' answer
    if [[ "$answer" != "yes" ]]; then
        echo "Cancelled. Nothing was changed."
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
"$OPENSSL" req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -config "$WORK/cert.cnf" -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null

# A throw-away passphrase protects the temporary .p12 file only; it is deleted with the folder.
P12_PASS="$("$OPENSSL" rand -hex 16)"
"$OPENSSL" pkcs12 -export -name "$NAME" -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
    -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 \
    -passout "pass:$P12_PASS" -out "$WORK/identity.p12"

echo "==> Importing the certificate and key into $KEYCHAIN"
security import "$WORK/identity.p12" -k "$KEYCHAIN" -f pkcs12 -P "$P12_PASS" -T /usr/bin/codesign

echo "==> Trusting \"$NAME\" for code signing (user setting; macOS asks for your password)"
security add-trusted-cert -r trustRoot -p codeSign "$WORK/cert.pem"

echo
if security find-identity -v -p codesigning "$KEYCHAIN" | grep -Fq "\"$NAME\""; then
    echo "Done: \"$NAME\" is a valid code-signing identity."
    echo "Run build/make-app.sh: it will sign Tandem with this certificate."
    echo "The first time, macOS may ask whether codesign may use the key: choose \"Always Allow\"."
else
    echo "The certificate was imported but macOS does not list it as a valid signing identity yet."
    echo "Check it in Keychain Access (login -> My Certificates -> \"$NAME\" -> Trust -> Code Signing: Always Trust)."
fi
echo "To remove it: security delete-identity -t -c \"$NAME\" \"$KEYCHAIN\""
