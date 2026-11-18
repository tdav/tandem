using Tandem.Core.Vfs.Local;

namespace Tandem.Platform.Mac.Tests;

/// <summary>A broker that grants everything and records which paths were opened and how many scopes are still open.</summary>
public sealed class CountingBroker : IFileAccessBroker
{
    private readonly Lock _lock = new();
    private readonly List<string> _accessed = [];
    private int _open;

    public IReadOnlyList<string> Accessed
    {
        get { lock (_lock) return [.. _accessed]; }
    }

    public int OpenScopes => Volatile.Read(ref _open);

    public IDisposable Access(string path)
    {
        lock (_lock) _accessed.Add(path);
        Interlocked.Increment(ref _open);
        return new Scope(this);
    }

    public Task<bool> RequestAccessAsync(string path, CancellationToken ct) => Task.FromResult(true);

    private sealed class Scope(CountingBroker owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._open);
        }
    }
}
