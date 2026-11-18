using System.Runtime.InteropServices;
using System.Text;

namespace Tandem.Platform.Mac.Tests;

/// <summary>Extended attributes of a file, for the tests (sys/xattr.h).</summary>
internal static partial class XAttr
{
    private const int NoFollow = 0x0001; // XATTR_NOFOLLOW

    public static void Set(string path, string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (setxattr(path, name, bytes, (nuint)bytes.Length, 0, NoFollow) != 0)
            throw new IOException($"setxattr failed: errno {Marshal.GetLastPInvokeError()}");
    }

    /// <summary>The attribute's value, or null when the file does not have it.</summary>
    public static string? Get(string path, string name)
    {
        var size = getxattr(path, name, null, 0, 0, NoFollow);
        if (size < 0) return null;
        var buffer = new byte[size];
        size = getxattr(path, name, buffer, (nuint)buffer.Length, 0, NoFollow);
        return size < 0 ? null : Encoding.UTF8.GetString(buffer, 0, (int)size);
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int setxattr(string path, string name, byte[] value, nuint size, uint position, int options);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint getxattr(string path, string name, byte[]? value, nuint size, uint position, int options);
}
