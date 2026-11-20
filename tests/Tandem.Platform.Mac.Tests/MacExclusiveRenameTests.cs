using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacExclusiveRenameTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly MacExclusiveRename _rename = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Rename_onto_existing_file_throws_AlreadyExists_and_keeps_both()
    {
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        File.WriteAllText(_temp.Combine("b.txt"), "b");

        Assert.Throws<VfsAlreadyExistsException>(() => _rename.Rename(_temp.Combine("a.txt"), _temp.Combine("b.txt")));

        Assert.Equal("a", File.ReadAllText(_temp.Combine("a.txt")));
        Assert.Equal("b", File.ReadAllText(_temp.Combine("b.txt")));
    }

    [Fact]
    public void Rename_onto_empty_directory_or_broken_link_throws_AlreadyExists()
    {
        Directory.CreateDirectory(_temp.Combine("src"));
        Directory.CreateDirectory(_temp.Combine("empty")); // plain rename(2) would replace it
        File.CreateSymbolicLink(_temp.Combine("broken"), "nowhere");

        Assert.Throws<VfsAlreadyExistsException>(() => _rename.Rename(_temp.Combine("src"), _temp.Combine("empty")));
        Assert.Throws<VfsAlreadyExistsException>(() => _rename.Rename(_temp.Combine("src"), _temp.Combine("broken")));

        Assert.True(Directory.Exists(_temp.Combine("src")));
        Assert.True(Directory.Exists(_temp.Combine("empty")));
        Assert.Equal("nowhere", new FileInfo(_temp.Combine("broken")).LinkTarget);
    }

    [Fact]
    public void Renames_file_directory_and_symlink_itself()
    {
        File.WriteAllText(_temp.Combine("file"), "f");
        Directory.CreateDirectory(_temp.Combine("dir"));
        File.WriteAllText(_temp.Combine("dir/inside.txt"), "i");
        File.CreateSymbolicLink(_temp.Combine("link"), "dir");

        _rename.Rename(_temp.Combine("file"), _temp.Combine("file2"));
        _rename.Rename(_temp.Combine("link"), _temp.Combine("link2"));
        _rename.Rename(_temp.Combine("dir"), _temp.Combine("dir2"));

        Assert.Equal("f", File.ReadAllText(_temp.Combine("file2")));
        Assert.Equal("i", File.ReadAllText(_temp.Combine("dir2/inside.txt")));
        var link = new FileInfo(_temp.Combine("link2"));
        Assert.True(link.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("dir", link.LinkTarget); // the link itself moved; its (now dangling) target text is unchanged
        Assert.Equal(["dir2", "file2", "link2"],
            Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rename_of_missing_source_throws_FileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => _rename.Rename(_temp.Combine("missing"), _temp.Combine("b")));
    }

    [Fact]
    public void Rename_across_volumes_throws_IOException()
    {
        using var volume = TinyVolume.Create(_temp.Combine("tiny.dmg"), _temp.Combine("mnt"));
        File.WriteAllText(_temp.Combine("a.txt"), "a");

        var error = Assert.Throws<IOException>(
            () => _rename.Rename(_temp.Combine("a.txt"), Path.Combine(volume.MountPoint, "a.txt")));

        Assert.Equal("Cross-device link", error.Message); // EXDEV: the engine copies and deletes instead
        Assert.True(File.Exists(_temp.Combine("a.txt")));
    }

    [Theory]
    [InlineData(45)] // ENOTSUP
    [InlineData(22)] // EINVAL
    public void Volume_without_exclusive_rename_throws_NotSupported(int errno)
    {
        var rename = new MacExclusiveRename((_, _) => errno);

        Assert.Throws<NotSupportedException>(() => rename.Rename(_temp.Combine("a"), _temp.Combine("b")));
    }

    [Fact]
    public void RenameSameEntry_changes_only_the_normalization_of_the_name()
    {
        var (composed, decomposed) = Spellings();
        File.WriteAllText(_temp.Combine(composed), "x");
        Assert.SkipUnless(File.Exists(_temp.Combine(decomposed)), "The temp volume is normalization-sensitive.");

        _rename.RenameSameEntry(_temp.Combine(composed), _temp.Combine(decomposed));

        Assert.Equal([decomposed], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Local_provider_with_mac_rename_passes_move_contract()
    {
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), _rename);
        var root = _temp.Root;
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        File.WriteAllText(_temp.Combine("b.txt"), "b");

        // Move_onto_existing_throws_AlreadyExists
        await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => fs.MoveAsync(root.Combine("a.txt"), root.Combine("b.txt"), Ct));
        Assert.Equal("a", File.ReadAllText(_temp.Combine("a.txt")));
        Assert.Equal("b", File.ReadAllText(_temp.Combine("b.txt")));

        // A free name, and a missing source
        await fs.MoveAsync(root.Combine("a.txt"), root.Combine("c.txt"), Ct);
        Assert.Equal("a", File.ReadAllText(_temp.Combine("c.txt")));
        await Assert.ThrowsAsync<VfsNotFoundException>(
            () => fs.MoveAsync(root.Combine("a.txt"), root.Combine("d.txt"), Ct));
        Assert.Equal(["b.txt", "c.txt"],
            Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Local_provider_with_mac_rename_changes_only_letter_case()
    {
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), _rename);
        Directory.CreateDirectory(_temp.Combine("dir"));
        Assert.SkipUnless(Directory.Exists(_temp.Combine("DIR")), "The temp volume is case-sensitive.");

        await fs.MoveAsync(_temp.Root.Combine("dir"), _temp.Root.Combine("Dir"), Ct);

        Assert.Equal(["Dir"], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Local_provider_with_mac_rename_changes_only_normalization()
    {
        var (composed, decomposed) = Spellings();
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), _rename);
        File.WriteAllText(_temp.Combine(composed), "x");
        Assert.SkipUnless(File.Exists(_temp.Combine(decomposed)), "The temp volume is normalization-sensitive.");

        await fs.MoveAsync(_temp.Root.Combine(composed), _temp.Root.Combine(decomposed), Ct);

        Assert.Equal([decomposed], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
        Assert.Equal("x", File.ReadAllText(_temp.Combine(decomposed)));
    }

    [Fact]
    public async Task Local_provider_with_mac_rename_refuses_a_hard_link_of_the_source()
    {
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), _rename);
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        HardLink(_temp.Combine("a.txt"), _temp.Combine("b.txt")); // the same inode under another name

        await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => fs.MoveAsync(_temp.Root.Combine("a.txt"), _temp.Root.Combine("b.txt"), Ct));

        Assert.Equal(["a.txt", "b.txt"],
            Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Local_provider_with_mac_rename_refuses_a_hard_link_of_the_source_in_another_directory()
    {
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), _rename);
        Directory.CreateDirectory(_temp.Combine("x"));
        Directory.CreateDirectory(_temp.Combine("y"));
        File.WriteAllText(_temp.Combine("x/a"), "a");
        HardLink(_temp.Combine("x/a"), _temp.Combine("y/A")); // an equivalent name, but another directory
        Assert.SkipUnless(File.Exists(_temp.Combine("y/a")), "The temp volume is case-sensitive.");

        var error = await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => fs.MoveAsync(VfsPath.Local(_temp.Combine("x/a")), VfsPath.Local(_temp.Combine("y/A")), Ct));

        Assert.Contains(_temp.Combine("y/A"), error.Message);
        Assert.Equal(["a"], Directory.GetFileSystemEntries(_temp.Combine("x")).Select(Path.GetFileName));
        Assert.Equal(["A"], Directory.GetFileSystemEntries(_temp.Combine("y")).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Local_provider_with_mac_rename_respells_through_a_linked_folder(bool hfs, bool normalization)
    {
        using var volume = hfs ? TinyVolume.Create(_temp.Combine("hfs.dmg"), _temp.Combine("hfs")) : null;
        var root = volume?.MountPoint ?? _temp.Path;
        var (from, to) = normalization ? Spellings() : ("a.txt", "A.txt");
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), _rename);
        Directory.CreateDirectory(Path.Combine(root, "real"));
        File.CreateSymbolicLink(Path.Combine(root, "link"), "real");
        File.WriteAllText(Path.Combine(root, "real", from), "a");
        Assert.SkipUnless(File.Exists(Path.Combine(root, "real", to)), "The volume tells the spellings apart.");

        // As from a panel in the linked folder: the source keeps the link, the destination directory is resolved.
        await fs.MoveAsync(VfsPath.Local(Path.Combine(root, "link", from)),
            VfsPath.Local(Path.Combine(root, "real", to)), Ct);

        Assert.Equal([to], Directory.GetFileSystemEntries(Path.Combine(root, "real")).Select(Path.GetFileName));
        Assert.Equal("a", File.ReadAllText(Path.Combine(root, "real", to)));
    }

    [Fact]
    public async Task Local_provider_with_mac_rename_refuses_an_equivalent_name_of_another_file_on_a_case_sensitive_volume()
    {
        using var volume = TinyVolume.Create(_temp.Combine("cs.dmg"), _temp.Combine("cs"), "Case-sensitive APFS");
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), _rename);
        var root = VfsPath.Local(volume.MountPoint);
        File.WriteAllText(Path.Combine(volume.MountPoint, "a.txt"), "a");
        File.WriteAllText(Path.Combine(volume.MountPoint, "A.txt"), "b");
        Assert.False(File.Exists(Path.Combine(volume.MountPoint, "a.TXT"))); // the volume is case-sensitive

        var error = await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => fs.MoveAsync(root.Combine("a.txt"), root.Combine("A.txt"), Ct));

        Assert.Contains(Path.Combine(volume.MountPoint, "A.txt"), error.Message);
        Assert.Equal("a", File.ReadAllText(Path.Combine(volume.MountPoint, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(volume.MountPoint, "A.txt")));
    }

    /// <summary>"й.txt" composed (one code point) and decomposed ("и" + combining breve).</summary>
    private static (string Composed, string Decomposed) Spellings()
    {
        var composed = "й.txt".Normalize(NormalizationForm.FormC);
        var decomposed = "й.txt".Normalize(NormalizationForm.FormD);
        Assert.NotEqual(composed, decomposed);
        return (composed, decomposed);
    }

    private static void HardLink(string existing, string link)
    {
        using var process = System.Diagnostics.Process.Start("/bin/ln", [existing, link]);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
