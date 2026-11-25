namespace Tandem.Platform.Windows;

/// <summary>OS path checks and the form a path is handed to a Win32 call in.</summary>
internal static class WinPath
{
    /// <summary>Paths this long or longer get the <c>\\?\</c> prefix, so that MAX_PATH does not apply (248 is the
    /// limit for a directory name, MAX_PATH less room for an 8.3 file name).</summary>
    private const int LongPath = 248;

    /// <summary>Throws unless <paramref name="path"/> is fully qualified (<c>C:\x</c> or <c>\\server\share\x</c>;
    /// not <c>x</c>, <c>\x</c> or <c>C:x</c>), so it can never be read as an option or against a current
    /// directory.</summary>
    public static void RequireAbsolute(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, paramName);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"The path '{path}' is not absolute.", paramName);
    }

    /// <summary><paramref name="path"/> as a Win32 call takes it: unchanged when short or already prefixed, else
    /// normalized with the <c>\\?\</c> (or <c>\\?\UNC\</c>) prefix.</summary>
    public static string ForWin32(string path)
    {
        if (path.Length < LongPath || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
