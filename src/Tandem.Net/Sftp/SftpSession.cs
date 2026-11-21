using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;

namespace Tandem.Net.Sftp;

internal sealed class SftpSession(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust,
    TimeSpan connectTimeout, TimeSpan operationTimeout) : IRemoteSession
{
    private SftpClient? _client;
    private volatile bool _connectionLost;

    public RemoteFeatures Features => RemoteFeatures.Multiplexed | RemoteFeatures.Modes | RemoteFeatures.SetModified;
    public bool IsConnected => !_connectionLost && _client is { IsConnected: true };

    public Task ConnectAsync(CancellationToken ct) => CredentialLoop.RunAsync(profile, credentials, async secret =>
    {
        var info = SftpAuth.Build(profile, secret, connectTimeout);
        var client = new SftpClient(info) { OperationTimeout = operationTimeout, KeepAliveInterval = TimeSpan.FromSeconds(30) };
        try
        {
            await SftpConnect.WithTrustAsync(client, profile, trust, ct).ConfigureAwait(false);
            _client = client;
        }
        catch
        {
            client.Dispose();
            SftpAuth.DisposeMethods(info);
            throw;
        }
    }, ct);

    public async IAsyncEnumerable<RemoteEntry> ListAsync(string directory, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var entries = Client.ListDirectoryAsync(directory, ct).GetAsyncEnumerator(ct);
        while (await RunAsync(directory, () => entries.MoveNextAsync().AsTask()).ConfigureAwait(false))
            if (SftpEntries.ToEntry(entries.Current) is { } entry)
                yield return entry;
    }

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => RunAsync<RemoteEntry?>(path, async () =>
    {
        if (path == "/")
            return SftpEntries.ToEntry("", await Client.GetAttributesAsync(path, ct).ConfigureAwait(false));
        return await FindListedAsync(path, ct).ConfigureAwait(false) is { } file ? SftpEntries.ToEntry(file) : null;
    });

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => OpenAsync(path, FileMode.Open, FileAccess.Read, ct);
    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct) => OpenAsync(path, FileMode.Create, FileAccess.Write, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        RunAsync(path, () => Client.CreateDirectoryAsync(path, ct));

    public Task DeleteFileAsync(string path, CancellationToken ct) => RunAsync(path, async () =>
    {
        var file = await FindListedAsync(path, ct).ConfigureAwait(false) ?? throw new VfsNotFoundException(path);
        await file.DeleteAsync(ct).ConfigureAwait(false);
    });

    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => RunAsync(path, async () =>
    {
        await RejectLinkAsync(path, ct).ConfigureAwait(false);
        await Client.DeleteDirectoryAsync(path, ct).ConfigureAwait(false);
    });

    public Task RenameAsync(string from, string to, CancellationToken ct) => RunAsync(from, async () =>
    {
        await RejectLinkAsync(from, ct).ConfigureAwait(false);
        await Client.RenameFileAsync(from, to, ct).ConfigureAwait(false);
    });

    public Task CreateSymlinkAsync(string path, string target, CancellationToken ct) =>
        Task.FromException(new NotSupportedException());

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken ct) =>
        SetAttributesAsync(path, a => a.LastWriteTimeUtc = modified.UtcDateTime, ct);

    public Task SetModeAsync(string path, UnixFileMode mode, CancellationToken ct) =>
        SetAttributesAsync(path, a => a.SetPermissions((short)mode), ct);

    public ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return ValueTask.CompletedTask;
        var info = client.ConnectionInfo;
        try { client.Dispose(); }
        finally { SftpAuth.DisposeMethods(info); }
        return ValueTask.CompletedTask;
    }

    private SftpClient Client => IsConnected ? _client! : throw new VfsConnectionLostException(profile.Address);

    private Task<Stream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken ct) =>
        RunAsync<Stream>(path, async () =>
        {
            if (access == FileAccess.Write) await RejectLinkAsync(path, ct).ConfigureAwait(false);
            var stream = await Client.OpenAsync(path, mode, access, ct).ConfigureAwait(false);
            stream.Timeout = operationTimeout;
            return new SessionStream(stream, e => Translate(e, path));
        });

    private Task SetAttributesAsync(string path, Action<SftpFileAttributes> change, CancellationToken ct) =>
        RunAsync(path, async () =>
        {
            await RejectLinkAsync(path, ct).ConfigureAwait(false);
            var attrs = await Client.GetAttributesAsync(path, ct).ConfigureAwait(false);
            change(attrs);
            await Task.Run(() => Client.SetAttributes(path, attrs), ct).ConfigureAwait(false);
        });

    private async Task RejectLinkAsync(string path, CancellationToken ct)
    {
        if (path != "/" && await FindListedAsync(path, ct).ConfigureAwait(false) is { IsSymbolicLink: true })
            throw new VfsAccessDeniedException(path);
    }

    private async Task<ISftpFile?> FindListedAsync(string path, CancellationToken ct)
    {
        var slash = path.LastIndexOf('/');
        var parent = slash == 0 ? "/" : path[..slash];
        var name = path[(slash + 1)..];
        try
        {
            await foreach (var file in Client.ListDirectoryAsync(parent, ct).ConfigureAwait(false))
                if (string.Equals(file.Name, name, StringComparison.Ordinal))
                    return file;
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return null;
        }
        return null;
    }

    private Exception Translate(Exception error, string path)
    {
        var mapped = SftpErrors.Map(error, path);
        if (mapped is VfsConnectionLostException) _connectionLost = true;
        return mapped;
    }

    private Task RunAsync(string path, Func<Task> action) =>
        RunAsync(path, async () => { await action().ConfigureAwait(false); return true; });

    private async Task<T> RunAsync<T>(string path, Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception e) { ExceptionDispatchInfo.Throw(Translate(e, path)); throw; }
    }
}
