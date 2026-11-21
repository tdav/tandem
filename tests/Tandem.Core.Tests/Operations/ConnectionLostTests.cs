using Tandem.Core.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Operations;

/// <summary>The connection to the destination is lost in the middle of writing a file: the engine asks, and the
/// answer is an ordinary Retry or Abort (the source reconnects by itself on the next call).</summary>
public sealed class ConnectionLostTests
{
    private const string Content = "the whole new content";

    private readonly InMemoryFileSystem _mem = new();
    private readonly DroppingProvider _remote = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _scripted = new();

    public ConnectionLostTests()
    {
        _providers.Register(_mem);
        _providers.Register(_remote);
        _mem.AddFile("/src/a.txt", Content);
        _remote.Inner.AddDirectory("/dst");
    }

    private Task<OperationResult> CopyToRemote() =>
        new CopyOperation(_providers, [new VfsPath("mem", "/src/a.txt")], new VfsPath(_remote.Id, "/dst"),
            new CopyOptions(), new ReconnectWhenAsked(_scripted, _remote)).RunAsync();

    private IEnumerable<string> ServiceFiles() =>
        _remote.Inner.AllPaths.Where(p => p.EndsWith(SafeReplace.PartSuffix, StringComparison.Ordinal) ||
                                          p.EndsWith(SafeReplace.BackupSuffix, StringComparison.Ordinal));

    [Fact]
    public async Task Copy_retried_after_lost_connection_completes_and_leaves_no_part_file()
    {
        _remote.DropAfterBytes = 5;
        _scripted.OnError(ErrorAction.Retry);

        var result = await CopyToRemote();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.IsType<VfsConnectionLostException>(Assert.Single(_scripted.Errors).Error);
        Assert.Equal(Content, _remote.Inner.ReadAllText("/dst/a.txt"));
        Assert.Empty(ServiceFiles());
    }

    [Fact]
    public async Task Copy_aborted_after_lost_connection_keeps_the_old_destination_file()
    {
        _remote.Inner.AddFile("/dst/a.txt", "old");
        _remote.DropAfterBytes = 5;
        _scripted.OnConflict(ConflictAction.Overwrite).OnError(ErrorAction.Abort);

        await Assert.ThrowsAsync<VfsConnectionLostException>(CopyToRemote);

        Assert.Equal("old", _remote.Inner.ReadAllText("/dst/a.txt"));
        Assert.DoesNotContain(_remote.Inner.AllPaths, p => p.EndsWith(SafeReplace.BackupSuffix, StringComparison.Ordinal));
    }

    /// <summary>As a server that comes back while the user reads the question: the clean-up right after the drop
    /// still fails (the part file stays behind), the calls after the answer succeed.</summary>
    private sealed class ReconnectWhenAsked(ScriptedInteraction inner, DroppingProvider remote) : IOperationInteraction
    {
        public Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct) =>
            inner.ResolveConflictAsync(source, existing, ct);

        public Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct)
        {
            remote.ReconnectOnNextCall = true;
            return inner.ResolveErrorAsync(item, error, ct);
        }
    }
}
