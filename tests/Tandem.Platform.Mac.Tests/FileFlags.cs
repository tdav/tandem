using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac.Tests;

/// <summary>File flags (<c>chflags</c>) for the tests: the Finder lock (<c>uchg</c>) and append-only
/// (<c>uappnd</c>). Links are never followed.</summary>
[SupportedOSPlatform("macos")]
internal static class FileFlags
{
    public const uint Locked = LibC.UF_IMMUTABLE;
    public const uint AppendOnly = LibC.UF_APPEND;

    public static uint Get(string path)
    {
        if (LibC.lstat(path, out var stat) != 0)
            throw new IOException($"lstat failed: errno {Marshal.GetLastPInvokeError()}");
        return stat.Flags;
    }

    public static void Add(string path, uint flags) => Set(path, Get(path) | flags);

    /// <summary>Clears the lock and append-only flags; does nothing when the path is gone.</summary>
    public static void Unlock(string path)
    {
        if (LibC.lstat(path, out var stat) != 0) return;
        if ((stat.Flags & (Locked | AppendOnly)) != 0) Set(path, stat.Flags & ~(Locked | AppendOnly));
    }

    private static void Set(string path, uint flags)
    {
        if (LibC.lchflags(path, flags) != 0)
            throw new IOException($"lchflags failed: errno {Marshal.GetLastPInvokeError()}");
    }
}
