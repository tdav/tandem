using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;

namespace Tandem.Archives.Tests;

/// <summary>Packing files of an in-memory provider into a new archive, read back through
/// <see cref="ArchiveProvider"/>; and unpacking, which is a copy out of an archive under its own name.</summary>
[SupportedOSPlatform("macos")]
public sealed class PackOperationTests : IDisposable
{
    private const string ArchivePath = "/out/pack.zip";
    private const string PartPath = "/out/.pack.zip.tandem-part";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Whole even seconds: zip keeps the time in two-second steps.
    private static readonly DateTimeOffset FolderTime = new(2021, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset FileTime = new(2023, 6, 7, 8, 9, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset OtherTime = new(2024, 1, 2, 3, 4, 6, TimeSpan.Zero);

    private readonly CountingHostProvider _host = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();
    private readonly TempDir _tempDir = new();
    private readonly TempFileStore _temp;
    private readonly List<ArchiveProvider> _opened = [];

    public PackOperationTests()
    {
        _temp = new TempFileStore(_tempDir.Combine("store"));
        _providers.Register(_host);
        _host.Files.AddDirectory("/out");
    }

    public void Dispose()
    {
        foreach (var provider in _opened) provider.Dispose();
        _temp.Dispose();
        _tempDir.Dispose();
    }

    public static TheoryData<ArchiveFormat> CreatableFormats => new(ArchiveFormats.Creatable);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private static string NameFor(ArchiveFormat format) => "/out/pack." + ArchiveFormats.DefaultExtension(format);

    private PackOperation Pack(IReadOnlyList<string> sources, string archive = ArchivePath,
        ArchiveFormat format = ArchiveFormat.Zip) =>
        new(_providers, sources.Select(Mem).ToList(), Mem(archive), format, _interaction, _temp);

    private static async Task<OperationResult> RunAsync(FileOperation op) =>
        await op.RunAsync().WaitAsync(Timeout, Ct);

    /// <summary>The tree most tests pack: a folder with a nested folder, an empty folder and a Cyrillic name, and a
    /// file beside it.</summary>
    private void AddTree()
    {
        _host.Files.AddFile("/src/docs/a.txt", "alpha", FileTime);
        _host.Files.AddFile("/src/docs/sub/привет.txt", "мир", OtherTime);
        _host.Files.AddDirectory("/src/docs/empty");
        _host.Files.AddFile("/src/b.bin", Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray(), FileTime);
        foreach (var folder in new[] { "/src/docs/empty", "/src/docs/sub", "/src/docs" })
            _host.Files.SetModifiedAsync(Mem(folder), FolderTime, Ct).GetAwaiter().GetResult();
    }

    private async Task<ArchiveProvider> OpenArchiveAsync(string path)
    {
        var provider = await ArchiveProvider.OpenAsync("arc" + _opened.Count, _providers, Mem(path), _temp, null,
            prompt: null, Ct);
        _opened.Add(provider);
        return provider;
    }

    private static async Task<byte[]> ReadAsync(IFileSystemProvider fs, VfsPath path)
    {
        await using var stream = await fs.OpenReadAsync(path, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    /// <summary>Every entry below <paramref name="directory"/>: path → (kind, time, content of a file as hex).</summary>
    private static async Task<SortedDictionary<string, (EntryKind Kind, DateTimeOffset Modified, string? Content)>>
        TreeAsync(IFileSystemProvider fs, string directory = "/")
    {
        var tree = new SortedDictionary<string, (EntryKind, DateTimeOffset, string?)>(StringComparer.Ordinal);
        var pending = new Stack<string>([directory]);
        while (pending.TryPop(out var folder))
            await foreach (var entry in fs.EnumerateAsync(new VfsPath(fs.Id, folder), Ct))
            {
                var content = entry.Kind == EntryKind.File ? Convert.ToHexString(await ReadAsync(fs, entry.Path)) : null;
                var relative = directory == "/" ? entry.Path.Path : entry.Path.Path[directory.Length..];
                tree[relative] = (entry.Kind, entry.Modified, content);
                if (entry.Kind == EntryKind.Directory) pending.Push(entry.Path.Path);
            }
        return tree;
    }

    private void AssertNothingLeft(string archive = ArchivePath)
    {
        Assert.False(_host.Files.Exists(archive), $"'{archive}' exists");
        Assert.DoesNotContain(_host.Files.AllPaths, p => p.EndsWith(SafeReplace.PartSuffix, StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(_temp.Directory, "*", SearchOption.AllDirectories));
    }

    // ---- Packing ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CreatableFormats))]
    public async Task Packed_tree_reads_back_through_the_archive_provider(ArchiveFormat format)
    {
        AddTree();
        var path = NameFor(format);
        var op = Pack(["/src/docs", "/src/b.bin"], path, format);

        var result = await RunAsync(op);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        var archive = await OpenArchiveAsync(path);
        Assert.Equal(format, archive.Format);
        var expected = await TreeAsync(_host.Files, "/src");
        var actual = await TreeAsync(archive);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (key, (kind, modified, content)) in expected)
        {
            Assert.Equal(kind, actual[key].Kind);
            Assert.Equal(content, actual[key].Content);
            Assert.True(modified == actual[key].Modified, $"{key}: {modified:O} != {actual[key].Modified:O}");
        }
        Assert.Equal("мир", Encoding.UTF8.GetString(await ReadAsync(archive,
            new VfsPath(archive.Id, "/docs/sub/привет.txt"))));
        Assert.DoesNotContain(_host.Files.AllPaths, p => p.EndsWith(SafeReplace.PartSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pack_describes_itself_for_the_interface()
    {
        AddTree();
        _host.Files.SetVolumeKey("/src", "source-volume");
        var op = Pack(["/src/docs", "/src/b.bin"]);

        Assert.Equal(OperationKind.Pack, op.Kind);
        Assert.Equal(2, op.ItemCount);
        Assert.Equal([Mem("/src/docs"), Mem("/src/b.bin")], op.Sources);
        Assert.Equal(Mem("/out"), op.Destination);
        Assert.Equal(["mem:mem", "mem:source-volume"], (await op.GetVolumeKeysAsync(Ct)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Empty_folder_is_stored()
    {
        _host.Files.AddDirectory("/src/empty");

        var result = await RunAsync(Pack(["/src/empty"]));

        Assert.Equal(OperationState.Completed, result.State);
        var archive = await OpenArchiveAsync(ArchivePath);
        var tree = await TreeAsync(archive);
        Assert.Equal(["/empty"], tree.Keys);
        Assert.Equal(EntryKind.Directory, tree["/empty"].Kind);
    }

    [Fact]
    public async Task Symlink_is_skipped_and_listed_in_the_summary()
    {
        _host.Files.AddFile("/src/a.txt", "alpha", FileTime);
        _host.Files.AddDirectory("/src/dir");
        await _host.Files.CreateSymlinkAsync(Mem("/src/link"), "a.txt", Ct);
        await _host.Files.CreateSymlinkAsync(Mem("/src/dir/inner-link"), "../a.txt", Ct);

        var op = Pack(["/src/a.txt", "/src/link", "/src/dir"]);
        var result = await RunAsync(op);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal([new SkippedItem(Mem("/src/link"), SkipReason.SymlinksNotSupported),
                      new SkippedItem(Mem("/src/dir/inner-link"), SkipReason.SymlinksNotSupported)], result.Skipped);
        var tree = await TreeAsync(await OpenArchiveAsync(ArchivePath));
        Assert.Equal(["/a.txt", "/dir"], tree.Keys);
        Assert.Equal(op.Progress.FilesTotal, op.Progress.FilesDone);
    }

    [Fact]
    public async Task Existing_archive_file_is_rejected()
    {
        _host.Files.AddFile("/src/a.txt", "alpha");
        _host.Files.AddFile(ArchivePath, "keep me");
        var op = Pack(["/src/a.txt"]);

        var rejection = await Assert.ThrowsAsync<OperationRejectedException>(() => RunAsync(op));

        Assert.Equal(RejectionReason.DestinationExists, rejection.Reason);
        Assert.Equal(Mem(ArchivePath), rejection.DestinationPath);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal("keep me", _host.Files.ReadAllText(ArchivePath));
        Assert.DoesNotContain(_host.Files.AllPaths, p => p.EndsWith(SafeReplace.PartSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Archive_inside_its_own_source_is_rejected()
    {
        _host.Files.AddFile("/src/a.txt", "alpha");
        var before = _host.Files.AllPaths.ToList();
        var op = Pack(["/src"], "/src/inner/pack.zip");

        var rejection = await Assert.ThrowsAsync<OperationRejectedException>(() => RunAsync(op));

        Assert.Equal(RejectionReason.DestinationInsideSource, rejection.Reason);
        Assert.Equal(Mem("/src"), rejection.SourcePath);
        Assert.Equal(before, _host.Files.AllPaths);
    }

    [Theory]
    [InlineData(ArchiveFormat.Rar)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task Format_that_cannot_be_created_is_rejected(ArchiveFormat format)
    {
        _host.Files.AddFile("/src/a.txt", "alpha");
        var before = _host.Files.AllPaths.ToList();
        var path = NameFor(format);
        var op = Pack(["/src/a.txt"], path, format);

        var rejection = await Assert.ThrowsAsync<OperationRejectedException>(() => RunAsync(op));

        Assert.Equal(RejectionReason.FormatNotWritable, rejection.Reason);
        Assert.Equal(Mem(path), rejection.DestinationPath);
        Assert.Equal(before, _host.Files.AllPaths);
    }

    [Theory]
    [MemberData(nameof(CreatableFormats))]
    public async Task Cancel_leaves_no_archive_and_no_part_file(ArchiveFormat format)
    {
        var path = NameFor(format);
        var part = "/out/." + path[5..] + SafeReplace.PartSuffix;
        _host.Files.AddFile("/src/1.bin", new byte[100_000], FileTime);
        _host.Files.AddFile("/src/2.bin", new byte[100_000], FileTime);
        var op = Pack(["/src/1.bin", "/src/2.bin"], path, format);
        var writingWhenCancelled = false;
        // Cancel while the archive is being written: when the second file is opened, the first is in the part file.
        _host.Files.FailOn((call, p) =>
        {
            if (call == "OpenRead" && p == Mem("/src/2.bin"))
            {
                writingWhenCancelled = _host.Files.Exists(part) || _host.Files.Exists(path);
                op.Cancel();
            }
            return false;
        }, () => new IOException("not used"));

        var result = await RunAsync(op);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.True(writingWhenCancelled, "nothing was being written when the operation was cancelled");
        AssertNothingLeft(path);
    }

    [Theory]
    [MemberData(nameof(CreatableFormats))]
    public async Task Failed_write_leaves_no_archive_and_no_part_file(ArchiveFormat format)
    {
        var path = NameFor(format);
        var noise = new byte[50_000];
        new Random(25).NextBytes(noise); // does not compress: the archive passes the failing byte
        _host.Files.AddFile("/src/a.bin", noise);
        // Under either name: the disk is full whatever the archive is written as.
        foreach (var name in new[] { "/out/." + path[5..] + SafeReplace.PartSuffix, path })
            _host.Files.FailWriteAfter(name, 600, () => new IOException("The disk is full."));
        var op = Pack(["/src/a.bin"], path, format);

        var error = await Assert.ThrowsAsync<IOException>(() => RunAsync(op));

        Assert.Equal("The disk is full.", error.Message);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Empty(_interaction.Errors); // a failed write is no item error to skip
        AssertNothingLeft(path);
    }

    /// <summary>Files for a Zip64 archive (forced by <see cref="PackOperation.AlwaysZip64"/>; a real one needs more
    /// than 65,535 entries or about 4 GiB), which the zip writer cannot write to the host's write stream (it does not
    /// seek): it is built in a temporary file first.</summary>
    private PackOperation PackZip64()
    {
        _host.Files.AddDirectory("/src/folder");
        _host.Files.AddFile("/src/z1.bin", new byte[1000], FileTime);
        _host.Files.AddFile("/src/z2.bin", new byte[1000], FileTime);
        return new PackOperation(_providers, [Mem("/src/folder"), Mem("/src/z1.bin"), Mem("/src/z2.bin")],
            Mem(ArchivePath), ArchiveFormat.Zip, _interaction, _temp) { AlwaysZip64 = true };
    }

    private bool TemporaryFilesExist() =>
        Directory.EnumerateFiles(_temp.Directory, "*", SearchOption.AllDirectories).Any();

    [Fact]
    public async Task Zip64_archive_is_built_in_a_temporary_file_for_a_stream_that_does_not_seek()
    {
        var op = PackZip64();
        var temporaryWhileWriting = false;
        _host.Files.FailOn((call, p) =>
        {
            if (call == "OpenRead" && p == Mem("/src/z2.bin")) temporaryWhileWriting = TemporaryFilesExist();
            return false;
        }, () => new IOException("not used"));

        var result = await RunAsync(op);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.True(temporaryWhileWriting, "the archive was not built in a temporary file");
        Assert.False(TemporaryFilesExist());
        await using var stream = await _host.Files.OpenReadAsync(Mem(ArchivePath), Ct);
        using var check = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Equal(["folder/", "z1.bin", "z2.bin"], check.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task Cancel_removes_the_temporary_file_of_a_zip64_archive()
    {
        var op = PackZip64();
        var temporaryWhenCancelled = false;
        _host.Files.FailOn((call, p) =>
        {
            if (call == "OpenRead" && p == Mem("/src/z2.bin"))
            {
                temporaryWhenCancelled = TemporaryFilesExist();
                op.Cancel();
            }
            return false;
        }, () => new IOException("not used"));

        var result = await RunAsync(op);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.True(temporaryWhenCancelled, "the archive was not being built in a temporary file");
        AssertNothingLeft();
    }

    [Fact]
    public async Task File_that_cannot_be_opened_can_be_skipped()
    {
        _host.Files.AddFile("/src/a.txt", "alpha", FileTime);
        _host.Files.AddFile("/src/b.txt", "bravo", FileTime);
        _host.Files.AddFile("/src/c.txt", "charlie", FileTime);
        _host.Files.FailOn((call, p) => call == "OpenRead" && p == Mem("/src/b.txt"),
            () => new UnauthorizedAccessException("No access."));
        _interaction.OnError(ErrorAction.Skip);
        var op = Pack(["/src/a.txt", "/src/b.txt", "/src/c.txt"]);

        var result = await RunAsync(op);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(Mem("/src/b.txt"), Assert.Single(_interaction.Errors).Item);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal((Mem("/src/b.txt"), SkipReason.Error), (skipped.Path, skipped.Reason));
        var archive = await OpenArchiveAsync(ArchivePath);
        var tree = await TreeAsync(archive);
        Assert.Equal(["/a.txt", "/c.txt"], tree.Keys);
        Assert.Equal("charlie", Encoding.UTF8.GetString(await ReadAsync(archive, new VfsPath(archive.Id, "/c.txt"))));
        Assert.Equal((3, 3, 12L, 12L), (op.Progress.FilesTotal, op.Progress.FilesDone, op.Progress.BytesTotal,
            op.Progress.BytesDone));
    }

    [Fact]
    public async Task Totals_equal_scanned_files_and_bytes()
    {
        AddTree();
        var states = new List<OperationState>();
        var op = Pack(["/src/docs", "/src/b.bin"]);
        op.Changed += (_, _) =>
        {
            lock (states)
                if (states.Count == 0 || states[^1] != op.State)
                    states.Add(op.State);
        };

        await RunAsync(op);

        var progress = op.Progress;
        // a.txt (5) + привет.txt ("мир", 6 bytes in UTF-8) + b.bin (3000); folders are not counted
        Assert.Equal((3, 3), (progress.FilesTotal, progress.FilesDone));
        Assert.Equal((3011L, 3011L), (progress.BytesTotal, progress.BytesDone));
        lock (states)
            Assert.Equal([OperationState.Scanning, OperationState.Running, OperationState.Completed],
                states.Where(s => s != OperationState.Queued));
    }

    [Fact]
    public async Task Archive_can_be_packed_into_a_writable_zip_in_one_rewrite()
    {
        _host.Files.AddFile("/arc/host.zip", Fixture.Bytes("sample.zip"), FileTime);
        var host = await ArchiveProvider.OpenAsync("host", _providers, Mem("/arc/host.zip"), _temp, null,
            prompt: null, Ct);
        _opened.Add(host);
        _providers.Register(host);
        AddTree();
        var op = new PackOperation(_providers, [Mem("/src/docs")], new VfsPath("host", "/packed.tar"),
            ArchiveFormat.Tar, _interaction, _temp);

        var result = await RunAsync(op);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(1, _host.ReplacementsOf("/arc/host.zip"));
        var reopenedHost = await ArchiveProvider.OpenAsync("host2", _providers, Mem("/arc/host.zip"), _temp, null,
            prompt: null, Ct);
        _opened.Add(reopenedHost);
        _providers.Register(reopenedHost);
        Assert.Null(await reopenedHost.GetEntryAsync(new VfsPath("host2", "/.packed.tar.tandem-part"), Ct));
        var packed = await ArchiveProvider.OpenAsync("packed", _providers, new VfsPath("host2", "/packed.tar"), _temp,
            null, prompt: null, Ct);
        _opened.Add(packed);
        Assert.Equal((await TreeAsync(_host.Files, "/src")).Keys.Where(k => k.StartsWith("/docs", StringComparison.Ordinal)),
            (await TreeAsync(packed)).Keys);
    }

    // ---- Unpacking -------------------------------------------------------------------------------------

    [Fact]
    public async Task Unpacking_the_root_entries_of_a_slip_archive_writes_only_into_the_destination()
    {
        _host.Files.AddFile("/arc/slip.zip", Fixture.Bytes("slip.zip"), FileTime);
        var archive = await OpenArchiveAsync("/arc/slip.zip");
        _providers.Register(archive);
        _host.Files.AddDirectory("/dst/out");
        var before = _host.Files.AllPaths.ToHashSet(StringComparer.Ordinal);
        var entries = new List<VfsPath>();
        await foreach (var entry in archive.EnumerateAsync(new VfsPath(archive.Id, "/"), Ct)) entries.Add(entry.Path);
        var op = new CopyOperation(_providers, entries, Mem("/dst/out"), new CopyOptions(), _interaction,
            kind: OperationKind.Unpack);

        var result = await RunAsync(op);

        Assert.Equal(OperationKind.Unpack, op.Kind);
        Assert.Equal(OperationState.Completed, result.State);
        var added = _host.Files.AllPaths.Where(p => !before.Contains(p)).Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, p => Assert.StartsWith("/dst/out/", p, StringComparison.Ordinal));
    }
}
