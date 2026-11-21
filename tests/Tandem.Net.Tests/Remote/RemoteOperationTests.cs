using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Vfs.Local;

namespace Tandem.Net.Tests.Remote;

/// <summary>The core operations engine over <see cref="Tandem.Net.Remote.RemoteProvider"/>: what the user sees when a
/// server drops the connection, refuses a rename, or is busy with another operation.</summary>
[SupportedOSPlatform("macos")]
public sealed class RemoteOperationTests : IAsyncDisposable
{
    private const string Content = "the whole new content, longer than the drop point";

    private readonly InMemoryFileSystem _mem = new();
    private readonly FakeRemoteServer _server = new();
    private readonly Tandem.Net.Remote.RemoteProvider _remote;
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _scripted = new();

    public RemoteOperationTests()
    {
        _remote = RemoteFixture.Provider(_server);
        _providers.Register(_mem);
        _providers.Register(_remote);
        _mem.AddFile("/src/a.txt", Content);
        _server.AddDirectory("/dst");
    }

    public ValueTask DisposeAsync() => _remote.DisposeAsync();

    private VfsPath R(string path) => new(_remote.Id, path);

    private Task<OperationResult> Upload() =>
        new CopyOperation(_providers, [new VfsPath("mem", "/src/a.txt")], R("/dst"), new CopyOptions(), _scripted)
            .RunAsync();

    private IEnumerable<string> ServiceFiles() =>
        _server.AllPaths.Where(p => SafeReplace.IsServiceName(p[(p.LastIndexOf('/') + 1)..]));

    [Fact]
    public async Task Upload_cut_off_and_retried_completes_and_leaves_no_part_or_backup()
    {
        _server.AddFile("/dst/a.txt", "old");
        _server.DropAfterBytes = 5;
        _scripted.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Retry);

        var result = await Upload();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.IsType<VfsConnectionLostException>(Assert.Single(_scripted.Errors).Error);
        Assert.Equal(Content, _server.ReadAllText("/dst/a.txt"));
        Assert.Empty(ServiceFiles());
        Assert.Equal(2, _server.ConnectCount);
    }

    [Fact]
    public async Task Upload_cut_off_and_skipped_keeps_the_previous_file_under_its_name()
    {
        _server.AddFile("/dst/a.txt", "old");
        _server.DropAfterBytes = 5;
        _scripted.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Skip);

        var result = await Upload();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Single(result.Skipped);
        Assert.Equal("old", _server.ReadAllText("/dst/a.txt"));
        Assert.Empty(ServiceFiles());
    }

    [Fact]
    public async Task Overwrite_whose_final_rename_fails_puts_the_old_file_back()
    {
        _server.AddFile("/dst/a.txt", "old");
        _server.RenameOverwrites = true;
        _server.FailOn("Rename", "/dst/.a.txt.tandem-part");
        _scripted.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Skip);

        var result = await Upload();

        Assert.Single(result.Skipped);
        Assert.Equal("old", _server.ReadAllText("/dst/a.txt"));
        Assert.Empty(ServiceFiles());
        Assert.Contains("#1 Rename /dst/a.txt -> /dst/.a.txt.tandem-old", _server.Log);
        Assert.Contains("#1 Rename /dst/.a.txt.tandem-old -> /dst/a.txt", _server.Log);
    }

    [Fact]
    public async Task Move_to_the_server_deletes_local_sources_only_after_the_upload_is_checked()
    {
        string? onServerWhenSourceDeleted = null;
        _mem.FailOn((operation, path) =>
        {
            if (operation == "Delete" && path.Path == "/src/a.txt")
                onServerWhenSourceDeleted = _server.Exists("/dst/a.txt") ? _server.ReadAllText("/dst/a.txt") : "";
            return false;
        }, () => new InvalidOperationException("never thrown"));
        Task<OperationResult> Move() =>
            new MoveOperation(_providers, [new VfsPath("mem", "/src/a.txt")], R("/dst"), new CopyOptions(), _scripted)
                .RunAsync();

        _server.FailCloseWrite = true; // the server's answer to the upload is an error
        _scripted.OnError(ErrorAction.Skip);
        var failed = await Move();

        Assert.Single(failed.Skipped);
        Assert.True(_mem.Exists("/src/a.txt"));
        Assert.Null(onServerWhenSourceDeleted);
        Assert.False(_server.Exists("/dst/a.txt"));
        Assert.Empty(ServiceFiles());

        _server.FailCloseWrite = false;
        var moved = await Move();

        Assert.Equal(OperationState.Completed, moved.State);
        Assert.Empty(moved.Skipped);
        Assert.False(_mem.Exists("/src/a.txt"));
        Assert.Equal(Content, onServerWhenSourceDeleted);
        Assert.Empty(ServiceFiles());
    }

    [Fact]
    public async Task Download_cut_off_leaves_no_part_file_on_disk()
    {
        using var temp = new TempDir();
        var local = new LocalFileSystemProvider(new UnrestrictedAccessBroker());
        var providers = new ProviderRegistry();
        providers.Register(local);
        providers.Register(_remote);
        _server.AddFile("/src/big.bin", new byte[100_000]);
        _server.DropAfterBytes = 1000;
        _scripted.OnError(ErrorAction.Skip);

        var result = await new CopyOperation(providers, [R("/src/big.bin")], temp.Root, new CopyOptions(), _scripted)
            .RunAsync();

        Assert.Single(result.Skipped);
        Assert.IsType<VfsConnectionLostException>(Assert.Single(_scripted.Errors).Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Operations_on_one_server_queue_up_and_on_two_servers_run_side_by_side()
    {
        var otherServer = new FakeRemoteServer();
        otherServer.AddDirectory("/dst");
        await using var other = RemoteFixture.Provider(otherServer, "srv2", RemoteFixture.Profile("other.test"));
        _providers.Register(other);
        foreach (var name in new[] { "s1", "s2", "s3" })
        {
            _mem.AddFile($"/{name}/{name}.txt", name);
            _mem.SetVolumeKey("/" + name, name); // separate sources: only the destinations are shared
        }
        _server.AddFile("/dst/s1.txt", "old");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new HeldConflict(gate.Task);
        CopyOperation Copy(string name, VfsPath destination) =>
            new(_providers, [new VfsPath("mem", $"/{name}/{name}.txt")], destination, new CopyOptions(), interaction);
        var queue = new OperationQueue();

        var first = Copy("s1", R("/dst"));
        var firstDone = queue.Enqueue(first);
        await interaction.Asked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = Copy("s2", R("/dst"));
        var secondDone = queue.Enqueue(second);
        var third = Copy("s3", new VfsPath(other.Id, "/dst"));
        var thirdDone = queue.Enqueue(third);

        Assert.Equal(OperationState.Completed,
            (await thirdDone.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).State);
        Assert.Equal(OperationState.Queued, second.State);
        Assert.False(_server.Exists("/dst/s2.txt"));

        gate.SetResult();
        Assert.Equal(OperationState.Completed, (await firstDone).State);
        Assert.Equal(OperationState.Completed, (await secondDone).State);
        Assert.Equal("s1", _server.ReadAllText("/dst/s1.txt"));
        Assert.Equal("s2", _server.ReadAllText("/dst/s2.txt"));
        Assert.Equal("s3", otherServer.ReadAllText("/dst/s3.txt"));
    }

    /// <summary>Holds the conflict question until <paramref name="gate"/> opens, then overwrites.</summary>
    private sealed class HeldConflict(Task gate) : IOperationInteraction
    {
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Asked => _asked.Task;

        public async Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing,
            CancellationToken ct)
        {
            _asked.TrySetResult();
            await gate.WaitAsync(ct);
            return new ConflictDecision(ConflictAction.Overwrite, ApplyToAll: false);
        }

        public Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct) =>
            throw new InvalidOperationException($"Unexpected error for '{item.Path}': {error.Message}");
    }
}
