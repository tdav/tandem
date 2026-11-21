#!/bin/sh
# Makes the SSH key samples the tests load. Uses only the system's /usr/bin/ssh-keygen: nothing is downloaded and no
# system setting changes. The keys are committed; run this only to rebuild them. They are test keys, used nowhere else.
set -eu
cd "$(dirname "$0")"
rm -f ed25519 ed25519.pub rsa3072 rsa3072.pub ecdsa256 ecdsa256.pub ed25519-pass ed25519-pass.pub rsa-pem rsa-pem.pub
/usr/bin/ssh-keygen -q -t ed25519 -N '' -C tandem-test-ed25519 -f ed25519
/usr/bin/ssh-keygen -q -t rsa -b 3072 -N '' -C tandem-test-rsa3072 -f rsa3072
/usr/bin/ssh-keygen -q -t ecdsa -b 256 -N '' -C tandem-test-ecdsa256 -f ecdsa256
/usr/bin/ssh-keygen -q -t ed25519 -N secret -C tandem-test-ed25519-pass -f ed25519-pass
/usr/bin/ssh-keygen -q -t rsa -b 3072 -m PEM -N '' -C tandem-test-rsa-pem -f rsa-pem
