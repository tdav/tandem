"""Writes the fixtures no archiver makes on purpose. Usage: make-crafted.py <fixtures dir> <common tree dir>.

cp866.zip  - one entry, "привет.txt", its name in CP866 without the UTF-8 flag (bit 11), as old DOS/Windows
             archivers wrote it.
slip.zip   - entries whose names climb out of the destination or are absolute: ../../evil.txt, /abs.txt,
             a/../../b.txt and dir\\win.txt (a backslash, not a separator, inside the name).
trap.tar   - a symbolic link "link" -> /tmp, then a regular file "link/file.txt" under it.
"""
import calendar
import io
import os
import sys
import tarfile
import zipfile

out, tree = sys.argv[1], sys.argv[2]
stamp = (2024, 1, 2, 3, 4, 4)  # UTC; zip times have a two-second step
mtime = calendar.timegm(stamp + (0, 0, 0))


def zip_info(name):
    info = zipfile.ZipInfo(name, date_time=stamp)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # Unix
    info.external_attr = 0o100644 << 16
    return info


# cp866.zip: written with an ASCII placeholder of the same byte length, which is then replaced in the local header
# and the central directory (the name is not covered by any checksum).
real = "привет.txt".encode("cp866")
placeholder = b"X" * (len(real) - 4) + b".txt"
assert len(placeholder) == len(real)
buffer = io.BytesIO()
with zipfile.ZipFile(buffer, "w") as archive:
    with open(os.path.join(tree, "привет.txt"), "rb") as source:
        archive.writestr(zip_info(placeholder.decode("ascii")), source.read())
data = buffer.getvalue()
assert data.count(placeholder) == 2
with open(os.path.join(out, "cp866.zip"), "wb") as target:
    target.write(data.replace(placeholder, real))

# slip.zip
with zipfile.ZipFile(os.path.join(out, "slip.zip"), "w") as archive:
    for name in ["../../evil.txt", "/abs.txt", "a/../../b.txt", "dir\\win.txt"]:
        archive.writestr(zip_info(name), ("from " + name + "\n").encode("utf-8"))
with open(os.path.join(out, "slip.zip"), "rb") as written:
    raw = written.read()
    for name in ["../../evil.txt", "/abs.txt", "a/../../b.txt", "dir\\win.txt"]:
        assert raw.count(name.encode("ascii")) == 2, name  # stored as given in both headers

# trap.tar
with tarfile.open(os.path.join(out, "trap.tar"), "w", format=tarfile.USTAR_FORMAT) as archive:
    link = tarfile.TarInfo("link")
    link.type = tarfile.SYMTYPE
    link.linkname = "/tmp"
    link.mode = 0o755
    link.mtime = mtime
    archive.addfile(link)
    body = b"under the link\n"
    file = tarfile.TarInfo("link/file.txt")
    file.size = len(body)
    file.mode = 0o644
    file.mtime = mtime
    archive.addfile(file, io.BytesIO(body))
