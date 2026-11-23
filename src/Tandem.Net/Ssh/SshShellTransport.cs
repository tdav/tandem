using Renci.SshNet;
using Renci.SshNet.Common;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;
using Tandem.Net.Sftp;

namespace Tandem.Net.Ssh;

public sealed class SshShellTransportFactory(TimeSpan? connectTimeout = null) : ITerminalTransportFactory
{
    public ITerminalTransport Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust)
    {
        if (!SshConsoles.Supports(profile)) throw new NotSupportedException();
        return new SshShellTransport(profile, credentials, trust, connectTimeout ?? TimeSpan.FromSeconds(15));
    }
}

internal sealed class SshShellTransport(ConnectionProfile profile, ICredentialSource credentials,
    ITrustDecisions trust, TimeSpan connectTimeout) : ITerminalTransport
{
    private SshClient? _client;
    private ShellStream? _shell;

    public Task ConnectAsync(int columns, int rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        if (_client is not null) throw new InvalidOperationException("The SSH shell is already connected.");
        return CredentialLoop.RunAsync(profile, credentials, async secret =>
        {
            var info = SftpAuth.Build(profile, secret, connectTimeout);
            var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            try
            {
                await SftpConnect.WithTrustAsync(client, profile, trust, ct).ConfigureAwait(false);
                _shell = await Task.Run(() => client.CreateShellStream("xterm-256color",
                    (uint)columns, (uint)rows, 0, 0, 64 * 1024,
                    new Dictionary<TerminalModes, uint> { [TerminalModes.IUTF8] = 1 }), ct).ConfigureAwait(false);
                _client = client;
            }
            catch (Exception error)
            {
                try { client.Dispose(); }
                finally { SftpAuth.DisposeMethods(info); }
                if (error is NetAuthenticationException or NetTrustException or OperationCanceledException
                    or VfsConnectionLostException) throw;
                throw Disconnected();
            }
        }, ct);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        try
        {
            var shell = Shell;
            // ShellStream implements a blocking Read. Closing the channel releases an idle read.
            var pending = Task.Run(() => shell.Read(buffer.Span));
            try
            {
                var count = await pending.WaitAsync(ct).ConfigureAwait(false);
                if (count == 0 && _client is not { IsConnected: true }) throw Disconnected();
                return count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { await DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try { await pending.ConfigureAwait(false); }
                    catch { /* cancellation ends the channel */ }
                    ct.ThrowIfCancellationRequested();
                }
                throw;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not VfsConnectionLostException)
        {
            ct.ThrowIfCancellationRequested();
            throw Disconnected();
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            var shell = Shell;
            await shell.WriteAsync(data, ct).ConfigureAwait(false);
            await shell.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not VfsConnectionLostException)
        {
            ct.ThrowIfCancellationRequested();
            throw Disconnected();
        }
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        return Task.Run(() =>
        {
            try { Shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0); }
            catch (Exception e) when (e is not VfsConnectionLostException) { throw Disconnected(); }
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        var shell = Interlocked.Exchange(ref _shell, null);
        var client = Interlocked.Exchange(ref _client, null);
        if (shell is null && client is null) return ValueTask.CompletedTask;
        return new ValueTask(Task.Run(() =>
        {
            try { shell?.Dispose(); }
            finally
            {
                if (client is not null)
                {
                    var info = client.ConnectionInfo;
                    try { client.Dispose(); }
                    finally { SftpAuth.DisposeMethods(info); }
                }
            }
        }));
    }

    private ShellStream Shell => _shell ?? throw Disconnected();
    private VfsConnectionLostException Disconnected() => new($"SSH shell disconnected for {profile.Address}.");
}
