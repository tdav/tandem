#!/bin/sh
# Rebuilds the archive fixtures in this folder with the tools that ship with macOS: /usr/bin/zip, /usr/bin/bsdtar
# (libarchive) and /usr/bin/python3 (make-crafted.py, for the archives no tool writes on purpose). The fixtures are
# committed; this script is only for reproducing them. Fixtures that cannot be made here are described in
# tests/fixtures/external/README.md.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
export COPYFILE_DISABLE=1 # no AppleDouble "._" files in tar archives

# The common tree, every entry dated 2024-01-02 03:04:05 local time.
tree="$work/tree"
mkdir -p "$tree/docs/sub" "$tree/empty"
printf 'a' > "$tree/a.txt"
printf 'hello\n' > "$tree/docs/readme.txt"
python3 -I -c 'import sys; sys.stdout.buffer.write(bytes(range(256)))' > "$tree/docs/sub/data.bin"
printf 'привет\n' > "$tree/привет.txt"
find "$tree" -exec touch -h -t 202401020304.05 {} +
entries='a.txt docs docs/readme.txt docs/sub docs/sub/data.bin empty привет.txt'

cd "$here"
rm -f a.txt sample.zip sample.tar sample.tar.gz sample.tar.bz2 sample.tar.xz sample.7z solid.7z \
      encrypted.zip cp866.zip slip.zip trap.tar broken.zip

(cd "$tree" && /usr/bin/zip -q -X -D "$here/sample.zip" a.txt docs/readme.txt docs/sub/data.bin привет.txt &&
               /usr/bin/zip -q -X "$here/sample.zip" docs/ docs/sub/ empty/)
(cd "$tree" && /usr/bin/zip -q -X -P secret "$here/encrypted.zip" a.txt docs/readme.txt docs/sub/data.bin привет.txt)

tar_options='--format ustar --no-mac-metadata --no-xattrs --no-acls --no-fflags --uid 0 --gid 0 --uname root --gname wheel'
# shellcheck disable=SC2086
(cd "$tree" && /usr/bin/bsdtar -cf "$here/sample.tar" $tar_options -n $entries &&
               /usr/bin/bsdtar -czf "$here/sample.tar.gz" $tar_options -n $entries &&
               /usr/bin/bsdtar -cjf "$here/sample.tar.bz2" $tar_options -n $entries &&
               /usr/bin/bsdtar -cJf "$here/sample.tar.xz" $tar_options -n $entries)

# 7z: stored entries give a non-solid archive; compressed ones are written as one solid block.
# shellcheck disable=SC2086
(cd "$tree" && /usr/bin/bsdtar -cf "$here/sample.7z" --format 7zip --options 7zip:compression=store -n $entries &&
               /usr/bin/bsdtar -cf "$here/solid.7z" --format 7zip -n $entries)

/usr/bin/python3 -I "$here/make-crafted.py" "$here" "$tree"

# A file that is no archive.
cp "$tree/a.txt" a.txt

# The first half of sample.zip: local headers without the central directory.
size=$(wc -c < sample.zip)
head -c $((size / 2)) sample.zip > broken.zip
