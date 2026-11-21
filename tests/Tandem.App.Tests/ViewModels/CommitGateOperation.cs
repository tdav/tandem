using Tandem.Core.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>An operation whose only work is a batch on a provider that takes batches; the batch's commit waits until
/// the test releases it, so the operation stays in its commit (<see cref="FileOperation.IsCommitting"/>).</summary>
internal sealed class CommitGateOperation : FileOperation
{
    private readonly ProviderRegistry _providers = new();
    private readonly GatedBatchProvider _provider = new();

    public CommitGateOperation() => _providers.Register(_provider);

    public override OperationKind Kind => OperationKind.Copy;
    public override int ItemCount => 1;
    public override IReadOnlyList<VfsPath> Sources { get; } = [VfsPath.Local("/src/a.txt")];
    public override VfsPath? Destination => new VfsPath("gated", "/");

    /// <summary>Completes once the commit has started.</summary>
    public Task Committing => _provider.Committing.Task;

    public void Release() => _provider.Release.TrySetResult();

    public override Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<string>>(["gated"]);

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        SetState(OperationState.Running);
        return RunInBatchAsync(_providers, [new VfsPath("gated", "/")], _ => Task.CompletedTask, afterCommit: null,
            ct);
    }

    private sealed class GatedBatchProvider : IFileSystemProvider, IBatchProvider
    {
        private readonly InMemoryFileSystem _inner = new("gated");

        public TaskCompletionSource Committing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IBatchProvider? BatchHost => null;

        public Task<IProviderBatch> BeginBatchAsync(CancellationToken ct) =>
            Task.FromResult<IProviderBatch>(new Batch(this));

        private sealed class Batch(GatedBatchProvider owner) : IProviderBatch
        {
            public Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct) => Task.CompletedTask;

            public Task CommitAsync(CancellationToken ct)
            {
                owner.Committing.TrySetResult();
                return owner.Release.Task;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public string Id => _inner.Id;
        public ProviderCapabilities Capabilities => _inner.Capabilities;
        public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
            _inner.EnumerateAsync(directory, ct);
        public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => _inner.GetEntryAsync(path, ct);
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => _inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => _inner.OpenWriteAsync(path, ct);
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => _inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
            _inner.DeleteAsync(path, recursive, ct);
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
            _inner.MoveAsync(source, destination, ct);
        public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
            _inner.CreateSymlinkAsync(path, target, ct);
        public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
            _inner.SetModifiedAsync(path, modified, ct);
        public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) =>
            _inner.SetModeAsync(path, mode, ct);
        public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) =>
            _inner.IsSameEntryAsync(a, b, ct);
        public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => _inner.GetVolumeKeyAsync(path, ct);
        public IDisposable? Watch(VfsPath directory, Action changed) => _inner.Watch(directory, changed);
    }
}
