using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Operations;

public sealed class OperationQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly OperationQueue _queue = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_volume_runs_sequentially()
    {
        var first = new GatedOperation("mem:a");
        var second = new GatedOperation("mem:a", "mem:b");

        var firstDone = _queue.Enqueue(first);
        var secondDone = _queue.Enqueue(second);
        await first.Started.WaitAsync(Timeout, Ct);

        Assert.Equal(OperationState.Queued, second.State);
        Assert.False(second.HasRun);
        Assert.Equal([first, second], _queue.Operations);

        first.Finish();
        Assert.Equal(OperationState.Completed, (await firstDone.WaitAsync(Timeout, Ct)).State);
        await second.Started.WaitAsync(Timeout, Ct);
        Assert.Equal([second], _queue.Operations);

        second.Finish();
        Assert.Equal(OperationState.Completed, (await secondDone.WaitAsync(Timeout, Ct)).State);
        Assert.Empty(_queue.Operations);
    }

    [Fact]
    public async Task Different_volumes_run_in_parallel()
    {
        var first = new GatedOperation("mem:a");
        var second = new GatedOperation("mem:b");

        var firstDone = _queue.Enqueue(first);
        var secondDone = _queue.Enqueue(second);

        await Task.WhenAll(first.Started, second.Started).WaitAsync(Timeout, Ct);
        first.Finish();
        second.Finish();
        await Task.WhenAll(firstDone, secondDone).WaitAsync(Timeout, Ct);
        Assert.Empty(_queue.Operations);
    }

    [Fact]
    public async Task RunImmediately_bypasses_queue()
    {
        var first = new GatedOperation("mem:a");
        var second = new GatedOperation("mem:a");

        var firstDone = _queue.Enqueue(first);
        var secondDone = _queue.Enqueue(second, runImmediately: true);

        await Task.WhenAll(first.Started, second.Started).WaitAsync(Timeout, Ct);
        first.Finish();
        second.Finish();
        await Task.WhenAll(firstDone, secondDone).WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task RunImmediately_operation_occupies_its_volumes()
    {
        var urgent = new GatedOperation("mem:a");
        var later = new GatedOperation("mem:a");

        var urgentDone = _queue.Enqueue(urgent, runImmediately: true);
        var laterDone = _queue.Enqueue(later);
        await urgent.Started.WaitAsync(Timeout, Ct);
        Assert.False(later.HasRun);

        urgent.Finish();
        await urgentDone.WaitAsync(Timeout, Ct);
        await later.Started.WaitAsync(Timeout, Ct);
        later.Finish();
        await laterDone.WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task Cancelled_queued_operation_never_runs()
    {
        var first = new GatedOperation("mem:a");
        var cancelled = new GatedOperation("mem:a");
        var third = new GatedOperation("mem:a");

        var firstDone = _queue.Enqueue(first);
        var cancelledDone = _queue.Enqueue(cancelled);
        var thirdDone = _queue.Enqueue(third);
        await first.Started.WaitAsync(Timeout, Ct);

        cancelled.Cancel();
        // Completes while the operation it waited for is still running.
        Assert.Equal(OperationState.Cancelled, (await cancelledDone.WaitAsync(Timeout, Ct)).State);
        Assert.Equal(OperationState.Cancelled, cancelled.State);
        Assert.False(cancelled.HasRun);
        Assert.Equal([first, third], _queue.Operations);

        first.Finish();
        await third.Started.WaitAsync(Timeout, Ct);
        Assert.False(cancelled.HasRun);
        third.Finish();
        await Task.WhenAll(firstDone, thirdDone).WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task Failure_of_one_does_not_block_next()
    {
        var failing = new GatedOperation("mem:a")
        {
            RunError = new OperationRejectedException(RejectionReason.DestinationInsideSource, "rejected"),
        };
        var next = new GatedOperation("mem:a");

        var failingDone = _queue.Enqueue(failing);
        var nextDone = _queue.Enqueue(next);
        await failing.Started.WaitAsync(Timeout, Ct);
        failing.Finish();

        await Assert.ThrowsAsync<OperationRejectedException>(() => failingDone.WaitAsync(Timeout, Ct));
        Assert.Equal(OperationState.Failed, failing.State);

        await next.Started.WaitAsync(Timeout, Ct);
        next.Finish();
        Assert.Equal(OperationState.Completed, (await nextDone.WaitAsync(Timeout, Ct)).State);
    }

    [Fact]
    public async Task Volume_keys_failure_fails_the_operation()
    {
        var blocker = new GatedOperation("mem:a");
        var broken = new GatedOperation { KeysError = new FileNotFoundException("missing source") };
        var next = new GatedOperation("mem:a");

        var blockerDone = _queue.Enqueue(blocker);
        var brokenDone = _queue.Enqueue(broken);
        var nextDone = _queue.Enqueue(next);

        await Assert.ThrowsAsync<FileNotFoundException>(() => brokenDone.WaitAsync(Timeout, Ct));
        Assert.Equal(OperationState.Failed, broken.State);
        await Assert.ThrowsAsync<FileNotFoundException>(() => broken.Completion.WaitAsync(Timeout, Ct));
        Assert.False(broken.HasRun);

        await blocker.Started.WaitAsync(Timeout, Ct);
        blocker.Finish();
        await next.Started.WaitAsync(Timeout, Ct);
        next.Finish();
        await Task.WhenAll(blockerDone, nextDone).WaitAsync(Timeout, Ct);
        Assert.Empty(_queue.Operations);
    }

    [Fact]
    public async Task Waiting_operations_start_in_order_of_addition()
    {
        var running = new GatedOperation("mem:a");
        var wide = new GatedOperation("mem:a", "mem:b");
        var narrow = new GatedOperation("mem:b");

        var runningDone = _queue.Enqueue(running);
        var wideDone = _queue.Enqueue(wide);
        var narrowDone = _queue.Enqueue(narrow);
        await running.Started.WaitAsync(Timeout, Ct);

        // "narrow" does not overlap the running operation, but would overtake "wide" on mem:b.
        Assert.False(narrow.HasRun);

        running.Finish();
        await wide.Started.WaitAsync(Timeout, Ct);
        Assert.False(narrow.HasRun);
        wide.Finish();
        await narrow.Started.WaitAsync(Timeout, Ct);
        narrow.Finish();
        await Task.WhenAll(runningDone, wideDone, narrowDone).WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task Paused_operation_keeps_its_volumes()
    {
        var paused = new GatedOperation("mem:a") { PauseCheckpoint = true };
        var next = new GatedOperation("mem:a");

        var pausedDone = _queue.Enqueue(paused);
        var nextDone = _queue.Enqueue(next);
        await paused.Started.WaitAsync(Timeout, Ct);
        paused.Pause();
        paused.Finish(); // reaches its checkpoint and pauses there
        await paused.Paused.WaitAsync(Timeout, Ct);

        Assert.False(next.HasRun);

        paused.Resume();
        await pausedDone.WaitAsync(Timeout, Ct);
        await next.Started.WaitAsync(Timeout, Ct);
        next.Finish();
        await nextDone.WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task Changed_is_raised_when_operations_are_added_and_removed()
    {
        var op = new GatedOperation("mem:a");
        var snapshots = new List<IReadOnlyList<FileOperation>>();
        _queue.Changed += (_, _) =>
        {
            lock (snapshots) snapshots.Add(_queue.Operations);
        };

        var done = _queue.Enqueue(op);
        await op.Started.WaitAsync(Timeout, Ct);
        op.Finish();
        await done.WaitAsync(Timeout, Ct);

        lock (snapshots)
        {
            Assert.Equal(2, snapshots.Count);
            Assert.Equal([op], snapshots[0]);
            Assert.Empty(snapshots[1]);
        }
    }

    [Fact]
    public async Task Enqueuing_the_same_operation_twice_throws()
    {
        var op = new GatedOperation("mem:a");
        var done = _queue.Enqueue(op);

        Exception? error = null;
        try
        {
            _ = _queue.Enqueue(op);
        }
        catch (InvalidOperationException e)
        {
            error = e;
        }
        Assert.NotNull(error); // thrown synchronously, not through the returned task

        await op.Started.WaitAsync(Timeout, Ct);
        op.Finish();
        await done.WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task Throwing_queue_Changed_handler_does_not_disturb_the_queue()
    {
        _queue.Changed += (_, _) => throw new InvalidOperationException("handler");
        var first = new GatedOperation("mem:a");
        var failing = new GatedOperation("mem:a")
        {
            RunError = new OperationRejectedException(RejectionReason.DestinationInsideSource, "rejected"),
        };
        var last = new GatedOperation("mem:a");

        var firstDone = _queue.Enqueue(first);
        var failingDone = _queue.Enqueue(failing);
        var lastDone = _queue.Enqueue(last);

        await first.Started.WaitAsync(Timeout, Ct);
        first.Finish();
        Assert.Equal(OperationState.Completed, (await firstDone.WaitAsync(Timeout, Ct)).State);
        await failing.Started.WaitAsync(Timeout, Ct);
        failing.Finish();
        await Assert.ThrowsAsync<OperationRejectedException>(() => failingDone.WaitAsync(Timeout, Ct));
        await last.Started.WaitAsync(Timeout, Ct);
        last.Finish();
        Assert.Equal(OperationState.Completed, (await lastDone.WaitAsync(Timeout, Ct)).State);
        Assert.Empty(_queue.Operations);
    }

    [Fact]
    public async Task Throwing_operation_Changed_handler_on_failure_without_running_does_not_block_others()
    {
        var broken = new GatedOperation { DeferKeys = true };
        broken.Changed += (_, _) => throw new InvalidOperationException("handler");
        var next = new GatedOperation("mem:a");

        var brokenDone = _queue.Enqueue(broken);
        var nextDone = _queue.Enqueue(next); // waits behind broken, whose volumes are unknown
        Assert.False(next.HasRun);

        broken.FailKeys(new FileNotFoundException("missing source"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => brokenDone.WaitAsync(Timeout, Ct));
        Assert.False(broken.HasRun);

        await next.Started.WaitAsync(Timeout, Ct);
        next.Finish();
        await nextDone.WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task RunImmediately_operation_with_failing_volume_lookup_holds_all_volumes()
    {
        var urgent = new GatedOperation { KeysError = new FileNotFoundException("missing source") };
        var later = new GatedOperation("mem:a");

        var urgentDone = _queue.Enqueue(urgent, runImmediately: true);
        await urgent.Started.WaitAsync(Timeout, Ct);
        var laterDone = _queue.Enqueue(later);

        // A negative check needs a bounded wait: "later" must not start while "urgent" runs.
        var startedEarly = await Task.WhenAny(later.Started, Task.Delay(200, Ct)) == later.Started;
        Assert.False(startedEarly);

        urgent.Finish();
        await urgentDone.WaitAsync(Timeout, Ct);
        await later.Started.WaitAsync(Timeout, Ct);
        later.Finish();
        await laterDone.WaitAsync(Timeout, Ct);
    }

    /// <summary>An operation on the given volumes that runs until <see cref="Finish"/>.</summary>
    private sealed class GatedOperation(params string[] volumes) : FileOperation
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<IReadOnlyCollection<string>> _keys =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? KeysError { get; init; }

        /// <summary>The volume lookup stays pending until <see cref="FailKeys"/>.</summary>
        public bool DeferKeys { get; init; }

        public void FailKeys(Exception error) => _keys.TrySetException(error);
        public Exception? RunError { get; init; }

        /// <summary>After <see cref="Finish"/>, passes a pause checkpoint before ending.</summary>
        public bool PauseCheckpoint { get; init; }

        public Task Started => _started.Task;
        public Task Paused => _paused.Task;
        public bool HasRun => _started.Task.IsCompleted;

        public void Finish() => _gate.TrySetResult();

        public override OperationKind Kind => OperationKind.Copy;
        public override int ItemCount => 0;
        public override IReadOnlyList<VfsPath> Sources => [];
        public override VfsPath? Destination => null;

        public override Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct) =>
            DeferKeys ? _keys.Task
            : KeysError is { } error
                ? Task.FromException<IReadOnlyCollection<string>>(error)
                : Task.FromResult<IReadOnlyCollection<string>>(volumes);

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            SetState(OperationState.Running);
            _started.TrySetResult();
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            if (PauseCheckpoint)
            {
                Changed += (_, _) =>
                {
                    if (State == OperationState.Paused) _paused.TrySetResult();
                };
                await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            }
            if (RunError is not null) throw RunError;
        }
    }
}
