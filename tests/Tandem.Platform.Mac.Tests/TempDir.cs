using System.Runtime.Versioning;
using Tandem.Core.Vfs;

namespace Tandem.Platform.Mac.Tests;

/// <summary>A fresh directory under the system temp path, deleted (after restoring permissions) on dispose.</summary>
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
        RestorePermissions(Path);
        Directory.Delete(Path, recursive: true);
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
