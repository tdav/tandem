using System.Formats.Tar;
using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using SysZip = System.IO.Compression;

namespace Tandem.Archives.Tests;

/// <summary>Changing a zip: the changes are collected and the archive is rewritten next to itself and put in place,
/// once per batch. The host is in memory (<see cref="CountingHostProvider"/> counts the replacements).</summary>
[SupportedOSPlatform("macos")]
public sealed class ZipEditingTests : IDisposable
{
    private const string ArchivePath = "/arc/sample.zip";
    private const string PartPath = "/arc/.sample.zip.tandem-part";
    private const string BackupPath = "/arc/.sample.zip.tandem-old";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset HostTime = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset FileTime = new(2023, 6, 7, 8, 9, 10, TimeSpan.Zero);

    private readonly CountingHostProvider _host = new();
    private readonly ProviderRegistry _providers = new();
    private readonly TempDir _tempDir = new();
    private readonly TempFileStore _temp;
    private readonly List<ArchiveProvider> _opened = [];

    public ZipEditingTests()
    {
        _temp = new TempFileStore(_tempDir.Combine("store"));
        _providers.Register(_host);
    }

    public void Dispose()
    {
        foreach (var provider in _opened) provider.Dispose();
        _temp.Dispose();
        _tempDir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Arc(string path) => new("arc", path);

    private static VfsPath Mem(string path) => new("mem", path);

    /// <summary>Puts <paramref name="fixture"/> at <see cref="ArchivePath"/> and opens it as "arc".</summary>
    private async Task<ArchiveProvider> OpenAsync(string fixture = "sample.zip")
    {
        _host.Files.AddFile(ArchivePath, Fixture.Bytes(fixture), HostTime);
        return await OpenOverAsync("arc", Mem(ArchivePath), register: true);
    }

    private async Task<ArchiveProvider> OpenOverAsync(string id, VfsPath host, bool register)
    {
        var provider = await ArchiveProvider.OpenAsync(id, _providers, host, _temp, null, prompt: null, Ct);
        _opened.Add(provider);
        if (register) _providers.Register(provider);
        return provider;
    }

    /// <summary>The archive as a new provider reads it from the host now.</summary>
    private Task<ArchiveProvider> ReopenAsync(string archive = ArchivePath) =>
        OpenOverAsync("fresh", Mem(archive), register: false);

    private async Task<byte[]> HostBytesAsync(string path = ArchivePath)
    {
        await using var stream = await _host.Files.OpenReadAsync(Mem(path), Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private static async Task<byte[]> ReadAsync(IFileSystemProvider fs, VfsPath path)
    {
        await using var stream = await fs.OpenReadAsync(path, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private static async Task<string> TextAsync(IFileSystemProvider fs, string path) =>
        Encoding.UTF8.GetString(await ReadAsync(fs, new VfsPath(fs.Id, path)));

    private static async Task WriteAsync(IFileSystemProvider fs, string path, string text)
    {
        await using var stream = await fs.OpenWriteAsync(new VfsPath(fs.Id, path), Ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), Ct);
    }

    private static async Task<List<FileEntry>> ListAsync(IFileSystemProvider fs, string directory)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(new VfsPath(fs.Id, directory), Ct)) entries.Add(entry);
        return entries;
    }

    /// <summary>Every entry below the root: path → (kind, size, time, content of a file).</summary>
    private static async Task<SortedDictionary<string, (EntryKind Kind, long Size, DateTimeOffset Modified,
        string? Content)>> TreeAsync(IFileSystemProvider fs)
    {
        var tree = new SortedDictionary<string, (EntryKind, long, DateTimeOffset, string?)>(StringComparer.Ordinal);
        var pending = new Stack<string>(["/"]);
        while (pending.TryPop(out var directory))
            foreach (var entry in await ListAsync(fs, directory))
            {
                var content = entry.Kind == EntryKind.File
                    ? Convert.ToHexString(await ReadAsync(fs, entry.Path))
                    : null;
                tree[entry.Path.Path] = (entry.Kind, entry.Size, entry.Modified, content);
                if (entry.Kind == EntryKind.Directory) pending.Push(entry.Path.Path);
            }
        return tree;
    }

    private void AssertNoLeftovers(string directory = "/arc")
    {
        Assert.DoesNotContain(_host.Files.AllPaths, p =>
            p.StartsWith(directory + "/", StringComparison.Ordinal) &&
            (p.EndsWith(SafeReplace.PartSuffix, StringComparison.Ordinal) ||
             p.EndsWith(SafeReplace.BackupSuffix, StringComparison.Ordinal)));
    }

    private async Task<OperationResult> RunAsync(FileOperation op) => await op.RunAsync().WaitAsync(Timeout, Ct);

    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new SysZip.ZipArchive(buffer, SysZip.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var stream = zip.CreateEntry(name, SysZip.CompressionLevel.NoCompression).Open();
                stream.Write(content);
            }
        return buffer.ToArray();
    }

    // ---- Single changes (each one rewrites the archive) -------------------------------------------------

    [Fact]
    public async Task Added_file_appears_and_untouched_entries_keep_content_and_time()
    {
        var archive = await OpenAsync();
        var before = await TreeAsync(archive);
        Assert.Equal(ProviderCapabilities.Write, archive.Capabilities);

        await WriteAsync(archive, "/новый.txt", "fresh");
        await archive.SetModifiedAsync(Arc("/новый.txt"), FileTime, Ct);

        Assert.Equal(2, _host.ReplacementsOf(ArchivePath));
        foreach (var provider in new[] { archive, await ReopenAsync() })
        {
            var after = await TreeAsync(provider);
            Assert.Equal("fresh", await TextAsync(provider, "/новый.txt"));
            Assert.Equal(FileTime, after["/новый.txt"].Modified);
            after.Remove("/новый.txt");
            Assert.Equal(before, after);
        }
        AssertNoLeftovers();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
        Assert.All(CentralFlags(await HostBytesAsync()), flags => Assert.True((flags & 0x800) != 0)); // UTF-8 names
    }

    /// <summary>The general-purpose flags of every central directory header.</summary>
    private static List<int> CentralFlags(byte[] zip)
    {
        var flags = new List<int>();
        for (var i = 0; i + 10 < zip.Length; i++)
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 1 && zip[i + 3] == 2)
                flags.Add(zip[i + 8] | zip[i + 9] << 8);
        Assert.NotEmpty(flags);
        return flags;
    }

    [Fact]
    public async Task Delete_file_and_recursive_delete_of_a_folder()
    {
        var archive = await OpenAsync();

        await archive.DeleteAsync(Arc("/a.txt"), recursive: false, Ct);
        await Assert.ThrowsAnyAsync<IOException>(() => archive.DeleteAsync(Arc("/docs"), recursive: false, Ct));
        await archive.DeleteAsync(Arc("/docs"), recursive: true, Ct);
        await archive.DeleteAsync(Arc("/empty"), recursive: false, Ct);
        await Assert.ThrowsAsync<VfsNotFoundException>(() => archive.DeleteAsync(Arc("/a.txt"), false, Ct));

        foreach (var provider in new[] { archive, await ReopenAsync() })
            Assert.Equal(["/привет.txt"], (await TreeAsync(provider)).Keys);
        Assert.Equal(3, _host.ReplacementsOf(ArchivePath));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Rename_file_and_rename_folder_with_its_subtree()
    {
        var archive = await OpenAsync();
        var before = await TreeAsync(archive);

        await archive.MoveAsync(Arc("/a.txt"), Arc("/docs/b.txt"), Ct);
        await archive.MoveAsync(Arc("/docs"), Arc("/papers"), Ct);
        await Assert.ThrowsAsync<VfsAlreadyExistsException>(() =>
            archive.MoveAsync(Arc("/papers"), Arc("/empty"), Ct));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => archive.MoveAsync(Arc("/docs"), Arc("/x"), Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => archive.MoveAsync(Arc("/papers"), Arc("/papers/sub/p"), Ct));

        foreach (var provider in new[] { archive, await ReopenAsync() })
        {
            var after = await TreeAsync(provider);
            Assert.Equal(["/empty", "/papers", "/papers/b.txt", "/papers/readme.txt", "/papers/sub",
                "/papers/sub/data.bin", "/привет.txt"], after.Keys);
            Assert.Equal(before["/a.txt"], after["/papers/b.txt"]);
            Assert.Equal(before["/docs/sub/data.bin"], after["/papers/sub/data.bin"]);
            Assert.Equal(before["/docs/readme.txt"], after["/papers/readme.txt"]);
        }
        Assert.Equal(2, _host.ReplacementsOf(ArchivePath));
    }

    [Fact]
    public async Task Created_folder_survives_reopening_the_archive()
    {
        var archive = await OpenAsync();

        await archive.CreateDirectoryAsync(Arc("/new/deep"), Ct);
        await archive.CreateDirectoryAsync(Arc("/new/deep"), Ct); // nothing to change: no rewrite
        await archive.CreateDirectoryAsync(Arc("/"), Ct);
        await Assert.ThrowsAnyAsync<IOException>(() => archive.CreateDirectoryAsync(Arc("/a.txt"), Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => archive.CreateDirectoryAsync(Arc("/a.txt/x"), Ct));

        var reopened = await ReopenAsync();
        Assert.Equal(EntryKind.Directory, (await reopened.GetEntryAsync(new VfsPath("fresh", "/new"), Ct))!.Kind);
        Assert.Equal(EntryKind.Directory, (await reopened.GetEntryAsync(new VfsPath("fresh", "/new/deep"), Ct))!.Kind);
        Assert.Empty(await ListAsync(reopened, "/new/deep"));
        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
    }

    [Fact]
    public async Task Writes_follow_the_provider_contract()
    {
        var archive = await OpenAsync();

        await Assert.ThrowsAsync<VfsNotFoundException>(() => archive.OpenWriteAsync(Arc("/missing/f.txt"), Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => archive.OpenWriteAsync(Arc("/docs"), Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => archive.OpenWriteAsync(Arc("/a.txt/f.txt"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.DeleteAsync(Arc("/"), true, Ct));
        await Assert.ThrowsAsync<VfsNotFoundException>(() =>
            archive.SetModifiedAsync(Arc("/missing"), FileTime, Ct));
        var link = await Assert.ThrowsAnyAsync<VfsException>(() => archive.CreateSymlinkAsync(Arc("/l"), "a.txt", Ct));
        Assert.IsNotType<VfsAccessDeniedException>(link);
        var mode = await Assert.ThrowsAnyAsync<VfsException>(() =>
            archive.SetModeAsync(Arc("/a.txt"), UnixFileMode.UserRead, Ct));
        Assert.IsNotType<VfsAccessDeniedException>(mode);

        Assert.Equal(0, _host.ReplacementsOf(ArchivePath));
        Assert.Equal(Fixture.Bytes("sample.zip"), await HostBytesAsync());
    }

    // ---- Through the operations engine ------------------------------------------------------------------

    [Fact]
    public async Task Overwrite_answer_replaces_an_entry_through_the_engine()
    {
        var archive = await OpenAsync();
        _host.Files.AddFile("/src/a.txt", "replaced", FileTime);
        var interaction = new ScriptedInteraction().OnConflict(ConflictAction.Overwrite);

        var result = await RunAsync(new CopyOperation(_providers, [Mem("/src/a.txt")], Arc("/"), new CopyOptions(),
            interaction));

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Single(interaction.Conflicts);
        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
        foreach (var provider in new[] { archive, await ReopenAsync() })
        {
            Assert.Equal("replaced", await TextAsync(provider, "/a.txt"));
            Assert.Equal(FileTime, (await provider.GetEntryAsync(new VfsPath(provider.Id, "/a.txt"), Ct))!.Modified);
            Assert.Equal(["a.txt", "docs", "empty", "привет.txt"],
                (await ListAsync(provider, "/")).Select(e => e.Name).Order(StringComparer.Ordinal));
        }
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Copying_100_files_in_rewrites_the_archive_once()
    {
        var archive = await OpenAsync();
        for (var i = 0; i < 100; i++) _host.Files.AddFile($"/src/batch/f{i:D3}.txt", $"file {i}", FileTime);

        var result = await RunAsync(new CopyOperation(_providers, [Mem("/src/batch")], Arc("/docs"),
            new CopyOptions(), new ScriptedInteraction()));

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
        var reopened = await ReopenAsync();
        var copied = await ListAsync(reopened, "/docs/batch");
        Assert.Equal(100, copied.Count);
        Assert.Equal("file 42", await TextAsync(reopened, "/docs/batch/f042.txt"));
        Assert.Equal("hello\n", await TextAsync(reopened, "/docs/readme.txt"));
        AssertNoLeftovers();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    [Fact]
    public async Task Reads_inside_a_batch_see_pending_changes()
    {
        var archive = await OpenAsync();
        var original = await HostBytesAsync();

        await using (var batch = await archive.BeginBatchAsync(Ct))
        {
            await WriteAsync(archive, "/new.txt", "pending");
            await archive.DeleteAsync(Arc("/a.txt"), recursive: false, Ct);
            await archive.MoveAsync(Arc("/docs"), Arc("/d2"), Ct);
            await archive.CreateDirectoryAsync(Arc("/made"), Ct);

            Assert.Equal("pending", await TextAsync(archive, "/new.txt"));
            Assert.Equal(7, (await archive.GetEntryAsync(Arc("/new.txt"), Ct))!.Size);
            Assert.Null(await archive.GetEntryAsync(Arc("/a.txt"), Ct));
            Assert.Equal("hello\n", await TextAsync(archive, "/d2/readme.txt"));
            Assert.Equal(["d2", "empty", "made", "new.txt", "привет.txt"],
                (await ListAsync(archive, "/")).Select(e => e.Name).Order(StringComparer.Ordinal));
            await Assert.ThrowsAsync<VfsNotFoundException>(() => ListAsync(archive, "/docs"));
            Assert.Equal(original, await HostBytesAsync()); // nothing written yet

            await batch.CommitAsync(Ct);
        }

        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
        Assert.Equal(["/d2", "/d2/readme.txt", "/d2/sub", "/d2/sub/data.bin", "/empty", "/made", "/new.txt",
            "/привет.txt"], (await TreeAsync(await ReopenAsync())).Keys);
    }

    [Fact]
    public async Task Batch_disposed_without_commit_discards_its_changes()
    {
        var archive = await OpenAsync();
        var before = await TreeAsync(archive);

        await using (await archive.BeginBatchAsync(Ct))
        {
            await WriteAsync(archive, "/new.txt", "pending");
            await archive.DeleteAsync(Arc("/docs"), recursive: true, Ct);
        }

        Assert.Equal(before, await TreeAsync(archive));
        Assert.Equal(Fixture.Bytes("sample.zip"), await HostBytesAsync());
        Assert.Equal(0, _host.ReplacementsOf(ArchivePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    [Fact]
    public async Task Move_out_of_zip_copies_then_removes_entries()
    {
        var archive = await OpenAsync();
        _host.Files.AddDirectory("/out");

        var result = await RunAsync(new MoveOperation(_providers, [Arc("/docs"), Arc("/a.txt")], Mem("/out"),
            new CopyOptions(), new ScriptedInteraction()));

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("hello\n", _host.Files.ReadAllText("/out/docs/readme.txt"));
        Assert.Equal("a", _host.Files.ReadAllText("/out/a.txt"));
        Assert.True(_host.Files.Exists("/out/docs/sub/data.bin"));
        foreach (var provider in new[] { archive, await ReopenAsync() })
            Assert.Equal(["/empty", "/привет.txt"], (await TreeAsync(provider)).Keys);
        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
    }

    [Fact]
    public async Task Move_inside_the_zip_is_a_rename()
    {
        var archive = await OpenAsync();

        var result = await RunAsync(new MoveOperation(_providers, [Arc("/a.txt")], Arc("/docs"), new CopyOptions(),
            new ScriptedInteraction()));

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", await TextAsync(await ReopenAsync(), "/docs/a.txt"));
        Assert.Null(await archive.GetEntryAsync(Arc("/a.txt"), Ct));
        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
    }

    // ---- Review Focus 1: an interrupted rewrite leaves the original whole ------------------------------

    [Fact]
    public async Task Move_into_zip_keeps_sources_when_the_rewrite_fails()
    {
        var archive = await OpenAsync();
        var before = await TreeAsync(archive);
        _host.Files.AddFile("/src/one.txt", "one");
        _host.Files.AddFile("/src/two/three.txt", "three");
        _host.Files.FailWriteAfter(PartPath, 200, () => new IOException("The disk is full."));

        var op = new MoveOperation(_providers, [Mem("/src/one.txt"), Mem("/src/two")], Arc("/"), new CopyOptions(),
            new ScriptedInteraction());
        var error = await Assert.ThrowsAnyAsync<IOException>(() => RunAsync(op));

        Assert.Equal("The disk is full.", error.Message);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal("one", _host.Files.ReadAllText("/src/one.txt"));
        Assert.Equal("three", _host.Files.ReadAllText("/src/two/three.txt"));
        Assert.Equal(Fixture.Bytes("sample.zip"), await HostBytesAsync());
        AssertNoLeftovers();
        Assert.Equal(before, await TreeAsync(archive));
        Assert.Equal(before, await TreeAsync(await ReopenAsync()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    [Fact]
    public async Task Failed_swap_leaves_the_original_archive_intact_and_readable()
    {
        var archive = await OpenAsync();
        var before = await TreeAsync(archive);
        _host.Files.FailOn((op, path) => op == "Move" && path.Path == PartPath,
            () => new IOException("The volume went away."));

        await using (var batch = await archive.BeginBatchAsync(Ct))
        {
            await WriteAsync(archive, "/new.txt", "lost");
            await archive.DeleteAsync(Arc("/docs"), recursive: true, Ct);
            await Assert.ThrowsAnyAsync<IOException>(() => batch.CommitAsync(Ct));
        }

        Assert.Equal(Fixture.Bytes("sample.zip"), await HostBytesAsync());
        AssertNoLeftovers();
        Assert.Equal(before, await TreeAsync(archive));
        Assert.Equal(before, await TreeAsync(await ReopenAsync()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    [Fact]
    public async Task Rewrite_that_lost_an_entry_is_rejected_before_the_swap()
    {
        var archive = await OpenAsync();
        var before = await TreeAsync(archive);
        // The first read of the written part (the check) sees an archive without the new entry.
        _host.Files.ChangeContentOnRead(PartPath, 1, Fixture.Bytes("sample.zip"));

        await Assert.ThrowsAnyAsync<IOException>(() => WriteAsync(archive, "/new.txt", "never seen"));

        Assert.Equal(Fixture.Bytes("sample.zip"), await HostBytesAsync());
        Assert.Equal(0, _host.ReplacementsOf(ArchivePath));
        AssertNoLeftovers();
        Assert.Equal(before, await TreeAsync(archive));
    }

    [Fact]
    public async Task Truncated_rewrite_is_rejected_before_the_swap()
    {
        var archive = await OpenAsync();
        var before = await TreeAsync(archive);
        var whole = Fixture.Bytes("sample.zip");
        _host.Files.ChangeContentOnRead(PartPath, 1, whole[..(whole.Length - 30)]);

        await Assert.ThrowsAnyAsync<IOException>(() => archive.DeleteAsync(Arc("/a.txt"), false, Ct));

        Assert.Equal(whole, await HostBytesAsync());
        AssertNoLeftovers();
        Assert.Equal(before, await TreeAsync(archive));
    }

    [Fact]
    public async Task Commit_refuses_when_the_archive_changed_meanwhile()
    {
        var archive = await OpenAsync();
        var replacement = Zip(("other.txt", "other"u8.ToArray()));

        await using (var batch = await archive.BeginBatchAsync(Ct))
        {
            await WriteAsync(archive, "/new.txt", "pending");
            _host.Files.AddFile(ArchivePath, replacement, HostTime.AddHours(1)); // changed by someone else
            await Assert.ThrowsAnyAsync<IOException>(() => batch.CommitAsync(Ct));
        }

        Assert.Equal(replacement, await HostBytesAsync());
        AssertNoLeftovers();
        Assert.Equal(["other.txt"], (await ListAsync(archive, "/")).Select(e => e.Name));
    }

    [Fact]
    public async Task Cancelled_copy_into_zip_keeps_the_files_already_copied()
    {
        var archive = await OpenAsync();
        for (var i = 0; i < 10; i++) _host.Files.AddFile($"/src/f{i}.txt", $"file {i}");
        CopyOperation? op = null;
        _host.Files.FailOn((name, path) =>
        {
            if (name == "OpenRead" && path.Path == "/src/f5.txt") op!.Cancel();
            return false;
        }, () => new IOException());
        op = new CopyOperation(_providers, Enumerable.Range(0, 10).Select(i => Mem($"/src/f{i}.txt")).ToList(),
            Arc("/"), new CopyOptions(), new ScriptedInteraction());

        var result = await RunAsync(op);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
        var names = (await ListAsync(await ReopenAsync(), "/")).Select(e => e.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["a.txt", "docs", "empty", "f0.txt", "f1.txt", "f2.txt", "f3.txt", "f4.txt", "привет.txt"],
            names);
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Editor_style_save_inside_one_batch_rewrites_once()
    {
        var archive = await OpenAsync();
        var file = Arc("/docs/readme.txt");

        await using (var scope = await BatchScope.BeginAsync(_providers, [file], Ct))
        {
            await SafeReplace.WriteAsync(archive, file, replace: true, async part =>
            {
                await using var stream = await archive.OpenWriteAsync(part, Ct);
                await stream.WriteAsync("edited\n"u8.ToArray(), Ct);
            });
            await scope.CommitAsync(Ct);
        }

        Assert.Equal(1, _host.ReplacementsOf(ArchivePath));
        var reopened = await ReopenAsync();
        Assert.Equal("edited\n", await TextAsync(reopened, "/docs/readme.txt"));
        Assert.Equal(["readme.txt", "sub"], (await ListAsync(reopened, "/docs")).Select(e => e.Name).Order());
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Second_batch_waits_for_the_first()
    {
        var archive = await OpenAsync();
        var first = await archive.BeginBatchAsync(Ct);
        var second = archive.BeginBatchAsync(Ct);

        await Task.Delay(20, Ct);
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await (await second.WaitAsync(Timeout, Ct)).DisposeAsync();
    }

    // ---- What stays read-only --------------------------------------------------------------------------

    [Theory]
    [InlineData("encrypted.zip")]
    [InlineData("slip.zip")] // its listing renames entries: a rewrite would rename or lose them
    [InlineData("sample.tar")]
    [InlineData("sample.tar.gz")]
    [InlineData("sample.7z")]
    public async Task Encrypted_zip_and_other_formats_stay_read_only(string fixture)
    {
        _host.Files.AddFile("/arc/" + fixture, Fixture.Bytes(fixture), HostTime);
        var archive = await OpenOverAsync("arc", Mem("/arc/" + fixture), register: true);
        var any = (await ListAsync(archive, "/")).First(e => e.Kind == EntryKind.File).Path;

        Assert.Equal(ProviderCapabilities.None, archive.Capabilities);
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.OpenWriteAsync(Arc("/new.txt"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.CreateDirectoryAsync(Arc("/new"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.DeleteAsync(any, recursive: false, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.MoveAsync(any, Arc("/moved"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.SetModifiedAsync(any, FileTime, Ct));
        await using (var batch = await archive.BeginBatchAsync(Ct))
        {
            await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.OpenWriteAsync(Arc("/new.txt"), Ct));
            await batch.CommitAsync(Ct);
        }
        Assert.Equal(Fixture.Bytes(fixture), await HostBytesAsync("/arc/" + fixture));
        Assert.Equal(0, _host.ReplacementsOf("/arc/" + fixture));
    }

    [Fact]
    public async Task Zip_holding_a_link_stays_read_only()
    {
        // A zip keeps a link as a file whose content is the target, marked as a link only in its Unix mode: a rewrite
        // would turn it into a plain file.
        using var buffer = new MemoryStream();
        using (var zip = new SysZip.ZipArchive(buffer, SysZip.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var file = zip.CreateEntry("a.txt").Open()) file.Write("a"u8);
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA1ED0000); // lrwxr-xr-x
            using var target = link.Open();
            target.Write("a.txt"u8);
        }
        _host.Files.AddFile(ArchivePath, buffer.ToArray(), HostTime);
        var archive = await OpenOverAsync("arc", Mem(ArchivePath), register: true);

        Assert.Equal(ProviderCapabilities.None, archive.Capabilities);
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.DeleteAsync(Arc("/a.txt"), false, Ct));
    }

    [Fact]
    public async Task Zip_inside_a_tar_stays_read_only()
    {
        using var tarBytes = new MemoryStream();
        await using (var writer = new TarWriter(tarBytes, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, "inner.zip")
            {
                DataStream = new MemoryStream(Fixture.Bytes("sample.zip")),
            };
            await writer.WriteEntryAsync(entry, Ct);
        }
        _host.Files.AddFile("/arc/outer.tar", tarBytes.ToArray(), HostTime);
        var tar = await OpenOverAsync("tar", Mem("/arc/outer.tar"), register: true);
        var zip = await OpenOverAsync("zip", new VfsPath("tar", "/inner.zip"), register: true);

        Assert.Equal(ProviderCapabilities.None, tar.Capabilities);
        Assert.Equal(ProviderCapabilities.None, zip.Capabilities);
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() =>
            zip.OpenWriteAsync(new VfsPath("zip", "/new.txt"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() =>
            zip.DeleteAsync(new VfsPath("zip", "/a.txt"), recursive: false, Ct));
        Assert.Equal("a", await TextAsync(zip, "/a.txt"));
    }

    [Fact]
    public async Task Zip_inside_a_writable_host_that_takes_no_batches_stays_read_only()
    {
        var hosted = HostedFileSystem.InFile("hosted", _providers, Mem("/arc/container"));
        hosted.Files.AddFile("/inner.zip", Fixture.Bytes("sample.zip"), HostTime);
        _providers.Register(hosted);
        var zip = await OpenOverAsync("zip", new VfsPath("hosted", "/inner.zip"), register: true);

        Assert.True(hosted.Capabilities.HasFlag(ProviderCapabilities.Write));
        Assert.Null(zip.BatchHost);
        Assert.Equal(ProviderCapabilities.None, zip.Capabilities);
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() =>
            zip.OpenWriteAsync(new VfsPath("zip", "/new.txt"), Ct));
        Assert.Equal(Fixture.Bytes("sample.zip"), await ReadAsync(hosted, new VfsPath("hosted", "/inner.zip")));
    }

    // ---- Nesting --------------------------------------------------------------------------------------

    [Fact]
    public async Task Zip_inside_zip_edit_rewrites_the_outer_archive_once()
    {
        const string outerPath = "/arc/outer.zip";
        _host.Files.AddFile(outerPath, Zip(("inner.zip", Fixture.Bytes("sample.zip")), ("note.txt", "n"u8.ToArray())),
            HostTime);
        var outer = await OpenOverAsync("outer", Mem(outerPath), register: true);
        var inner = await OpenOverAsync("inner", new VfsPath("outer", "/inner.zip"), register: true);
        for (var i = 0; i < 5; i++) _host.Files.AddFile($"/src/f{i}.txt", $"file {i}", FileTime);
        Assert.Same(outer, inner.BatchHost);
        Assert.Equal(ProviderCapabilities.Write, inner.Capabilities);

        var result = await RunAsync(new CopyOperation(_providers,
            Enumerable.Range(0, 5).Select(i => Mem($"/src/f{i}.txt")).ToList(), new VfsPath("inner", "/docs"),
            new CopyOptions(), new ScriptedInteraction()));

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(1, _host.ReplacementsOf(outerPath));
        var reopenedOuter = await OpenOverAsync("outer2", Mem(outerPath), register: true);
        Assert.Equal(["inner.zip", "note.txt"],
            (await ListAsync(reopenedOuter, "/")).Select(e => e.Name).Order(StringComparer.Ordinal));
        var reopenedInner = await OpenOverAsync("inner2", new VfsPath("outer2", "/inner.zip"), register: false);
        Assert.Equal("file 3", await TextAsync(reopenedInner, "/docs/f3.txt"));
        Assert.Equal("hello\n", await TextAsync(reopenedInner, "/docs/readme.txt"));
        Assert.Equal("file 4", await TextAsync(inner, "/docs/f4.txt"));
        AssertNoLeftovers();
        Assert.DoesNotContain((await ListAsync(outer, "/")).Select(e => e.Name),
            n => n.EndsWith(SafeReplace.PartSuffix, StringComparison.Ordinal) ||
                 n.EndsWith(SafeReplace.BackupSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zip_inside_zip_edit_outside_a_batch_reaches_the_outer_archive()
    {
        const string outerPath = "/arc/outer.zip";
        _host.Files.AddFile(outerPath, Zip(("inner.zip", Fixture.Bytes("sample.zip")), ("note.txt", "n"u8.ToArray())),
            HostTime);
        var outer = await OpenOverAsync("outer", Mem(outerPath), register: true);
        var inner = await OpenOverAsync("inner", new VfsPath("outer", "/inner.zip"), register: true);

        await WriteAsync(inner, "/added.txt", "added");
        await inner.DeleteAsync(new VfsPath("inner", "/docs"), recursive: true, Ct);

        Assert.Equal(["a.txt", "added.txt", "empty", "привет.txt"],
            (await ListAsync(inner, "/")).Select(e => e.Name).Order(StringComparer.Ordinal));
        var reopenedOuter = await OpenOverAsync("outer2", Mem(outerPath), register: true);
        Assert.Equal(["inner.zip", "note.txt"],
            (await ListAsync(reopenedOuter, "/")).Select(e => e.Name).Order(StringComparer.Ordinal));
        var reopenedInner = await OpenOverAsync("inner2", new VfsPath("outer2", "/inner.zip"), register: false);
        Assert.Equal("added", await TextAsync(reopenedInner, "/added.txt"));
        Assert.Null(await reopenedInner.GetEntryAsync(new VfsPath("inner2", "/docs"), Ct));
        Assert.Equal("n", await TextAsync(outer, "/note.txt"));
        AssertNoLeftovers();
    }

    // ---- Zip64 ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Archive_of_more_than_65535_entries_is_written_as_zip64()
    {
        var archive = await OpenAsync();

        await using (var batch = await archive.BeginBatchAsync(Ct))
        {
            for (var i = 0; i < 65_536; i++) await archive.CreateDirectoryAsync(Arc($"/many/{i}"), Ct);
            await batch.CommitAsync(Ct);
        }

        var bytes = await HostBytesAsync();
        using (var check = new SysZip.ZipArchive(new MemoryStream(bytes), SysZip.ZipArchiveMode.Read))
            Assert.Equal(65_536 + 1 + 7, check.Entries.Count); // "many/", and sample.zip's 7 entries
        Assert.Equal(65_536, (await ListAsync(await ReopenAsync(), "/many")).Count);
    }

    [Theory]
    [InlineData(10, 1000L, false)]
    [InlineData(65_535, 1000L, false)]
    [InlineData(65_536, 1000L, true)]
    [InlineData(1, 4L * 1024 * 1024 * 1024, true)]
    [InlineData(2, 3L * 1024 * 1024 * 1024, true)] // together past the 4 GiB offsets
    [InlineData(2, 1L * 1024 * 1024 * 1024, false)]
    public void Zip64_is_used_for_many_entries_or_large_content(int entries, long eachSize, bool expected)
    {
        var sizes = Enumerable.Repeat(eachSize, entries).ToList();

        Assert.Equal(expected, ArchiveProvider.NeedsZip64(entries, sizes.Sum()));
        Assert.Equal(eachSize >= 4L * 1024 * 1024 * 1024, ArchiveProvider.EntryNeedsZip64(eachSize));
    }
}
