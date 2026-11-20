using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tandem.Platform.Mac.Interop;

/// <summary>libc (libSystem) functions, with signatures and constants as in the macOS SDK headers.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class LibC
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    // copyfile.h: copyfile_flags_t
    public const uint COPYFILE_ACL = 1 << 0;
    public const uint COPYFILE_STAT = 1 << 1;
    public const uint COPYFILE_XATTR = 1 << 2;
    public const uint COPYFILE_DATA = 1 << 3;
    public const uint COPYFILE_ALL = COPYFILE_STAT | COPYFILE_ACL | COPYFILE_XATTR | COPYFILE_DATA;
    public const uint COPYFILE_METADATA = COPYFILE_STAT | COPYFILE_ACL | COPYFILE_XATTR;
    public const uint COPYFILE_NOFOLLOW_SRC = 1 << 18;
    public const uint COPYFILE_NOFOLLOW_DST = 1 << 19;
    public const uint COPYFILE_NOFOLLOW = COPYFILE_NOFOLLOW_SRC | COPYFILE_NOFOLLOW_DST;
    public const uint COPYFILE_UNLINK = 1 << 21;
    public const uint COPYFILE_CLONE = 1 << 24;

    // copyfile.h: copyfile_state_get/set requestors
    public const uint COPYFILE_STATE_STATUS_CB = 6;
    public const uint COPYFILE_STATE_STATUS_CTX = 7;
    public const uint COPYFILE_STATE_COPIED = 8;

    // copyfile.h: callback "what", stage and return values
    public const int COPYFILE_COPY_DATA = 4;
    public const int COPYFILE_ERR = 3;
    public const int COPYFILE_PROGRESS = 4;
    public const int COPYFILE_CONTINUE = 0;
    public const int COPYFILE_QUIT = 2;

    /// <summary><c>int copyfile(const char *from, const char *to, copyfile_state_t state, copyfile_flags_t flags)</c></summary>
    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int copyfile(string from, string to, IntPtr state, uint flags);

    /// <summary><c>copyfile_state_t copyfile_state_alloc(void)</c></summary>
    [LibraryImport(LibSystem)]
    public static partial IntPtr copyfile_state_alloc();

    /// <summary><c>int copyfile_state_free(copyfile_state_t)</c></summary>
    [LibraryImport(LibSystem)]
    public static partial int copyfile_state_free(IntPtr state);

    /// <summary><c>int copyfile_state_get(copyfile_state_t s, uint32_t flag, void *dst)</c></summary>
    [LibraryImport(LibSystem)]
    public static partial int copyfile_state_get(IntPtr state, uint flag, void* dst);

    /// <summary><c>int copyfile_state_set(copyfile_state_t s, uint32_t flag, const void *src)</c></summary>
    [LibraryImport(LibSystem, SetLastError = true)]
    public static partial int copyfile_state_set(IntPtr state, uint flag, void* src);

    // sys/stat.h: st_flags settable by the owner
    public const uint UF_IMMUTABLE = 0x00000002;
    public const uint UF_APPEND = 0x00000004;

    /// <summary><c>struct stat</c> of arm64 macOS (64-bit inodes); only the fields used here are named.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    public struct Stat
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(116)] public uint Flags;
    }

    /// <summary><c>int lstat(const char *path, struct stat *buf)</c></summary>
    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int lstat(string path, out Stat buffer);

    /// <summary><c>int lchflags(const char *path, unsigned int flags)</c>: the flags of the link itself, not of its
    /// target.</summary>
    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int lchflags(string path, uint flags);

    // sys/stdio.h: renamex_np flags
    public const uint RENAME_EXCL = 0x00000004;

    /// <summary><c>int rename(const char *old, const char *new)</c> (_stdio.h)</summary>
    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int rename(string from, string to);

    /// <summary><c>int renamex_np(const char *, const char *, unsigned int)</c> (sys/stdio.h)</summary>
    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int renamex_np(string from, string to, uint flags);

    /// <summary><c>int strerror_r(int errnum, char *strerrbuf, size_t buflen)</c> (string.h)</summary>
    [LibraryImport(LibSystem)]
    private static partial int strerror_r(int errnum, byte* buffer, nuint length);

    /// <summary>The system's message for <paramref name="errno"/>, from the thread-safe <c>strerror_r</c>.</summary>
    public static string StrError(int errno)
    {
        const int Length = 256;
        var buffer = stackalloc byte[Length];
        // A non-zero result (EINVAL for an unknown errno, ERANGE when truncated) still leaves a terminated message.
        strerror_r(errno, buffer, Length);
        var message = Marshal.PtrToStringUTF8((IntPtr)buffer);
        return string.IsNullOrEmpty(message) ? $"errno {errno}" : message;
    }
}
