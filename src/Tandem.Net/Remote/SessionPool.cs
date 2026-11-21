using Tandem.Core.Vfs;

namespace Tandem.Net.Remote;

/// <summary>
/// The connections of one <see cref="RemoteProvider"/>: up to a maximum, each lent to one caller at a time; or, when
/// the server multiplexes (<see cref="RemoteFeatures.Multiplexed"/>), a single connection lent to everyone at once.
/// <para>A caller takes a free connection or opens a new one; at the maximum it waits (cancellably). New connections
/// are opened one at a time, so that the user is asked about credentials once, not by every caller together, and
/// so that the callers waiting meanwhile learn whether the server multiplexes before they open their own.</para>
/// <para>A connection given back is kept for the next caller unless it is no longer connected, failed in a way that
/// leaves its state unknown, or was retired by <see cref="CloseAllAsync"/>; then it is closed. Thread-safe.</para>
/// </summary>
internal sealed class SessionPool(Func<IRemoteSession> newSession, int maxSessions) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly Stack<Slot> _idle = new();                  // connected, lent to no one; not multiplexed
    private readonly List<TaskCompletionSource> _waiters = [];  // woken on every change, then they look again
    private Slot? _shared;                                       // the multiplexed connection in use
    private int _count;       // connections open or being opened, retired ones not yet closed included
    private int _open;        // connections that connected and are not closed yet
    private bool _connecting;
    private int _generation;
    private bool _disposed;
    private RemoteFeatures? _features;

    /// <summary>The features the last connection declared; null before any connected.</summary>
    public RemoteFeatures? Features
    {
        get { lock (_lock) return _features; }
    }

    public bool HasOpenConnections
    {
        get { lock (_lock) return _open > 0; }
    }

    /// <summary>A connection for one call or one stream; give it back with <see cref="Lease.Release"/>.</summary>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    public async Task<Lease> AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource wake;
            List<Slot> dead = [];
            var connect = false;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_shared is { } shared)
                {
                    if (shared.Session.IsConnected)
                    {
                        shared.Users++;
                        return new Lease(this, shared, reused: true);
                    }
                    RetireSharedLocked(dead);
                }
                while (_idle.TryPop(out var slot))
                {
                    if (slot.Session.IsConnected) return new Lease(this, slot, reused: true);
                    ForgetLocked(slot, dead);
                }
                if (!_connecting && _count < maxSessions)
                {
                    _connecting = true;
                    _count++;
                    connect = true;
                    wake = null!;
                }
                else
                {
                    wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(wake);
                }
            }
            await CloseQuietlyAsync(dead).ConfigureAwait(false);
            if (connect) return await ConnectAsync(ct).ConfigureAwait(false);
            try
            {
                await wake.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_lock) _waiters.Remove(wake);
                throw;
            }
        }
    }

    /// <summary>Closes the free connections now; those in use close when they are given back. The next caller opens
    /// a new one.</summary>
    public Task CloseAllAsync()
    {
        List<Slot> dead = [];
        lock (_lock)
        {
            _generation++;
            while (_idle.TryPop(out var slot)) ForgetLocked(slot, dead);
            if (_shared is not null) RetireSharedLocked(dead);
            WakeLocked();
        }
        return CloseQuietlyAsync(dead);
    }

    /// <summary>Closes the free connections: one of the same age turned out to be closed by the server, so these are
    /// likely closed too.</summary>
    public Task CloseIdleAsync()
    {
        List<Slot> dead = [];
        lock (_lock)
        {
            while (_idle.TryPop(out var slot)) ForgetLocked(slot, dead);
            WakeLocked();
        }
        return CloseQuietlyAsync(dead);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock) _disposed = true;
        await CloseAllAsync().ConfigureAwait(false);
    }

    private async Task<Lease> ConnectAsync(CancellationToken ct)
    {
        IRemoteSession? session = null;
        try
        {
            session = newSession();
            await session.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                _count--;
                _connecting = false;
                WakeLocked();
            }
            if (session is not null) await CloseQuietlyAsync(session).ConfigureAwait(false);
            throw;
        }

        var slot = new Slot(session);
        lock (_lock)
        {
            _connecting = false;
            _open++;
            _features = session.Features;
            slot.Generation = _generation;
            WakeLocked();
            if (!_disposed)
            {
                if (session.Features.HasFlag(RemoteFeatures.Multiplexed))
                {
                    slot.Shared = true;
                    slot.Users = 1;
                    _shared = slot;
                }
                return new Lease(this, slot, reused: false);
            }
            ForgetLocked(slot, []);
        }
        await CloseQuietlyAsync(session).ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(SessionPool));
    }

    private void Release(Slot slot, bool broken)
    {
        List<Slot> dead = [];
        lock (_lock)
        {
            if (slot.Shared)
            {
                slot.Users--;
                if ((broken || !slot.Session.IsConnected) && !slot.Retired) RetireSharedLocked(dead, slot);
                else if (slot.Retired && slot.Users == 0) ForgetLocked(slot, dead);
            }
            else if (broken || !slot.Session.IsConnected || slot.Generation != _generation || _disposed)
            {
                ForgetLocked(slot, dead);
            }
            else
            {
                _idle.Push(slot);
            }
            WakeLocked();
        }
        if (dead.Count > 0) _ = CloseQuietlyAsync(dead);
    }

    /// <summary>Stops lending the shared connection (by default the current one); it is closed once no one uses it.
    /// Caller holds the lock.</summary>
    private void RetireSharedLocked(List<Slot> dead, Slot? slot = null)
    {
        slot ??= _shared!;
        if (ReferenceEquals(_shared, slot)) _shared = null;
        slot.Retired = true;
        if (slot.Users == 0) ForgetLocked(slot, dead);
    }

    /// <summary>Takes the connection out of the count and queues it for closing. Caller holds the lock.</summary>
    private void ForgetLocked(Slot slot, List<Slot> dead)
    {
        if (slot.Forgotten) return;
        slot.Forgotten = true;
        _count--;
        _open--;
        dead.Add(slot);
    }

    private void WakeLocked()
    {
        foreach (var waiter in _waiters) waiter.TrySetResult();
        _waiters.Clear();
    }

    private static async Task CloseQuietlyAsync(List<Slot> slots)
    {
        foreach (var slot in slots) await CloseQuietlyAsync(slot.Session).ConfigureAwait(false);
    }

    private static async Task CloseQuietlyAsync(IRemoteSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing a connection that is going away anyway: nothing to tell anyone.
        }
    }

    internal sealed class Slot(IRemoteSession session)
    {
        public IRemoteSession Session { get; } = session;
        public int Generation { get; set; }
        public bool Shared { get; set; }
        public int Users { get; set; }
        public bool Retired { get; set; }
        public bool Forgotten { get; set; }
    }

    /// <summary>A connection lent to one caller (or, multiplexed, shared). <see cref="Reused"/>: it was connected
    /// before this caller took it, so it may have been closed by the server in the meantime.</summary>
    internal sealed class Lease
    {
        private readonly SessionPool _pool;
        private readonly Slot _slot;
        private int _released;

        public Lease(SessionPool pool, Slot slot, bool reused)
        {
            _pool = pool;
            _slot = slot;
            Reused = reused;
        }

        public IRemoteSession Session => _slot.Session;

        public bool Reused { get; }

        /// <summary>Gives the connection back. <paramref name="failure"/>: what the last call threw, if anything; a
        /// lost connection, a cancelled or unexpected failure (the connection's state is then unknown) closes it,
        /// while a plain answer of the server (not found, access denied, …) keeps it. A multiplexed connection is
        /// closed only when lost. Only the first call counts.</summary>
        public void Release(Exception? failure = null)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _pool.Release(_slot, failure is not null && Breaks(failure, _slot.Shared));
        }

        private static bool Breaks(Exception failure, bool shared) => failure switch
        {
            VfsConnectionLostException => true,
            _ when shared => false,
            NetAuthenticationException or NetTrustException => true,
            VfsException => false,
            _ => true,
        };
    }
}
