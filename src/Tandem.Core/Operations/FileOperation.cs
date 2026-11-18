using System.Diagnostics;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

public enum OperationKind { Copy, Move, Delete }

/// <summary>
/// A long-running file operation (copy, move, delete). It runs once, on the thread pool; state and progress may
/// be read from any thread, and <see cref="Changed"/> is raised on the operation's background thread — on every
/// state change and, for progress, at most <see cref="MaxProgressEventsPerSecond"/> times a second.
/// Outcomes: <see cref="OperationState.Completed"/> and <see cref="OperationState.Cancelled"/> complete
/// <see cref="Completion"/> with a result; <see cref="OperationState.Failed"/> faults it with the error
/// (an <see cref="OperationRejectedException"/> when the operation was refused before it started).
/// </summary>
public abstract class FileOperation
{
    public const int MaxProgressEventsPerSecond = 10;
    private static readonly TimeSpan ProgressEventInterval = TimeSpan.FromSeconds(1.0 / MaxProgressEventsPerSecond);

    private readonly TaskCompletionSource<OperationResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = new();
    private readonly List<SkippedItem> _skipped = [];
    private int _started;

    // Guarded by _lock.
    private OperationState _state = OperationState.Queued;
    private TimeSpan? _runningSince;
    private TimeSpan _pausedFor; // completed pauses
    private TimeSpan? _pausedSince; // the current pause
    private TimeSpan? _lastProgressEvent;
    private string? _currentItem;
    private TaskCompletionSource? _resume; // set while a pause is requested; completed by Resume
    private long _filesDone, _filesTotal, _bytesDone, _bytesTotal;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>What the operation does; with <see cref="ItemCount"/> and <see cref="Destination"/> the
    /// interface builds a title.</summary>
    public abstract OperationKind Kind { get; }

    /// <summary>How many items the user selected (top-level sources, not everything inside them).</summary>
    public abstract int ItemCount { get; }

    /// <summary>The items the user selected (top-level sources), in the given order; lets the interface name a
    /// single item in the title.</summary>
    public abstract IReadOnlyList<VfsPath> Sources { get; }

    /// <summary>The directory the items go to; null for operations without one (delete).</summary>
    public abstract VfsPath? Destination { get; }

    public OperationState State
    {
        get { lock (_lock) return _state; }
    }

    public OperationProgress Progress
    {
        get { lock (_lock) return SnapshotProgress(); }
    }

    public event EventHandler? Changed;

    /// <summary>The outcome; pending until the operation has run (or forever, if it is never run).</summary>
    public Task<OperationResult> Completion => _completion.Task;

    /// <summary>Starts the operation on the thread pool and returns <see cref="Completion"/>. Failures, including
    /// rejections, surface through the returned task, never synchronously.</summary>
    /// <exception cref="InvalidOperationException">The operation was already started.</exception>
    public Task<OperationResult> RunAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("The operation has already been started.");
        _ = Task.Run(RunCoreAsync);
        return _completion.Task;
    }

    /// <summary>Asks a started operation to pause. It stops at its next checkpoint (between scanned entries, between
    /// items, and between the blocks of a file it copies or verifies itself), then reports <see cref="OperationState.Paused"/> until
    /// <see cref="Resume"/> or <see cref="Cancel"/>. A file handed to the platform's own copy finishes before the
    /// pause takes effect. Before <see cref="RunAsync"/>, after the end, or while already paused it does nothing.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (Volatile.Read(ref _started) == 0 || IsFinal(_state) || _resume is not null) return;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Lets a paused operation go on (or withdraws a pause that has not taken effect yet); otherwise it
    /// does nothing.</summary>
    public void Resume()
    {
        TaskCompletionSource? resume;
        lock (_lock)
        {
            resume = _resume;
            _resume = null;
        }
        resume?.TrySetResult();
    }

    /// <summary>Asks the operation to stop; it ends as <see cref="OperationState.Cancelled"/>, also when paused. A
    /// file being written is abandoned between blocks and its part file removed. Calling it before
    /// <see cref="RunAsync"/> makes the run end immediately; calling it after the end has no effect.</summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>Volume keys of every volume the operation touches, for scheduling.</summary>
    public abstract Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct);

    /// <summary>The operation's work. Throw <see cref="OperationCanceledException"/> for <paramref name="ct"/> to
    /// end as cancelled; any other exception ends it as failed.</summary>
    protected abstract Task ExecuteAsync(CancellationToken ct);

    /// <summary>Moves to <paramref name="state"/> and raises <see cref="Changed"/>.</summary>
    protected void SetState(OperationState state)
    {
        lock (_lock)
        {
            _state = state;
            if (state == OperationState.Running) _runningSince ??= _clock.Elapsed;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected internal void SetTotals(long files, long bytes)
    {
        lock (_lock)
        {
            _filesTotal = files;
            _bytesTotal = bytes;
        }
        OnProgress();
    }

    protected internal void BeginItem(string item)
    {
        lock (_lock) _currentItem = item;
        OnProgress();
    }

    /// <summary>Counts copied bytes; a negative value takes back the bytes of an attempt that failed.</summary>
    protected internal void AddBytesDone(long bytes)
    {
        lock (_lock) _bytesDone += bytes;
        OnProgress();
    }

    /// <summary>Takes a skipped file's bytes out of the total, so speed and remaining time reflect only what is
    /// really copied.</summary>
    protected internal void DropBytesFromTotal(long bytes)
    {
        lock (_lock) _bytesTotal -= bytes;
        OnProgress();
    }

    protected internal void CompleteFile()
    {
        lock (_lock) _filesDone++;
        OnProgress();
    }

    protected internal void AddSkipped(SkippedItem item)
    {
        lock (_lock) _skipped.Add(item);
    }

    /// <summary>A pause checkpoint: when a pause was requested, reports <see cref="OperationState.Paused"/> and
    /// waits for <see cref="Resume"/>, then returns to the state it paused in.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is cancelled, before or during the pause.</exception>
    protected internal async ValueTask WaitWhilePausedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task resumed;
        OperationState pausedIn;
        lock (_lock)
        {
            if (_resume is null) return;
            resumed = _resume.Task;
            pausedIn = _state;
            _state = OperationState.Paused;
            _pausedSince = _clock.Elapsed;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await resumed.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            // A pause while scanning ends before the speed clock starts, so it is not subtracted from it.
            if (_runningSince is not null) _pausedFor += _clock.Elapsed - _pausedSince!.Value;
            _pausedSince = null;
        }
        SetState(pausedIn);
    }

    private static bool IsFinal(OperationState state) =>
        state is OperationState.Completed or OperationState.Cancelled or OperationState.Failed;

    private async Task RunCoreAsync()
    {
        lock (_lock) _clock.Start();
        var final = OperationState.Completed;
        Exception? error = null;
        try
        {
            _cancel.Token.ThrowIfCancellationRequested();
            await ExecuteAsync(_cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            final = OperationState.Cancelled;
        }
        catch (Exception e)
        {
            final = OperationState.Failed;
            error = e;
        }

        try
        {
            SetState(final);
        }
        finally
        {
            // The outcome is delivered even if a Changed handler throws.
            if (error is null)
            {
                IReadOnlyList<SkippedItem> skipped;
                lock (_lock) skipped = _skipped.ToArray();
                _completion.TrySetResult(new OperationResult(final, skipped));
            }
            else
            {
                _completion.TrySetException(error);
            }
        }
    }

    private void OnProgress()
    {
        lock (_lock)
        {
            var now = _clock.Elapsed;
            if (_lastProgressEvent is { } last && now - last < ProgressEventInterval) return;
            _lastProgressEvent = now;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Speed is the average since the operation started running, not counting time spent paused.
    /// Caller holds _lock.</summary>
    private OperationProgress SnapshotProgress()
    {
        var now = _pausedSince ?? _clock.Elapsed;
        var elapsed = _runningSince is { } since ? (now - since - _pausedFor).TotalSeconds : 0;
        var speed = elapsed > 0 ? _bytesDone / elapsed : 0;
        TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, _bytesTotal - _bytesDone) / speed) : null;
        return new OperationProgress(_currentItem, _filesDone, _filesTotal, _bytesDone, _bytesTotal, speed, remaining);
    }
}
