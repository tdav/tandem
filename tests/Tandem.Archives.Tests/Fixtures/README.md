# Archive fixtures

Made on macOS by `make-fixtures.sh` (with `make-crafted.py`) from the stock `/usr/bin/zip` (Info-ZIP 3.0, Apple),
`/usr/bin/bsdtar` (libarchive) and `/usr/bin/python3`. They are committed; run the script only to rebuild them.
Nothing here was downloaded.

The common tree, every entry dated 2024-01-02 03:04:05 local time:

| Entry | Content |
|---|---|
| `a.txt` | `a` |
| `docs/readme.txt` | `hello\n` |
| `docs/sub/data.bin` | bytes 0…255 |
| `привет.txt` | `привет\n` in UTF-8 |
| `empty/` | an empty folder |

| Fixture | What it is |
|---|---|
| `sample.zip` | The common tree. Apple's zip stores the Cyrillic name as UTF-8 **without** the UTF-8 flag (bit 11), and stores the files uncompressed. |
| `encrypted.zip` | The common tree's files (no folders), ZipCrypto, password `secret` (`zip -P`). The listing is in the clear. |
| `cp866.zip` | One entry, `привет.txt`, its name in CP866 with no UTF-8 flag. |
| `slip.zip` | `../../evil.txt`, `/abs.txt`, `a/../../b.txt` and `dir\win.txt` (a backslash inside the name), stored as given. |
| `broken.zip` | The first half of `sample.zip`, so there is no central directory. |
| `sample.tar` | The common tree, ustar, root/wheel. |
| `sample.tar.gz`, `sample.tar.bz2`, `sample.tar.xz` | The same tar, compressed. |
| `sample.7z` | The common tree, stored (`7zip:compression=store`). **Not solid.** |
| `solid.7z` | The common tree, compressed by bsdtar's default 7z writer. **Solid** (`IArchive.IsSolid`). |
| `trap.tar` | A symlink `link` → `/tmp`, then a regular file `link/file.txt`. |
| `a.txt` | Not an archive. |

The RAR fixtures and a 7z with encrypted headers cannot be made on a Mac. See `tests/fixtures/external/README.md`.
Tests that need them are skipped while they are absent.
