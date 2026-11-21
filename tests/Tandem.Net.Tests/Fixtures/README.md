# Network test fixtures

## `keys/`

SSH private keys made on macOS by `keys/make-keys.sh` with the system's `/usr/bin/ssh-keygen`. Nothing was
downloaded and no system setting was changed. They are committed; run the script only to rebuild them (each run makes
new keys).

**These are test keys.** They exist only to test key loading, are used nowhere else and must never be added to a real
server's `authorized_keys`.

| File | Type | Format | Passphrase |
|---|---|---|---|
| `ed25519` | Ed25519 | OpenSSH | none |
| `rsa3072` | RSA 3072 | OpenSSH | none |
| `ecdsa256` | ECDSA P-256 | OpenSSH | none |
| `ed25519-pass` | Ed25519 | OpenSSH | `secret` |
| `rsa-pem` | RSA 3072 | PEM (`-m PEM`, "BEGIN RSA PRIVATE KEY") | none |

Each key has its public half beside it as `<name>.pub`.
