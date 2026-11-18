using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Vfs.Local;

namespace Tandem.Core.Tests.Operations;

[SupportedOSPlatform("macos")]
public sealed class MoveOperationTests
{
    private const int BigSize = 4 * 1024 * 1024 + 123; // several 1 MB copy blocks
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly InMemoryFileSystem _mem = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public MoveOperationTests() => _providers.Register(_mem);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private MoveOperation MoveInto(string destination, CopyOptions? options, params string[] sources) =>
        new(_providers, sources.Select(Mem).ToArray(), Mem(destination), options ?? new CopyOptions(), _interaction);

    private MoveOperation Move(params string[] sources) => MoveInto("/dst", null, sources);

    /// <summary>/src and /dst on different volumes of the same provider.</summary>
    private void SeparateVolumes()
    {
        _mem.SetVolumeKey("/src", "v1");
        _mem.SetVolumeKey("/dst", "v2");
    }

    /// <summary>Any write fails: proves that a move did not copy.</summary>
    private void ForbidWrites() =>
        _mem.FailOn((op, _) => op == "OpenWrite", () => new InvalidOperationException("the move copied"));

    private IEnumerable<string> Under(string prefix) =>
        _mem.AllPaths.Where(p => p == prefix || p.StartsWith(prefix + "/", StringComparison.Ordinal));

    private void AssertNoPartFiles() =>
        Assert.DoesNotContain(_mem.AllPaths, p => p.EndsWith(".tandem-part", StringComparison.Ordinal) ||
                                                  p.EndsWith(".tandem-old", StringComparison.Ordinal));

    // ---- Same volume: rename ------------------------------------------------------------------------

    [Fact]
    public async Task Same_volume_uses_rename()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/src/dir/b.txt", "b");
        _mem.AddDirectory("/dst");
        ForbidWrites();

        var result = await Move("/src/a.txt", "/src/dir").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal(["/src"], Under("/src"));
        Assert.Equal(["/dst", "/dst/a.txt", "/dst/dir", "/dst/dir/b.txt"], Under("/dst"));
        Assert.Equal("b", _mem.ReadAllText("/dst/dir/b.txt"));
    }

    [Fact]
    public async Task Rename_in_place()
    {
        _mem.AddFile("/src/a.txt", "a");
        ForbidWrites();

        var result = await MoveInto("/src", new CopyOptions(NewName: "b.txt"), "/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/src", "/src/b.txt"], Under("/src"));
        Assert.Equal("a", _mem.ReadAllText("/src/b.txt"));
    }

    [Fact]
    public async Task Case_only_rename_works()
    {
        _mem.AddFile("/src/a.txt", "a");

        var result = await MoveInto("/src", new CopyOptions(NewName: "A.txt"), "/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/src", "/src/A.txt"], Under("/src"));
    }

    [Fact]
    public async Task Case_only_rename_works_on_local_disk()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("a.txt"), "a");
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        var op = new MoveOperation(providers, [temp.Root.Combine("a.txt")], temp.Root,
            new CopyOptions(NewName: "A.txt"), _interaction);

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["A.txt"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
        Assert.Equal("a", File.ReadAllText(temp.Combine("A.txt")));
    }

    [Fact]
    public async Task Rejects_move_into_itself()
    {
        _mem.AddFile("/src/dir/sub/a.txt", "a");
        var before = _mem.AllPaths;

        var op = MoveInto("/src/dir/sub", null, "/src/dir");
        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.DestinationInsideSource, error.Reason);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal(before, _mem.AllPaths);
    }

    [Fact]
    public async Task Rejects_move_onto_itself()
    {
        _mem.AddFile("/src/a.txt", "a");

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => MoveInto("/src", null, "/src/a.txt").RunAsync());

        Assert.Equal(RejectionReason.SourceEqualsDestination, error.Reason);
        Assert.Equal("a", _mem.ReadAllText("/src/a.txt"));
    }

    [Fact]
    public async Task Same_volume_conflict_skip_keeps_both()
    {
        _mem.AddFile("/src/a.txt", "new");
        _mem.AddFile("/dst/a.txt", "old");
        _interaction.OnConflict(ConflictAction.Skip);

        var result = await Move("/src/a.txt").RunAsync();

        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.ConflictSkipped), Assert.Single(result.Skipped));
        Assert.Equal("new", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
    }

    [Fact]
    public async Task Same_volume_conflict_overwrite_replaces()
    {
        _mem.AddFile("/src/a.txt", "new");
        _mem.AddFile("/dst/a.txt", "old");
        _interaction.OnConflict(ConflictAction.Overwrite);
        ForbidWrites();

        var result = await Move("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/src"], Under("/src"));
        Assert.Equal(["/dst", "/dst/a.txt"], Under("/dst"));
        Assert.Equal("new", _mem.ReadAllText("/dst/a.txt"));
    }

    [Fact]
    public async Task Same_volume_failed_overwrite_keeps_both_files()
    {
        _mem.AddFile("/src/a.txt", "new");
        _mem.AddFile("/dst/a.txt", "old");
        // The old file is set aside first; moving the source into place then fails.
        _mem.FailOn((op, p) => op == "Move" && p.Path == "/src/a.txt", () => new IOException("rename failed"));
        _interaction.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Skip);

        var result = await Move("/src/a.txt").RunAsync();

        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "rename failed"), Assert.Single(result.Skipped));
        Assert.Equal("new", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal("old", _mem.ReadAllText("/dst/a.txt"));
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Same_volume_conflict_rename_picks_a_free_name()
    {
        _mem.AddFile("/src/a.txt", "new");
        _mem.AddFile("/dst/a.txt", "old");
        _interaction.OnConflict(ConflictAction.Rename);

        await Move("/src/a.txt").RunAsync();

        Assert.Equal(["/dst", "/dst/a (2).txt", "/dst/a.txt"], Under("/dst"));
        Assert.Equal("new", _mem.ReadAllText("/dst/a (2).txt"));
        Assert.False(_mem.Exists("/src/a.txt"));
    }

    [Fact]
    public async Task Same_volume_overwrite_older_keeps_newer_destination()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _mem.AddFile("/src/a.txt", "older", t);
        _mem.AddFile("/dst/a.txt", "newer", t.AddDays(1));
        _interaction.OnConflict(ConflictAction.OverwriteOlder);

        var result = await Move("/src/a.txt").RunAsync();

        Assert.Equal(SkipReason.OlderThanDestination, Assert.Single(result.Skipped).Reason);
        Assert.Equal("older", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal("newer", _mem.ReadAllText("/dst/a.txt"));
    }

    [Fact]
    public async Task Same_volume_conflict_cancel_ends_cancelled()
    {
        _mem.AddFile("/src/a.txt", "new");
        _mem.AddFile("/src/b.txt", "b");
        _mem.AddFile("/dst/a.txt", "old");
        _interaction.OnConflict(ConflictAction.Cancel);

        var result = await Move("/src/a.txt", "/src/b.txt").RunAsync();

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(["/src", "/src/a.txt", "/src/b.txt"], Under("/src"));
    }

    [Fact]
    public async Task Same_volume_directory_into_existing_directory_merges()
    {
        _mem.AddFile("/src/dir/a.txt", "a");
        _mem.AddFile("/src/dir/sub/c.txt", "c");
        _mem.AddFile("/dst/dir/b.txt", "b");

        var result = await Move("/src/dir").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/src"], Under("/src"));
        Assert.Equal(["/dst", "/dst/dir", "/dst/dir/a.txt", "/dst/dir/b.txt", "/dst/dir/sub", "/dst/dir/sub/c.txt"],
            Under("/dst"));
    }

    [Fact]
    public async Task Same_volume_rename_error_skip_keeps_source()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/src/b.txt", "b");
        _mem.AddDirectory("/dst");
        _mem.FailOn((op, p) => op == "Move" && p.Path == "/src/a.txt", () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await Move("/src/a.txt", "/src/b.txt").RunAsync();

        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "denied"), Assert.Single(result.Skipped));
        Assert.Equal(["/src", "/src/a.txt"], Under("/src"));
        Assert.Equal(["/dst", "/dst/b.txt"], Under("/dst"));
    }

    [Fact]
    public async Task Same_volume_decision_uses_resolved_destination()
    {
        _mem.SetVolumeKey("/src", "v1");
        _mem.SetVolumeKey("/real", "v1"); // the link itself sits on the default volume
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/real");
        await _mem.CreateSymlinkAsync(Mem("/dstlink"), "real", Ct);
        ForbidWrites();

        var result = await MoveInto("/dstlink", null, "/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", _mem.ReadAllText("/real/a.txt"));
        Assert.False(_mem.Exists("/src/a.txt"));
    }

    // ---- Across volumes: copy, confirm, delete ------------------------------------------------------

    [Fact]
    public async Task Cross_volume_copies_then_deletes_source()
    {
        SeparateVolumes();
        _mem.AddFile("/src/dir/a.txt", "a");
        _mem.AddFile("/src/dir/sub/b.txt", "b");
        _mem.AddDirectory("/src/dir/empty");
        _mem.AddFile("/src/c.txt", "c");
        _mem.AddDirectory("/dst");
        var writes = 0;
        _mem.FailOn((op, _) =>
        {
            if (op == "OpenWrite") Interlocked.Increment(ref writes);
            return false;
        }, () => new IOException("unused"));

        var result = await Move("/src/dir", "/src/c.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal(3, writes); // really copied
        Assert.Equal(["/src"], Under("/src"));
        Assert.Equal(["/dst", "/dst/c.txt", "/dst/dir", "/dst/dir/a.txt", "/dst/dir/empty", "/dst/dir/sub",
            "/dst/dir/sub/b.txt"], Under("/dst"));
        Assert.Equal("b", _mem.ReadAllText("/dst/dir/sub/b.txt"));
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Cross_provider_move_works()
    {
        var other = new InMemoryFileSystem("mem2");
        _providers.Register(other);
        _mem.AddFile("/src/dir/a.txt", "a");
        other.AddDirectory("/dst");
        var op = new MoveOperation(_providers, [Mem("/src/dir")], new VfsPath("mem2", "/dst"), new CopyOptions(),
            _interaction);

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/src"], Under("/src"));
        Assert.Equal(["/dst", "/dst/dir", "/dst/dir/a.txt"], other.AllPaths);
        Assert.Equal("a", other.ReadAllText("/dst/dir/a.txt"));
    }

    [Fact]
    public async Task Skipped_item_stays_in_source_and_parent_kept()
    {
        SeparateVolumes();
        _mem.AddFile("/src/dir/a.txt", "a");
        _mem.AddFile("/src/dir/b.txt", "new b");
        _mem.AddFile("/src/dir/c.txt", "c");
        _mem.AddFile("/dst/dir/b.txt", "old b");
        _interaction.OnConflict(ConflictAction.Skip);

        var result = await Move("/src/dir").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(new SkippedItem(Mem("/src/dir/b.txt"), SkipReason.ConflictSkipped), Assert.Single(result.Skipped));
        Assert.Equal(["/src", "/src/dir", "/src/dir/b.txt"], Under("/src"));
        Assert.Equal("new b", _mem.ReadAllText("/src/dir/b.txt"));
        Assert.Equal(["/dst", "/dst/dir", "/dst/dir/a.txt", "/dst/dir/b.txt", "/dst/dir/c.txt"], Under("/dst"));
        Assert.Equal("old b", _mem.ReadAllText("/dst/dir/b.txt"));
    }

    [Fact]
    public async Task Failed_copy_never_deletes_source()
    {
        SeparateVolumes();
        _mem.AddFile("/src/a.txt", "the content");
        _mem.AddFile("/src/b.txt", "b");
        _mem.AddDirectory("/dst");
        _mem.FailWriteAfter("/dst/.a.txt.tandem-part", 3, () => new IOException("disk full"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await Move("/src/a.txt", "/src/b.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "disk full"), Assert.Single(result.Skipped));
        Assert.Equal("the content", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal(["/dst", "/dst/b.txt"], Under("/dst"));
        Assert.False(_mem.Exists("/src/b.txt"));
        AssertNoPartFiles();
    }

    [Fact]
    public async Task Size_mismatch_keeps_source()
    {
        SeparateVolumes();
        _mem.AddFile("/src/a.txt", "the full content");
        _mem.AddDirectory("/dst");
        // The copy reads (and writes) fewer bytes than the file has, without any error.
        _mem.ChangeContentOnRead("/src/a.txt", 1, Encoding.UTF8.GetBytes("the full"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await Move("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        var (item, error) = Assert.Single(_interaction.Errors);
        Assert.Equal(Mem("/src/a.txt"), item);
        Assert.IsType<ContentMismatchException>(error);
        Assert.Equal(SkipReason.Error, Assert.Single(result.Skipped).Reason);
        Assert.Equal("the full content", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal(["/dst"], Under("/dst"));
    }

    [Fact]
    public async Task Size_mismatch_can_be_retried()
    {
        SeparateVolumes();
        _mem.AddFile("/src/a.txt", "the full content");
        _mem.AddDirectory("/dst");
        _mem.ChangeContentOnRead("/src/a.txt", 1, Encoding.UTF8.GetBytes("the full"));
        _interaction.OnError(ErrorAction.Retry);

        var result = await Move("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal("the full content", _mem.ReadAllText("/dst/a.txt"));
        Assert.False(_mem.Exists("/src/a.txt"));
    }

    [Fact]
    public async Task Verify_mismatch_keeps_source()
    {
        SeparateVolumes();
        _mem.AddFile("/src/a.txt", "the content");
        _mem.AddDirectory("/dst");
        // Same length, other bytes, on the verification read: only the hash check can see it.
        _mem.ChangeContentOnRead("/src/a.txt", 2, Encoding.UTF8.GetBytes("THE CONTENT"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await MoveInto("/dst", new CopyOptions(VerifyContent: true), "/src/a.txt").RunAsync();

        Assert.IsType<ContentMismatchException>(Assert.Single(_interaction.Errors).Error);
        Assert.Equal(SkipReason.Error, Assert.Single(result.Skipped).Reason);
        Assert.Equal("the content", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal(["/dst"], Under("/dst"));
    }

    [Fact]
    public async Task Failed_source_delete_is_asked_and_skip_keeps_both()
    {
        SeparateVolumes();
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        _mem.FailOn((op, p) => op == "Delete" && p.Path == "/src/a.txt", () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await Move("/src/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(new SkippedItem(Mem("/src/a.txt"), SkipReason.Error, "denied"), Assert.Single(result.Skipped));
        Assert.Equal("a", _mem.ReadAllText("/src/a.txt"));
        Assert.Equal("a", _mem.ReadAllText("/dst/a.txt"));
    }

    [Fact]
    public async Task Cross_volume_symlink_is_moved_as_a_link()
    {
        SeparateVolumes();
        _mem.AddFile("/src/target/x.txt", "x");
        await _mem.CreateSymlinkAsync(Mem("/src/link"), "target", Ct);
        _mem.AddDirectory("/dst");

        var result = await Move("/src/link").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/src", "/src/target", "/src/target/x.txt"], Under("/src"));
        Assert.Equal("target", (await _mem.GetEntryAsync(Mem("/dst/link"), Ct))!.LinkTarget);
    }

    [Fact]
    public async Task Cancel_mid_way_keeps_moved_items_and_the_one_in_flight()
    {
        SeparateVolumes();
        var big = new byte[BigSize];
        new Random(42).NextBytes(big);
        _mem.AddFile("/src/a.txt", "first");
        _mem.AddFile("/src/b.bin", big);
        _mem.AddFile("/src/c.txt", "third");
        _mem.AddDirectory("/dst");
        _mem.FailOn((op, p) =>
        {
            if (op == "OpenRead" && p.Path == "/src/b.bin") Thread.Sleep(150); // see CopyControlTests
            return false;
        }, () => new IOException("unused"));
        var op = Move("/src/a.txt", "/src/b.bin", "/src/c.txt");
        long bytesAtCancel = -1;
        op.Changed += (_, _) =>
        {
            if (op.Progress is not { CurrentItem: "/src/b.bin", BytesDone: > 5 } || bytesAtCancel >= 0) return;
            bytesAtCancel = op.Progress.BytesDone;
            op.Cancel();
        };

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.InRange(bytesAtCancel, 6, 5 + BigSize - 1); // it really was mid-file
        Assert.Equal(["/dst", "/dst/a.txt"], Under("/dst"));
        Assert.Equal(["/src", "/src/b.bin", "/src/c.txt"], Under("/src"));
        Assert.Equal(BigSize, (await _mem.GetEntryAsync(Mem("/src/b.bin"), Ct))!.Size);
    }

    [Fact]
    public void Describes_itself_for_the_title()
    {
        var op = Move("/src/a.txt", "/src/b.txt");

        Assert.Equal(OperationKind.Move, op.Kind);
        Assert.Equal(2, op.ItemCount);
        Assert.Equal([Mem("/src/a.txt"), Mem("/src/b.txt")], op.Sources);
        Assert.Equal(Mem("/dst"), op.Destination);
    }

    [Fact]
    public async Task Volume_keys_cover_sources_and_destination()
    {
        SeparateVolumes();

        var keys = await Move("/src/a.txt").GetVolumeKeysAsync(Ct);

        Assert.Equal(["mem:v1", "mem:v2"], keys.Order());
    }
}
