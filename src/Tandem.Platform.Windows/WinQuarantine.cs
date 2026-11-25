using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tandem.Core.Platform;
using static Tandem.Platform.Windows.Interop.Kernel32;

namespace Tandem.Platform.Windows;

/// <summary>
/// The Mark of the Web: the NTFS alternate data stream <c>path:Zone.Identifier</c>, opened on the item itself
/// (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so a link's own stream, not its target's). An item that does not exist is a
/// <see cref="FileNotFoundException"/>; any other failure — a volume without streams (FAT, many network shares), a
/// stream that cannot be read or written — reads as no mark and writes nothing, since the mark is advisory.
/// </summary>
public sealed class WinQuarantine : IQuarantine
{
    private const string Stream = ":Zone.Identifier";

    /// <summary>A mark is a few lines of text; anything larger is not one.</summary>
    private const int MaxMarkBytes = 64 * 1024;

    public byte[]? Get(string path)
    {
        WinPath.RequireAbsolute(path, nameof(path));
        RequireExists(path);
        try
        {
            using var handle = Open(path, GENERIC_READ, OPEN_EXISTING);
            if (handle is null) return null;
            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            if (stream.Length > MaxMarkBytes) return null;
            var value = new byte[stream.Length];
            stream.ReadExactly(value);
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Set(string path, byte[] value)
    {
        WinPath.RequireAbsolute(path, nameof(path));
        ArgumentNullException.ThrowIfNull(value);
        RequireExists(path);
        try
        {
            // OPEN_ALWAYS and a truncation rather than CREATE_ALWAYS, which a hidden or system file refuses.
            using var handle = Open(path, GENERIC_WRITE, OPEN_ALWAYS);
            if (handle is null) return;
            using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
            stream.SetLength(0);
            stream.Write(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Advisory: an item that cannot carry the mark goes without it.
        }
    }

    /// <summary>The stream opened for <paramref name="access"/>, or null when it cannot be (it is missing, or the
    /// volume has no streams).</summary>
    private static SafeFileHandle? Open(string path, uint access, uint disposition)
    {
        var handle = CreateFile(WinPath.ForWin32(path + Stream), access, FILE_SHARE_READ | FILE_SHARE_DELETE,
            IntPtr.Zero, disposition, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        _ = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return null;
    }

    private static void RequireExists(string path)
    {
        // Not Path.Exists alone: a dangling link exists itself.
        if (!Path.Exists(path) && new FileInfo(path).LinkTarget is null)
            throw new FileNotFoundException($"'{path}' does not exist.", path);
    }
}
