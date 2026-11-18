namespace Tandem.Core.Vfs.Local;

/// <summary>The broker for a non-sandboxed build: every path is accessible.</summary>
public sealed class UnrestrictedAccessBroker : IFileAccessBroker
{
    public IDisposable Access(string path) => NoScope.Instance;

    public Task<bool> RequestAccessAsync(string path, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled<bool>(ct) : Task.FromResult(true);

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose() { }
    }
}
