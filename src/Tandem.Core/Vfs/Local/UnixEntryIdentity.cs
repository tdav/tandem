using System.Runtime.InteropServices;
using Tandem.Core.Platform;

namespace Tandem.Core.Vfs.Local;

/// <summary>(device, inode) from <c>lstat</c>, so a link is itself, not its target. macOS only.</summary>
internal sealed class UnixEntryIdentity : IEntryIdentity
{
    private const int ENOENT = 2, EACCES = 13, ENOTDIR = 20;
    private const int StatBufferSize = 256; // struct stat is 144 bytes on macOS

    public (ulong Volume, ulong FileId)? Get(string osPath)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Entry identity is implemented for macOS only.");
        var buffer = new byte[StatBufferSize];
        if (LStat(osPath, buffer) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is ENOENT or ENOTDIR) return null;
            if (errno == EACCES) throw new VfsAccessDeniedException($"Access to '{osPath}' is denied.");
            throw new VfsException($"Cannot read '{osPath}' (errno {errno}).");
        }
        // macOS (64-bit inodes): dev_t st_dev at 0 (int32), ino_t st_ino at 8 (uint64).
        return ((ulong)(uint)BitConverter.ToInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8));
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
}
