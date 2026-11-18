using System.Diagnostics;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>
/// A long-running file operation (copy, move, delete). It runs once, on the thread pool; state and progress may
/// be read from any thread, and <see cref="Changed"/> is raised on the operation's background thread — on every
/// state change and, for progress, at most <see cref="MaxProgressEventsPerSecond"/> times a second.
/// Outcomes: <see cref="OperationState.Completed"/> and <see cref="OperationState.Cancelled"/> complete
/// <see cref="Completion"/> with a result; <see cref="OperationState.Failed"/> faults it with the error
/// (an <see cref="OperationRejectedException"/> when the operation was refused before it started).
/// </summary>
public enum OperationKind { Copy, Move, Delete }

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
    private TimeSpan? _lastProgressEvent;
    private string? _currentItem;
    private long _filesDone, _filesTotal, _bytesDone, _bytesTotal;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>What the operation does; with <see cref="ItemCount"/> and <see cref="Destination"/> the
    /// interface builds a title.</summary>
    public abstract OperationKind Kind { get; }

    /// <summary>How many items the user selected (top-level sources, not everything inside them).</summary>
    public abstract int ItemCount { get; }

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

    /// <summary>Not implemented yet: pausing arrives with block-level control of the copy loop.</summary>
    public void Pause() { }

    /// <summary>Not implemented yet: see <see cref="Pause"/>.</summary>
    public void Resume() { }

    /// <summary>Asks the operation to stop; it ends as <see cref="OperationState.Cancelled"/>. Calling it before
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

    protected internal void AddBytesDone(long bytes)
    {
        lock (_lock) _bytesDone += bytes;
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

    /// <summary>Speed is the average since the operation started running. Caller holds _lock.</summary>
    private OperationProgress SnapshotProgress()
    {
        var elapsed = _runningSince is { } since ? (_clock.Elapsed - since).TotalSeconds : 0;
        var speed = elapsed > 0 ? _bytesDone / elapsed : 0;
        TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, _bytesTotal - _bytesDone) / speed) : null;
        return new OperationProgress(_currentItem, _filesDone, _filesTotal, _bytesDone, _bytesTotal, speed, remaining);
    }
}
