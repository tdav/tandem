namespace Tandem.Net.Ssh;

public interface ITerminalTransport : IAsyncDisposable
{
    Task ConnectAsync(int columns, int rows, CancellationToken ct);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    Task ResizeAsync(int columns, int rows, CancellationToken ct);
}

public interface ITerminalTransportFactory
{
    ITerminalTransport Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust);
}
