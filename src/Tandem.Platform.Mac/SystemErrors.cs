using System.Runtime.Versioning;
using Tandem.Core.Vfs;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>Exceptions for failed system calls, typed by errno the way .NET's own file APIs type them, so that the
/// interface words them by kind (disk full, access denied, not found); the message is always <c>strerror</c>.</summary>
[SupportedOSPlatform("macos")]
internal static class SystemErrors
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EACCES = 13;
    public const int EEXIST = 17;
    public const int EINVAL = 22;
    public const int ENOSPC = 28;
    public const int ENOTSUP = 45;

    public static Exception FromErrno(int errno)
    {
        var message = LibC.StrError(errno);
        return errno switch
        {
            // .NET gives an IOException for ENOSPC the errno itself as its HResult on macOS.
            ENOSPC => new IOException(message, ENOSPC),
            EACCES or EPERM => new UnauthorizedAccessException(message),
            ENOENT => new FileNotFoundException(message),
            EEXIST => new VfsAlreadyExistsException(message),
            _ => new IOException(message),
        };
    }
}
