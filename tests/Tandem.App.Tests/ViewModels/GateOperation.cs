using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

/// <summary>An operation that runs until the test releases it; it can report progress and honours pause and
/// cancel at <see cref="CheckpointAsync"/>.</summary>
internal sealed class GateOperation(string volume = "v", string name = "a.txt") : FileOperation
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _checkpoint = new(0);

    public override OperationKind Kind => OperationKind.Copy;
    public override int ItemCount => 1;
    public override IReadOnlyList<VfsPath> Sources { get; } = [VfsPath.Local("/src/" + name)];
    public override VfsPath? Destination => VfsPath.Local("/dst");

    public Task Started => _started.Task;

    public void Release() => _release.TrySetResult();

    /// <summary>Lets the running operation pass one pause checkpoint.</summary>
    public void Checkpoint() => _checkpoint.Release();

    public void Report(long done, long total)
    {
        SetTotals(1, total);
        AddBytesDone(done - Progress.BytesDone);
    }

    public override Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<string>>([volume]);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        SetState(OperationState.Running);
        _started.TrySetResult();
        while (!_release.Task.IsCompleted)
        {
            var next = _checkpoint.WaitAsync(ct);
            await Task.WhenAny(next, _release.Task.WaitAsync(ct));
            ct.ThrowIfCancellationRequested();
            if (next.IsCompletedSuccessfully) await WaitWhilePausedAsync(ct);
        }
    }
}
