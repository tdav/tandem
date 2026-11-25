using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tandem.Platform.Windows.Interop;

/// <summary>The kernel32 calls the platform layer makes (fileapi.h, winbase.h).</summary>
internal static partial class Kernel32
{
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NOT_SAME_DEVICE = 17;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_HANDLE_DISK_FULL = 39;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_DISK_FULL = 112;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_NOT_FOUND = 1168;

    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint OPEN_ALWAYS = 4;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
