namespace Tandem.Core.Operations;

/// <summary>
/// Decides when each file operation starts: operations that touch a common volume
/// (<see cref="FileOperation.GetVolumeKeysAsync"/>) run one after another, others run in parallel. A waiting
/// operation stays <see cref="OperationState.Queued"/> and starts once nothing running shares a volume with it and no
/// operation added before it still waits for one of its volumes, so operations on a volume start in the order they
/// were added. A running operation holds its volumes until it ends, also while paused; one whose volumes are not
/// known yet holds every volume. Safe to use from any thread.
/// </summary>
public sealed class OperationQueue
{
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = []; // guarded by _lock; in order of addition

    /// <summary>The operations not yet ended, running and waiting, in order of addition; a snapshot.</summary>
    public IReadOnlyList<FileOperation> Operations
    {
        get
        {
            lock (_lock) return _entries.Select(e => e.Operation).ToArray();
        }
    }

    /// <summary>Raised, on any thread, when an operation is added to <see cref="Operations"/> or removed from it.
    /// Changes of an operation's own state and progress are reported by <see cref="FileOperation.Changed"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Adds <paramref name="operation"/> and starts it when its volumes are free, or at once when
    /// <paramref name="runImmediately"/> (it then still holds its volumes against operations added later).
    /// <see cref="FileOperation.Cancel"/> on a waiting operation ends it as <see cref="OperationState.Cancelled"/>
    /// without running it; if its volume keys cannot be determined, it ends as <see cref="OperationState.Failed"/>
    /// with that error, without running.</summary>
    /// <returns>The operation's outcome, like <see cref="FileOperation.Completion"/>; it completes after the
    /// operation has left <see cref="Operations"/>.</returns>
    /// <exception cref="InvalidOperationException">The operation is already in the queue or no longer
    /// <see cref="OperationState.Queued"/>.</exception>
    public Task<OperationResult> Enqueue(FileOperation operation, bool runImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var entry = new Entry(operation) { Phase = runImmediately ? Phase.Running : Phase.Waiting };
        lock (_lock)
        {
            if (operation.State != OperationState.Queued || _entries.Exists(e => e.Operation == operation))
                throw new InvalidOperationException("The operation is already queued or started.");
            _entries.Add(entry);
        }

        // Already cancelled, the callback runs here and ends the operation at once.
        entry.CancelRegistration = operation.CancellationRequested.Register(() => OnCancelled(entry));
        if (runImmediately) operation.RunAsync();
        var finished = TrackAsync(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        _ = ResolveVolumesAsync(entry);
        return finished;
    }

    private async Task<OperationResult> TrackAsync(Entry entry)
    {
        try
        {
            return await entry.Operation.Completion.ConfigureAwait(false);
        }
        finally
        {
            List<Entry> toStart;
            lock (_lock)
            {
                _entries.Remove(entry);
                toStart = TakeStartable();
            }
            entry.CancelRegistration.Dispose();
            Start(toStart);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ResolveVolumesAsync(Entry entry)
    {
        IReadOnlyCollection<string> keys;
        Exception? error = null;
        try
        {
            keys = await entry.Operation.GetVolumeKeysAsync(entry.Operation.CancellationRequested)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            keys = [];
            error = e;
        }

        bool fail;
        List<Entry> toStart;
        lock (_lock)
        {
            // On an error: cancelled while waiting, OnCancelled has already ended it; already running, it meets
            // the same problem itself. Either way it holds no volumes.
            fail = error is not null && entry.Phase == Phase.Waiting;
            if (fail) entry.Phase = Phase.Ending;
            entry.Volumes = keys;
            toStart = TakeStartable();
        }
        if (fail) entry.Operation.FailWithoutRunning(error!);
        Start(toStart);
    }

    private void OnCancelled(Entry entry)
    {
        lock (_lock)
        {
            if (entry.Phase != Phase.Waiting) return;
            entry.Phase = Phase.Ending;
        }
        // Being cancelled, the run ends at once as Cancelled without doing anything.
        entry.Operation.RunAsync();
    }

    /// <summary>Marks the waiting operations that may start now as running and returns them. Caller holds _lock.</summary>
    private List<Entry> TakeStartable()
    {
        var toStart = new List<Entry>();
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _entries.Where(e => e.Phase == Phase.Running))
        {
            if (entry.Volumes is null) return toStart; // unknown volumes hold all of them
            held.UnionWith(entry.Volumes);
        }

        foreach (var entry in _entries.Where(e => e.Phase == Phase.Waiting))
        {
            // Not knowing what it touches, nothing added after it may overtake it.
            if (entry.Volumes is null) break;
            if (!held.Overlaps(entry.Volumes))
            {
                entry.Phase = Phase.Running;
                toStart.Add(entry);
            }
            // Started or not, its volumes are taken for the operations added after it.
            held.UnionWith(entry.Volumes);
        }
        return toStart;
    }

    private static void Start(List<Entry> entries)
    {
        foreach (var entry in entries) entry.Operation.RunAsync();
    }

    private enum Phase
    {
        Waiting,
        Running,

        /// <summary>Ended without running (cancelled or failed while waiting); holds no volumes.</summary>
        Ending,
    }

    private sealed class Entry(FileOperation operation)
    {
        public FileOperation Operation { get; } = operation;

        // Phase and Volumes are guarded by OperationQueue._lock.
        public Phase Phase { get; set; }

        /// <summary>Null until known.</summary>
        public IReadOnlyCollection<string>? Volumes { get; set; }

        public CancellationTokenRegistration CancelRegistration { get; set; }
    }
}
