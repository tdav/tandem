namespace Tandem.Core.Vfs.Local;

/// <summary>How the running system spells local paths.</summary>
public enum LocalPathStyle
{
    /// <summary>macOS: the OS path is the VFS path itself.</summary>
    Unix,

    /// <summary>Windows: a drive is the first VFS segment (<c>C:\a</c> is <c>/C:/a</c>), a share is
    /// <c>/UNC/server/share/...</c>, and the VFS root <c>/</c> is the list of drives, with no OS path of its
    /// own.</summary>
    Windows,
}

/// <summary>Converts between OS path strings and the '/'-rooted paths of the local provider's
/// <see cref="VfsPath"/>. On <see cref="LocalPathStyle.Unix"/> both directions are the identity.</summary>
public static class LocalPath
{
    /// <summary>The first segment of a UNC path in VFS form: <c>/UNC/server/share</c>.</summary>
    public const string UncSegment = "UNC";

    /// <summary>The style of the running system.</summary>
    public static LocalPathStyle Current { get; } =
        OperatingSystem.IsWindows() ? LocalPathStyle.Windows : LocalPathStyle.Unix;

    /// <summary>The VFS form of an absolute OS path (not yet normalized: <see cref="VfsPath"/> does that). A string
    /// that already starts with '/' is taken as VFS form; on Windows only its drive letter is upper-cased.</summary>
    /// <exception cref="ArgumentException">A relative or drive-relative (<c>C:foo</c>) Windows path.</exception>
    public static string FromOs(string os, LocalPathStyle style)
    {
        ArgumentNullException.ThrowIfNull(os);
        if (style == LocalPathStyle.Unix) return os;

        if (os.StartsWith('/')) return UpperDrive(os);
        if (os.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) os = @"\\" + os[8..];
        else if (os.StartsWith(@"\\?\", StringComparison.Ordinal) || os.StartsWith(@"\\.\", StringComparison.Ordinal))
            os = os[4..];

        if (os.StartsWith(@"\\", StringComparison.Ordinal))
            return "/" + UncSegment + "/" + os[2..].Replace('\\', '/');
        if (os.Length >= 2 && char.IsAsciiLetter(os[0]) && os[1] == ':' && (os.Length == 2 || os[2] is '\\' or '/'))
            return "/" + char.ToUpperInvariant(os[0]) + ":" + os[2..].Replace('\\', '/');
        throw new ArgumentException($"Path must be absolute: '{os}'.", nameof(os));
    }

    /// <summary>The OS path of a normalized local VFS path. <c>/C:</c> is <c>C:\</c> (with the backslash: a bare
    /// <c>C:</c> means the current directory of drive C).</summary>
    /// <exception cref="VfsException">On Windows: the root <c>/</c>, an incomplete share (<c>/UNC/server</c>) or a
    /// first segment that is not a drive. None of them is a place on disk.</exception>
    public static string ToOs(string vfs, LocalPathStyle style)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        if (style == LocalPathStyle.Unix) return vfs;

        var segments = vfs.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 1 && IsDriveSegment(segments[0]))
            return segments.Length == 1 ? segments[0] + @"\" : string.Join('\\', segments);
        if (segments.Length >= 3 && segments[0] == UncSegment)
            return @"\\" + string.Join('\\', segments[1..]);
        throw new VfsException($"'{vfs}' has no Windows path.");
    }

    /// <summary>The OS path of a local <paramref name="path"/> on the running system.</summary>
    /// <exception cref="ArgumentException">The path belongs to another provider.</exception>
    /// <exception cref="VfsException">See <see cref="ToOs(string, LocalPathStyle)"/>.</exception>
    public static string ToOsPath(this VfsPath path)
    {
        if (!string.Equals(path.ProviderId, VfsPath.LocalProviderId, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' is not a local path.", nameof(path));
        return ToOs(path.Path, Current);
    }

    /// <summary>True for a VFS path that is exactly a drive (<c>/C:</c>).</summary>
    public static bool IsDriveRoot(string vfs) =>
        vfs.Length == 3 && vfs[0] == '/' && IsDriveSegment(vfs[1..]);

    /// <summary>True for a drive segment such as <c>C:</c>.</summary>
    public static bool IsDriveSegment(string segment) =>
        segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':';

    private static string UpperDrive(string vfs) =>
        vfs.Length >= 3 && IsDriveSegment(vfs.Substring(1, 2)) && (vfs.Length == 3 || vfs[3] == '/')
            ? "/" + char.ToUpperInvariant(vfs[1]) + vfs[2..]
            : vfs;
}
