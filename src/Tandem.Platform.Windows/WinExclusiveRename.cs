using System.Runtime.InteropServices;
using Tandem.Core.Platform;
using Tandem.Platform.Windows.Interop;

namespace Tandem.Platform.Windows;

/// <summary>
/// <c>MoveFileExW(source, destination, 0)</c>: without <c>MOVEFILE_REPLACE_EXISTING</c> the file system refuses an
/// existing destination (a file, an empty directory, a link) in the same call, and without
/// <c>MOVEFILE_COPY_ALLOWED</c> it refuses another volume. Errors are typed by code (<see cref="SystemErrors"/>):
/// ERROR_ALREADY_EXISTS / ERROR_FILE_EXISTS a <see cref="Core.Vfs.VfsAlreadyExistsException"/>, FILE/PATH_NOT_FOUND
/// a <see cref="FileNotFoundException"/>, ACCESS_DENIED an <see cref="UnauthorizedAccessException"/>, anything else
/// (ERROR_NOT_SAME_DEVICE, a sharing violation) an <see cref="IOException"/>. Every Windows volume refuses an
/// existing destination this way, so <see cref="NotSupportedException"/> is never thrown.
/// </summary>
public sealed class WinExclusiveRename : IExclusiveRename
{
    public void Rename(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        Move(sourcePath, destinationPath);
    }

    /// <remarks>The same call: Windows lets an entry be renamed to another spelling of its own name (letter case),
    /// which is not an existing destination.</remarks>
    public void RenameSameEntry(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        Move(sourcePath, destinationPath);
    }

    private static void Move(string sourcePath, string destinationPath)
    {
        if (!Kernel32.MoveFileEx(WinPath.ForWin32(sourcePath), WinPath.ForWin32(destinationPath), 0))
        {
            var error = Marshal.GetLastPInvokeError();
            // The not-found error does not say which side is missing; the source is the usual one.
            throw SystemErrors.FromWin32(error,
                error is Interop.Kernel32.ERROR_ALREADY_EXISTS or Interop.Kernel32.ERROR_FILE_EXISTS
                    ? destinationPath
                    : sourcePath);
        }
    }
}
