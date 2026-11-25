using System.Runtime.InteropServices;
using Tandem.Core.Vfs;
using static Tandem.Platform.Windows.Interop.Kernel32;

namespace Tandem.Platform.Windows;

/// <summary>Exceptions for failed Win32 calls, typed by error code the way .NET's own file APIs type them on Windows,
/// so that the interface words them by kind (disk full, access denied, not found). The message is the system's text
/// for the code, followed by the path; an <see cref="IOException"/> carries <c>HRESULT_FROM_WIN32(code)</c> as its
/// HResult, like .NET's.</summary>
internal static class SystemErrors
{
    public static Exception FromWin32(int error, string path)
    {
        var message = $"{Marshal.GetPInvokeErrorMessage(error)} ('{path}')";
        return error switch
        {
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => new FileNotFoundException(message, path),
            ERROR_ACCESS_DENIED => new UnauthorizedAccessException(message),
            ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS => new VfsAlreadyExistsException(message),
            _ => new IOException(message, HResultOf(error)),
        };
    }

    /// <summary>HRESULT_FROM_WIN32.</summary>
    public static int HResultOf(int error) => error <= 0 ? error : unchecked((int)(((uint)error & 0xFFFF) | 0x80070000));
}
