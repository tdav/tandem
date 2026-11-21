using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>The <c>com.apple.quarantine</c> extended attribute, read and written with <c>getxattr</c> and
/// <c>setxattr</c> on the item itself (<c>XATTR_NOFOLLOW</c>), inside a broker access scope.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacQuarantine(IFileAccessBroker broker) : IQuarantine
{
    private const string Attribute = "com.apple.quarantine";

    private readonly IFileAccessBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    public byte[]? Get(string path)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        using var access = _broker.Access(path);
        while (true)
        {
            var size = LibC.getxattr(path, Attribute, null, 0, 0, LibC.XATTR_NOFOLLOW);
            if (size < 0) return NoneOrThrow();
            var buffer = new byte[size];
            var read = LibC.getxattr(path, Attribute, buffer, (nuint)buffer.Length, 0, LibC.XATTR_NOFOLLOW);
            if (read < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == SystemErrors.ERANGE) continue; // it grew between the two calls
                return errno == LibC.ENOATTR ? null : throw SystemErrors.FromErrno(errno);
            }
            return read == buffer.Length ? buffer : buffer[..(int)read];
        }

        static byte[]? NoneOrThrow()
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno == LibC.ENOATTR ? null : throw SystemErrors.FromErrno(errno);
        }
    }

    public void Set(string path, byte[] value)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        ArgumentNullException.ThrowIfNull(value);
        using var access = _broker.Access(path);
        if (LibC.setxattr(path, Attribute, value, (nuint)value.Length, 0, LibC.XATTR_NOFOLLOW) != 0)
            throw SystemErrors.FromErrno(Marshal.GetLastPInvokeError());
    }
}
