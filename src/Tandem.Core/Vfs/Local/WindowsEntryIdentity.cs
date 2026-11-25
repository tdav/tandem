using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Tandem.Core.Platform;

namespace Tandem.Core.Vfs.Local;

/// <summary>
/// (volume serial number, file index) from <c>GetFileInformationByHandle</c>, on a handle opened with no access
/// (attributes only), shared with everyone, <c>FILE_FLAG_OPEN_REPARSE_POINT</c> (a link or junction is itself, not
/// its target) and <c>FILE_FLAG_BACKUP_SEMANTICS</c> (so directories open too). On ReFS the 128-bit file id is
/// truncated to the 64 bits this call reports. Windows only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsEntryIdentity : IEntryIdentity
{
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5,
        ERROR_BAD_NETPATH = 53, ERROR_BAD_NET_NAME = 67;

    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // READ | WRITE | DELETE
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary>Paths this long or longer get the <c>\\?\</c> prefix, so that MAX_PATH does not apply.</summary>
    private const int LongPath = 248;

    public (ulong Volume, ulong FileId)? Get(string osPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(osPath);
        using var handle = CreateFile(ForWin32(osPath), 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_BAD_NETPATH or ERROR_BAD_NET_NAME)
                return null;
            throw Error(error, osPath);
        }
        if (!GetFileInformationByHandle(handle, out var info))
            throw Error(Marshal.GetLastPInvokeError(), osPath);
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    private static VfsException Error(int error, string path)
    {
        var message = $"{Marshal.GetPInvokeErrorMessage(error)} ('{path}')";
        return error == ERROR_ACCESS_DENIED ? new VfsAccessDeniedException(message) : new VfsException(message);
    }

    private static string ForWin32(string path)
    {
        if (path.Length < LongPath || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION (fileapi.h); the FILETIMEs as their two DWORDs (52 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
