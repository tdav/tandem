using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Vfs.Local;

namespace Tandem.Core.Tests.Operations;

[SupportedOSPlatform("macos")]
public sealed class DeleteOperationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly InMemoryFileSystem _mem = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public DeleteOperationTests() => _providers.Register(_mem);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private DeleteOperation Delete(params string[] sources) =>
        new(_providers, sources.Select(Mem).ToArray(), toTrash: false, trash: null, _interaction);

    /// <summary>Makes deleting <paramref name="path"/> fail the first <paramref name="times"/> times (every time
    /// when null).</summary>
    private void FailDelete(string path, Func<Exception> error, int? times = null)
    {
        var left = times ?? int.MaxValue;
        _mem.FailOn((op, p) => op == "Delete" && p.Path == path && Interlocked.Decrement(ref left) >= 0, error);
    }

    /// <summary>Records each call; with <paramref name="fs"/>, also removes the item from it (as the real trash
    /// takes it away).</summary>
    private sealed class FakeTrash(InMemoryFileSystem? fs = null) : ITrash
    {
        private readonly Lock _lock = new();
        private readonly List<string> _calls = [];

        public Func<string, Exception?> Fail { get; set; } = _ => null;

        public IReadOnlyList<string> Calls
        {
            get { lock (_lock) return _calls.ToList(); }
        }

        public async Task MoveToTrashAsync(string localPath, CancellationToken ct)
        {
            lock (_lock) _calls.Add(localPath);
            if (Fail(localPath) is { } error) throw error;
            if (fs is not null) await fs.DeleteAsync(new VfsPath(fs.Id, localPath), recursive: true, ct);
        }
    }

    // ---- Permanent delete ---------------------------------------------------------------------------

    [Fact]
    public async Task Permanent_delete_removes_tree()
    {
        _mem.AddFile("/d/a.txt", "a");
        _mem.AddFile("/d/sub/b.txt", "b");
        _mem.AddDirectory("/d/sub/empty");
        _mem.AddFile("/f.txt", "f");
        _mem.AddFile("/keep.txt", "k");

        var op = Delete("/d", "/f.txt");
        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal(["/keep.txt"], _mem.AllPaths);
        Assert.Equal(3, op.Progress.FilesTotal);
        Assert.Equal(3, op.Progress.FilesDone);
    }

    [Fact]
    public async Task Permanent_delete_goes_depth_first_without_recursive_deletes()
    {
        _mem.AddFile("/d/sub/b.txt", "b");
        var deletes = new List<string>();
        _mem.FailOn((op, p) =>
        {
            if (op == "Delete") lock (deletes) deletes.Add(p.Path);
            return false;
        }, () => new IOException("unused"));

        await Delete("/d").RunAsync();

        Assert.Equal(["/d/sub/b.txt", "/d/sub", "/d"], deletes);
    }

    [Fact]
    public async Task Delete_error_skip_continues_with_rest()
    {
        _mem.AddFile("/d/a.txt", "a");
        _mem.AddFile("/d/b.txt", "b");
        _mem.AddFile("/e.txt", "e");
        FailDelete("/d/a.txt", () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await Delete("/d", "/e.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(new SkippedItem(Mem("/d/a.txt"), SkipReason.Error, "denied"), Assert.Single(result.Skipped));
        Assert.Single(_interaction.Errors); // the folder left behind is not asked about again
        Assert.Equal(["/d", "/d/a.txt"], _mem.AllPaths);
    }

    [Fact]
    public async Task Delete_error_retry_then_succeeds()
    {
        _mem.AddFile("/a.txt", "a");
        FailDelete("/a.txt", () => new IOException("busy"), times: 1);
        _interaction.OnError(ErrorAction.Retry);

        var result = await Delete("/a.txt").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Empty(_mem.AllPaths);
    }

    [Fact]
    public async Task Delete_error_skip_all_stops_asking()
    {
        _mem.AddFile("/a.txt", "a");
        _mem.AddFile("/b.txt", "b");
        FailDelete("/a.txt", () => new IOException("busy"));
        FailDelete("/b.txt", () => new IOException("busy"));
        _interaction.OnError(ErrorAction.SkipAll);

        var result = await Delete("/a.txt", "/b.txt").RunAsync();

        Assert.Equal(2, result.Skipped.Count);
        Assert.Single(_interaction.Errors);
    }

    [Fact]
    public async Task Delete_error_abort_fails()
    {
        _mem.AddFile("/a.txt", "a");
        _mem.AddFile("/b.txt", "b");
        FailDelete("/a.txt", () => new IOException("busy"));
        _interaction.OnError(ErrorAction.Abort);

        var op = Delete("/a.txt", "/b.txt");
        var error = await Assert.ThrowsAsync<IOException>(() => op.RunAsync());

        Assert.Equal("busy", error.Message);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal(["/a.txt", "/b.txt"], _mem.AllPaths);
    }

    [Fact]
    public async Task Unreadable_directory_skip_keeps_it()
    {
        _mem.AddFile("/d/locked/x.txt", "x");
        _mem.AddFile("/d/a.txt", "a");
        _mem.FailOn((op, p) => op == "Enumerate" && p.Path == "/d/locked", () => new VfsAccessDeniedException("denied"));
        _interaction.OnError(ErrorAction.Skip);

        var result = await Delete("/d").RunAsync();

        Assert.Equal(new SkippedItem(Mem("/d/locked"), SkipReason.Error, "denied"), Assert.Single(result.Skipped));
        Assert.Equal(["/d", "/d/locked", "/d/locked/x.txt"], _mem.AllPaths);
    }

    [Fact]
    public async Task Deleting_a_link_removes_the_link_only()
    {
        _mem.AddFile("/target/x.txt", "x");
        _mem.AddDirectory("/d");
        await _mem.CreateSymlinkAsync(Mem("/d/inner"), "/target", Ct);
        await _mem.CreateSymlinkAsync(Mem("/link"), "target", Ct);

        var result = await Delete("/link", "/d").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/target", "/target/x.txt"], _mem.AllPaths);
    }

    [Fact]
    public async Task Deleting_a_link_on_local_disk_keeps_its_target()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("target"));
        File.WriteAllText(temp.Combine("target/x.txt"), "x");
        Directory.CreateDirectory(temp.Combine("d"));
        File.CreateSymbolicLink(temp.Combine("d/inner"), temp.Combine("target"));
        File.CreateSymbolicLink(temp.Combine("link"), temp.Combine("target"));
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        var op = new DeleteOperation(providers, [temp.Root.Combine("link"), temp.Root.Combine("d")], toTrash: false,
            trash: null, _interaction);

        var result = await op.RunAsync().WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["target"], Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
        Assert.Equal("x", File.ReadAllText(temp.Combine("target/x.txt")));
    }

    [Fact]
    public async Task Cancel_stops_between_items()
    {
        _mem.AddFile("/a.txt", "a");
        _mem.AddFile("/b.txt", "b");
        var op = Delete("/a.txt", "/b.txt");
        _mem.FailOn((o, p) =>
        {
            if (o == "Delete" && p.Path == "/a.txt") op.Cancel();
            return false;
        }, () => new IOException("unused"));

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(["/b.txt"], _mem.AllPaths);
    }

    [Fact]
    public async Task Describes_itself_for_the_title()
    {
        _mem.SetVolumeKey("/a", "v1");
        var op = Delete("/a/x.txt", "/b.txt");

        Assert.Equal(OperationKind.Delete, op.Kind);
        Assert.Equal(2, op.ItemCount);
        Assert.Equal([Mem("/a/x.txt"), Mem("/b.txt")], op.Sources);
        Assert.Null(op.Destination);
        Assert.Equal(["mem:mem", "mem:v1"], (await op.GetVolumeKeysAsync(Ct)).Order());
    }

    // ---- Trash --------------------------------------------------------------------------------------

    [Fact]
    public async Task Trash_calls_ITrash_once_per_selected_item()
    {
        var local = new InMemoryFileSystem(VfsPath.LocalProviderId);
        var providers = new ProviderRegistry();
        providers.Register(local);
        for (var i = 0; i < 100; i++) local.AddFile($"/d/f{i}.txt", "x");
        local.AddFile("/x.txt", "x");
        var deletes = 0;
        local.FailOn((op, _) =>
        {
            if (op is "Delete" or "Enumerate") Interlocked.Increment(ref deletes);
            return false;
        }, () => new IOException("unused"));
        var trash = new FakeTrash();
        var op = new DeleteOperation(providers, [VfsPath.Local("/d"), VfsPath.Local("/x.txt")], toTrash: true, trash,
            _interaction);

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(["/d", "/x.txt"], trash.Calls);
        Assert.Equal(0, deletes); // no walk, no delete of its own
    }

    [Fact]
    public async Task Trash_error_skip_continues_with_rest()
    {
        var local = new InMemoryFileSystem(VfsPath.LocalProviderId);
        var providers = new ProviderRegistry();
        providers.Register(local);
        local.AddFile("/a.txt", "a");
        local.AddFile("/b.txt", "b");
        var trash = new FakeTrash(local) { Fail = p => p == "/a.txt" ? new IOException("no trash here") : null };
        _interaction.OnError(ErrorAction.Skip);
        var op = new DeleteOperation(providers, [VfsPath.Local("/a.txt"), VfsPath.Local("/b.txt")], toTrash: true,
            trash, _interaction);

        var result = await op.RunAsync();

        Assert.Equal(new SkippedItem(VfsPath.Local("/a.txt"), SkipReason.Error, "no trash here"),
            Assert.Single(result.Skipped));
        Assert.Equal(["/a.txt"], local.AllPaths);
    }

    [Fact]
    public async Task Trash_on_provider_without_trash_is_rejected()
    {
        _mem.AddFile("/a.txt", "a");
        var trash = new FakeTrash();
        var op = new DeleteOperation(_providers, [Mem("/a.txt")], toTrash: true, trash, _interaction);

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.TrashNotSupported, error.Reason);
        Assert.Equal(Mem("/a.txt"), error.SourcePath);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Empty(trash.Calls);
        Assert.Equal(["/a.txt"], _mem.AllPaths);
    }

    [Fact]
    public async Task Trash_without_a_trash_service_is_rejected()
    {
        var local = new InMemoryFileSystem(VfsPath.LocalProviderId);
        var providers = new ProviderRegistry();
        providers.Register(local);
        local.AddFile("/a.txt", "a");
        var op = new DeleteOperation(providers, [VfsPath.Local("/a.txt")], toTrash: true, trash: null, _interaction);

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.TrashNotSupported, error.Reason);
        Assert.Equal(["/a.txt"], local.AllPaths);
    }
}
