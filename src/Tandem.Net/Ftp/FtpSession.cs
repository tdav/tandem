using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using FluentFTP;
using FluentFTP.Exceptions;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;

namespace Tandem.Net.Ftp;

internal sealed class FtpSession(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust,
    TimeSpan connectTimeout, TimeSpan readTimeout) : IRemoteSession
{
    private ListingFtpClient? _client;
    private volatile bool _broken;
    private int _dataCertificateRefused;
    private string? _password;

    public RemoteFeatures Features => _client?.HasFeature(FtpCapability.MFMT) == true
        ? RemoteFeatures.SetModified : RemoteFeatures.None;
    public bool IsConnected => !_broken && _client is { IsConnected: true };

    public Task ConnectAsync(CancellationToken ct) => profile.Auth == NetAuth.Anonymous
        ? ConnectOnceAsync(new("", null, null), ct)
        : CredentialLoop.RunAsync(profile, credentials, secret => ConnectOnceAsync(secret, ct), ct);

    private async Task ConnectOnceAsync(NetCredentials secret, CancellationToken ct)
    {
        var password = secret.Password ?? "";
        var user = profile.Auth == NetAuth.Anonymous ? ConnectionProfile.AnonymousUser : profile.User;
        ValidateCommandArgument(user);
        ValidateCommandArgument(password);
        var client = new ListingFtpClient(profile.Host, user, password, profile.Port,
            FtpSettings.ToConfig(profile, connectTimeout, readTimeout)) { Encoding = FtpSettings.EncodingOf(profile) };
        var refused = false;
        ExceptionDispatchInfo? promptError = null;
        client.ValidateCertificate += (_, e) =>
        {
            if (e.PolicyErrors == SslPolicyErrors.None) { e.Accept = true; return; }
            e.Accept = false;
            if (e.IsDataConnection)
            {
                // FluentFTP accepts a data certificate that matches the already-verified control certificate.
                // A different certificate failing system validation must not replace that trust decision.
                Interlocked.Exchange(ref _dataCertificateRefused, 1);
                return;
            }
            try
            {
                if (e.Certificate is not null)
                    e.Accept = trust.TrustCertificateAsync(profile, FtpSettings.Describe(e.Certificate, e.PolicyErrors), ct)
                        .GetAwaiter().GetResult();
                refused = !e.Accept;
            }
            catch (Exception error) { promptError = ExceptionDispatchInfo.Capture(error); }
        };
        try
        {
            await Task.Run(() => client.Connect(ct), ct).ConfigureAwait(false);
            _password = password;
            _client = client;
        }
        catch (Exception error)
        {
            client.Dispose();
            ct.ThrowIfCancellationRequested();
            promptError?.Throw();
            if (refused) throw new NetTrustException($"TLS certificate refused for {profile.Address}.");
            if (error is FtpAuthenticationException)
                throw new NetAuthenticationException($"FTP authentication failed for {profile.Address}.");
            throw Translate(error, profile.Address, password);
        }
    }

    public async IAsyncEnumerable<RemoteEntry> ListAsync(string directory, [EnumeratorCancellation] CancellationToken ct)
    {
        var entries = await ListDirectoryAsync(directory, ct).ConfigureAwait(false);
        foreach (var item in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Name is not ("." or ".."))
                yield return ToEntry(item);
        }
    }

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => RunAsync<RemoteEntry?>(path, async () =>
    {
        ValidateCommandArgument(path);
        if (path == "/")
        {
            await CommandAsync("CWD /", path, ct).ConfigureAwait(false);
            return new("", EntryKind.Directory, 0, DateTimeOffset.UnixEpoch, null);
        }

        var slash = path.LastIndexOf('/');
        var folder = slash == 0 ? "/" : path[..slash];
        var name = path[(slash + 1)..];
        if (Client.HasFeature(FtpCapability.MLST))
        {
            var reply = await Client.Execute("MLST " + path, ct).ConfigureAwait(false);
            if (!IsMissing(reply.Code, reply.Message))
            {
                CheckReply(reply, path);
                var info = await Client.GetObjectInfo(path, dateModified: false, ct).ConfigureAwait(false);
                if (info is null) throw new VfsException($"{path}: unreadable MLST response.");
                return ToEntry(info) with { Name = name };
            }
            // Even a "not found" reply can hide an access problem. Confirm it with the parent listing.
        }
        try
        {
            var items = await ListDirectoryAsync(folder, ct).ConfigureAwait(false);
            return items.FirstOrDefault(item => item.Name == name) is { } match ? ToEntry(match) : null;
        }
        catch (VfsNotFoundException) { return null; }
    });

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => TransferAsync(path, writing: false, ct);
    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct) => TransferAsync(path, writing: true, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => SendPathAsync("MKD", path, ct);
    public Task DeleteFileAsync(string path, CancellationToken ct) => SendPathAsync("DELE", path, ct);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => SendPathAsync("RMD", path, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => RunAsync(from, async () =>
    {
        ValidateCommandArgument(from);
        ValidateCommandArgument(to);
        await Client.Rename(from, to, ct).ConfigureAwait(false);
    });

    public Task CreateSymlinkAsync(string path, string target, CancellationToken ct) =>
        Task.FromException(new NotSupportedException());

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken ct) =>
        Features.HasFlag(RemoteFeatures.SetModified)
            ? RunAsync(path, async () =>
            {
                ValidateCommandArgument(path);
                var stamp = modified.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                await CommandAsync($"MFMT {stamp} {path}", path, ct).ConfigureAwait(false);
            })
            : Task.CompletedTask;

    public Task SetModeAsync(string path, UnixFileMode mode, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _client, null)?.Dispose();
        _password = null;
        return ValueTask.CompletedTask;
    }

    private ListingFtpClient Client => IsConnected ? _client! : throw new VfsConnectionLostException(profile.Address);

    private Task<FtpListItem[]> ListDirectoryAsync(string directory, CancellationToken ct) =>
        RunAsync(directory, async () =>
        {
            ValidateCommandArgument(directory);
            try { return await Client.ListStrictlyAsync(directory, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { _broken = true; throw; }
        });

    private Task<Stream> TransferAsync(string path, bool writing, CancellationToken ct) =>
        RunAsync<Stream>(path, async () =>
        {
            ValidateCommandArgument(path);
            var client = Client;
            var stream = writing
                ? await client.OpenWrite(path, FtpDataType.Binary, checkIfFileExists: false, ct).ConfigureAwait(false)
                : await client.OpenRead(path, FtpDataType.Binary, 0, checkIfFileExists: false, ct).ConfigureAwait(false);
            // FluentFTP also closes a data stream internally on failed reads. We own the final reply.
            if (stream is FtpDataStream ftp) ftp.CommandStatus = new FtpReply();
            return new SessionStream(stream, error => Translate(error, path), CompleteAsync, readTimeout);

            async ValueTask CompleteAsync(bool closed)
            {
                if (!closed || _broken) { _broken = true; return; }
                using var timeout = new CancellationTokenSource(readTimeout);
                try
                {
                    var reply = await client.GetReply(timeout.Token).ConfigureAwait(false);
                    CheckReply(reply, path, completion: true);
                }
                catch (OperationCanceledException)
                {
                    _broken = true;
                    throw new VfsConnectionLostException(path);
                }
                catch (Exception error)
                {
                    ExceptionDispatchInfo.Throw(Translate(error, path));
                }
            }
        });

    private Task SendPathAsync(string command, string path, CancellationToken ct) => RunAsync(path, async () =>
    {
        ValidateCommandArgument(path);
        await CommandAsync(command + " " + path, path, ct).ConfigureAwait(false);
    });

    private async Task CommandAsync(string command, string path, CancellationToken ct) =>
        CheckReply(await Client.Execute(command, ct).ConfigureAwait(false), path);

    private void CheckReply(FtpReply reply, string path, bool completion = false)
    {
        if (reply.Success && (!completion || reply.Code.StartsWith('2'))) return;
        throw ReplyError(reply.Code, reply.Message, path, _password);
    }

    private Exception Translate(Exception error, string path, string? password = null)
    {
        if (Interlocked.Exchange(ref _dataCertificateRefused, 0) != 0)
        {
            _broken = true;
            if (error is not OperationCanceledException)
                return new NetTrustException($"TLS data certificate refused for {profile.Address}.");
        }
        var mapped = error switch
        {
            FtpCommandException command => ReplyError(command.CompletionCode, command.Message, path, password ?? _password),
            SocketException or TimeoutException or FtpMissingSocketException or ObjectDisposedException =>
                new VfsConnectionLostException(path),
            VfsException or OperationCanceledException => error,
            FtpException when _client is { IsConnected: false } ||
                              error.Message.Contains("SelfConnectMode", StringComparison.Ordinal) =>
                new VfsConnectionLostException(path),
            FtpException => new VfsException($"{path}: {Redact(error.Message, password ?? _password)}"),
            IOException when error.InnerException is SocketException => new VfsConnectionLostException(path),
            _ => error,
        };
        if (mapped is VfsConnectionLostException) _broken = true;
        return mapped;
    }

    private static Exception ReplyError(string? code, string? message, string path, string? password)
    {
        var detail = $"{path}: {Redact(message ?? "", password)}";
        if (IsMissing(code, message)) return new VfsNotFoundException(detail);
        return code switch
        {
            null or "" or "421" or "425" or "426" => new VfsConnectionLostException(detail),
            "530" or "550" or "553" => new VfsAccessDeniedException(detail),
            "452" or "552" => new IOException(detail),
            _ => new VfsException(detail),
        };
    }

    private static bool IsMissing(string? code, string? message) => code == "550" &&
        message is not null && (message.Contains("no such", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));

    private static string Redact(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "***", StringComparison.Ordinal);

    private static void ValidateCommandArgument(string value)
    {
        if (value.AsSpan().ContainsAny('\0', '\r', '\n'))
            throw new VfsAccessDeniedException("Invalid FTP command argument.");
    }

    private static RemoteEntry ToEntry(FtpListItem item) => new(item.Name, item.Type switch
    {
        FtpObjectType.Directory => EntryKind.Directory,
        FtpObjectType.Link => EntryKind.Symlink,
        _ => EntryKind.File,
    }, Math.Max(0, item.Size), DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc), null,
        item.LinkTarget, item.LinkObject?.Type == FtpObjectType.Directory);

    private Task RunAsync(string path, Func<Task> action) =>
        RunAsync(path, async () => { await action().ConfigureAwait(false); return true; });

    private async Task<T> RunAsync<T>(string path, Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) { ExceptionDispatchInfo.Throw(Translate(error, path)); throw; }
    }
}
