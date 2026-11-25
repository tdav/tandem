using System.Runtime.Versioning;
using Tandem.Core.Vfs;

namespace Tandem.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinExclusiveRenameTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly WinExclusiveRename _rename = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Rename_onto_an_existing_file_throws_AlreadyExists_and_keeps_both()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        File.WriteAllText(_temp.Combine("b.txt"), "b");

        Assert.Throws<VfsAlreadyExistsException>(() => _rename.Rename(_temp.Combine("a.txt"), _temp.Combine("b.txt")));

        Assert.Equal("a", File.ReadAllText(_temp.Combine("a.txt")));
        Assert.Equal("b", File.ReadAllText(_temp.Combine("b.txt")));
    }

    [Fact]
    public void Rename_onto_an_existing_empty_directory_throws_AlreadyExists_and_keeps_both()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Directory.CreateDirectory(_temp.Combine("src"));
        File.WriteAllText(_temp.Combine("src/inside.txt"), "i");
        Directory.CreateDirectory(_temp.Combine("empty"));

        Assert.Throws<VfsAlreadyExistsException>(() => _rename.Rename(_temp.Combine("src"), _temp.Combine("empty")));
        Assert.Throws<VfsAlreadyExistsException>(
            () => _rename.Rename(_temp.Combine("src/inside.txt"), _temp.Combine("empty")));

        Assert.Equal("i", File.ReadAllText(_temp.Combine("src/inside.txt")));
        Assert.True(Directory.Exists(_temp.Combine("empty")));
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Combine("empty")));
    }

    [Fact]
    public void Renames_a_file_and_a_directory_to_free_names()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllText(_temp.Combine("file"), "f");
        Directory.CreateDirectory(_temp.Combine("dir"));
        File.WriteAllText(_temp.Combine("dir/inside.txt"), "i");

        _rename.Rename(_temp.Combine("file"), _temp.Combine("file2"));
        _rename.Rename(_temp.Combine("dir"), _temp.Combine("dir2"));

        Assert.Equal("f", File.ReadAllText(_temp.Combine("file2")));
        Assert.Equal("i", File.ReadAllText(_temp.Combine("dir2/inside.txt")));
        Assert.Equal(["dir2", "file2"],
            Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rename_of_a_missing_source_throws_FileNotFound()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        Assert.Throws<FileNotFoundException>(() => _rename.Rename(_temp.Combine("missing"), _temp.Combine("b")));
        Assert.Throws<FileNotFoundException>(
            () => _rename.Rename(_temp.Combine("no-dir/missing"), _temp.Combine("b")));
    }

    [Fact]
    public void Rename_across_volumes_throws_IOException_and_keeps_the_source()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var other = OtherWritableVolumeDirectory();
        Assert.SkipWhen(other is null, "No second writable volume.");
        try
        {
            File.WriteAllText(_temp.Combine("a.txt"), "a");

            var error = Assert.Throws<IOException>(
                () => _rename.Rename(_temp.Combine("a.txt"), Path.Combine(other!, "a.txt")));

            Assert.IsNotType<VfsAlreadyExistsException>(error);
            Assert.True(File.Exists(_temp.Combine("a.txt")));
            Assert.False(File.Exists(Path.Combine(other!, "a.txt")));
        }
        finally
        {
            Directory.Delete(other!, recursive: true);
        }
    }

    [Fact]
    public void RenameSameEntry_changes_only_the_letter_case()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        Directory.CreateDirectory(_temp.Combine("dir"));

        _rename.RenameSameEntry(_temp.Combine("a.txt"), _temp.Combine("A.txt"));
        _rename.RenameSameEntry(_temp.Combine("dir"), _temp.Combine("Dir"));

        Assert.Equal(["A.txt", "Dir"],
            Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("a", File.ReadAllText(_temp.Combine("A.txt")));
    }

    /// <summary>A fresh directory on a ready fixed volume other than the temp directory's, or null.</summary>
    private string? OtherWritableVolumeDirectory()
    {
        var tempRoot = Path.GetPathRoot(_temp.Path)!;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed ||
                string.Equals(drive.Name, tempRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            var directory = Path.Combine(drive.Name, "tandem-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not writable: try the next one.
            }
        }
        return null;
    }
}
