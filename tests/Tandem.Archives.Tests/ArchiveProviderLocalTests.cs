using System.Formats.Tar;
using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Archives.Tests;

/// <summary>The archive provider over a real archive file on disk, copied out to the real disk.</summary>
[SupportedOSPlatform("macos")]
public sealed class ArchiveProviderLocalTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempFileStore _temp;
    private readonly ProviderRegistry _providers = new();

    public ArchiveProviderLocalTests()
    {
        _temp = new TempFileStore(_dir.Combine("store"));
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
    }

    public void Dispose()
    {
        _temp.Dispose();
        _dir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sample.zip")]
    [InlineData("sample.7z")]
    [InlineData("solid.7z")]
    public async Task Zip_and_7z_on_real_disk_copy_out_with_content_and_times(string fixture)
    {
        var archivePath = _dir.Combine(fixture);
        await File.WriteAllBytesAsync(archivePath, Fixture.Bytes(fixture), Ct);
        var archive = await ArchiveProvider.OpenAsync("arc", _providers, VfsPath.Local(archivePath), _temp, null, Ct);
        _providers.Register(archive);
        var output = _dir.Combine("out");
        Directory.CreateDirectory(output);
        var sources = new List<VfsPath>();
        await foreach (var entry in archive.EnumerateAsync(new VfsPath("arc", "/"), Ct)) sources.Add(entry.Path);

        var op = new CopyOperation(_providers, sources, VfsPath.Local(output), new CopyOptions(),
            new ScriptedInteraction());
        var result = await op.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(output, "a.txt"), Ct));
        Assert.Equal("hello\n", await File.ReadAllTextAsync(Path.Combine(output, "docs", "readme.txt"), Ct));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(output, "docs", "sub", "data.bin"), Ct));
        Assert.Equal("привет\n", await File.ReadAllTextAsync(Path.Combine(output, "привет.txt"), Ct));
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(output, "empty")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output, ".*", SearchOption.AllDirectories));

        foreach (var copied in new[] { "a.txt", "docs/readme.txt", "docs", "empty" })
        {
            var path = Path.Combine(output, copied);
            if (fixture.EndsWith(".zip", StringComparison.Ordinal))
                Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 6), File.GetLastWriteTime(path)); // zip: wall clock
            else
                Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    /// <summary>Review Focus 2 on a case-insensitive disk: a link "Link" to a folder outside, then "link/file.txt".
    /// On APFS "out/link" is the link, so writing the file naively would land in the folder it points to.</summary>
    [Fact]
    public async Task Case_variant_entry_below_a_link_is_never_written_through_it()
    {
        using var victim = new TempDir();
        var archivePath = _dir.Combine("trap-case.tar");
        await using (var file = File.Create(archivePath))
        await using (var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false))
        {
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "Link") { LinkName = victim.Path },
                Ct);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "link/file.txt")
            {
                DataStream = new MemoryStream("x"u8.ToArray()),
            };
            await tar.WriteEntryAsync(entry, Ct);
        }
        var archive = await ArchiveProvider.OpenAsync("arc", _providers, VfsPath.Local(archivePath), _temp, null,
            Ct);
        _providers.Register(archive);
        var output = _dir.Combine("out");
        Directory.CreateDirectory(output);
        var before = Directory.EnumerateFileSystemEntries(_dir.Path, "*", SearchOption.AllDirectories)
            .ToHashSet(StringComparer.Ordinal);
        var sources = new List<VfsPath>();
        await foreach (var entry in archive.EnumerateAsync(new VfsPath("arc", "/"), Ct)) sources.Add(entry.Path);

        var interaction = new ScriptedInteraction().OnError(ErrorAction.Skip);
        var op = new CopyOperation(_providers, sources, VfsPath.Local(output), new CopyOptions(), interaction);
        await op.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Empty(Directory.EnumerateFileSystemEntries(victim.Path, "*", SearchOption.AllDirectories));
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        var added = Directory.EnumerateFileSystemEntries(_dir.Path, "*", options).Where(p => !before.Contains(p));
        Assert.All(added, p => Assert.StartsWith(output + "/", p, StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(output, "*", options), p => p.EndsWith("file.txt",
            StringComparison.OrdinalIgnoreCase));

        // The folder "link" meets the link "Link" already copied: an item error, skipped, not a merge into it.
        Assert.IsType<VfsAlreadyExistsException>(Assert.Single(interaction.Errors).Error);
        Assert.Equal(EntryKind.Symlink, (await _providers.For(VfsPath.Local(output))
            .GetEntryAsync(VfsPath.Local(Path.Combine(output, "Link")), Ct))!.Kind);
    }
}
