using System.Runtime.Versioning;
using Tandem.Core.Vfs;

namespace Tandem.Platform.Mac.Tests;

/// <summary>A fresh directory under the system temp path, deleted (after unlocking every entry and restoring
/// permissions) on dispose.</summary>
[SupportedOSPlatform("macos")]
public sealed class TempDir : IDisposable
{
    private const UnixFileMode OwnerAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tandem-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public VfsPath Root => VfsPath.Local(Path);

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        Unlock(Path);
        RestorePermissions(Path);
        Directory.Delete(Path, recursive: true);
    }

    /// <summary>Clears the Finder lock and append-only flags of <paramref name="path"/> and, for a real directory,
    /// of everything inside it (links themselves, never their targets), so the tree can be deleted.</summary>
    private static void Unlock(string path)
    {
        FileFlags.Unlock(path);
        if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) || !Directory.Exists(path)) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | OwnerAll); // to list it
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
            Unlock(child);
    }

    /// <summary>Gives the owner full access to every directory in the tree so it can be deleted.</summary>
    private static void RestorePermissions(string directory)
    {
        File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | OwnerAll);
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            RestorePermissions(child);
    }
}
