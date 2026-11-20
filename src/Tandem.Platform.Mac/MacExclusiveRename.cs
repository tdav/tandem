using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>
/// <c>renamex_np(source, destination, RENAME_EXCL)</c>: the rename fails with EEXIST instead of replacing an existing
/// destination, checked by the file system in the same call. Errors are typed by errno (<see cref="SystemErrors"/>):
/// EEXIST is a <see cref="Core.Vfs.VfsAlreadyExistsException"/>, ENOENT a <see cref="FileNotFoundException"/>,
/// EACCES/EPERM an <see cref="UnauthorizedAccessException"/>, anything else (EXDEV for another volume among them) an
/// <see cref="IOException"/>; the message is <c>strerror(errno)</c>. A volume without exclusive renames answers
/// ENOTSUP or EINVAL, reported as <see cref="NotSupportedException"/>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacExclusiveRename : IExclusiveRename
{
    private readonly Func<string, string, int> _exclusiveRename;

    public MacExclusiveRename() : this(RenameExclusively) { }

    /// <summary>For tests: another exclusive rename call, returning 0 or the errno it failed with.</summary>
    internal MacExclusiveRename(Func<string, string, int> exclusiveRename) =>
        _exclusiveRename = exclusiveRename ?? throw new ArgumentNullException(nameof(exclusiveRename));

    public void Rename(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        var errno = _exclusiveRename(sourcePath, destinationPath);
        if (errno == 0) return;
        // EINVAL is also the answer for a directory moved into itself; the caller's fallback reports that one.
        if (errno is SystemErrors.ENOTSUP or SystemErrors.EINVAL)
            throw new NotSupportedException(LibC.StrError(errno));
        throw SystemErrors.FromErrno(errno);
    }

    public void RenameSameEntry(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        if (LibC.rename(sourcePath, destinationPath) != 0)
            throw SystemErrors.FromErrno(Marshal.GetLastPInvokeError());
    }

    private static int RenameExclusively(string sourcePath, string destinationPath) =>
        LibC.renamex_np(sourcePath, destinationPath, LibC.RENAME_EXCL) == 0 ? 0 : Marshal.GetLastPInvokeError();
}
