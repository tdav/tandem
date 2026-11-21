using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Renci.SshNet.Common;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;
using Tandem.Net.Sftp;

namespace Tandem.Net.Connections;

/// <summary>Manages saved connections, their credential sources and the lifetime of their open providers.</summary>
public sealed class NetConnections : IAsyncDisposable
{
    public const string ProviderIdPrefix = "net:";

    private readonly ProviderRegistry _providers;
    private readonly ConnectionStore _store;
    private readonly ISecretStore _secrets;
    private readonly INetPrompts _prompts;
    private readonly IReadOnlyDictionary<NetProtocol, IRemoteSessionFactory> _factories;
    private readonly Func<string, CancellationToken, Task<byte[]>> _readKeyFile;
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly ConcurrentDictionary<Guid, Credentials> _credentials = new();
    private readonly ConcurrentDictionary<string, RemoteProvider> _open = new(StringComparer.Ordinal);
    private static long s_nextProvider;
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private bool _disposed;

    public NetConnections(ProviderRegistry providers, ConnectionStore store, TrustStore trust, ISecretStore secrets,
        INetPrompts prompts, IReadOnlyDictionary<NetProtocol, IRemoteSessionFactory> factories,
        Func<string, CancellationToken, Task<byte[]>> readKeyFile)
    {
        _providers = providers;
        _store = store;
        _secrets = secrets;
        _prompts = prompts;
        _factories = factories;
        _readKeyFile = readKeyFile;
        Trust = new SharedTrust(trust, prompts);
    }

    public IReadOnlyList<ConnectionProfile> Profiles => Volatile.Read(ref _profiles);
    public bool HasOpenConnections => _open.Values.Any(provider => provider.HasOpenConnections);
    public ITrustDecisions Trust { get; }

    public event EventHandler? Changed;
    public event EventHandler<Exception>? SecretSaveFailed;

    public void Load()
    {
        _lifecycle.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _profiles, Sorted(_store.Load()));
        }
        finally
        {
            _lifecycle.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string SecretKey(Guid profileId, SecretKind kind) =>
        $"connection:{profileId:N}:{(kind == SecretKind.Password ? "password" : "passphrase")}";

    public Task SaveAsync(ConnectionProfile profile, string? password, CancellationToken ct) =>
        SaveAsync(profile, password, passphrase: null, ct);

    public async Task SaveAsync(ConnectionProfile profile, string? password, string? passphrase, CancellationToken ct)
    {
        profile = profile.Normalized();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = Profiles.FirstOrDefault(item => item.Id == profile.Id);
            var updated = Sorted(Profiles.Where(item => item.Id != profile.Id).Append(profile));
            _store.Save(updated);
            var credentialsChanged = password is not null || passphrase is not null ||
                previous is null || !SameSession(previous, profile);
            if (credentialsChanged)
            {
                var credentials = SourceOf(profile.Id);
                await credentials.BeginUpdate().ConfigureAwait(false);
                try
                {
                    await SetStoredAsync(profile.Id, SecretKind.Password, password).ConfigureAwait(false);
                    await SetStoredAsync(profile.Id, SecretKind.Passphrase, passphrase).ConfigureAwait(false);
                    Volatile.Write(ref _profiles, updated);
                }
                finally
                {
                    credentials.EndUpdate();
                }
            }
            else
            {
                Volatile.Write(ref _profiles, updated);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var updated = Sorted(Profiles.Where(item => item.Id != profileId));
            _store.Save(updated);
            Volatile.Write(ref _profiles, updated);

            if (_credentials.TryRemove(profileId, out var credentials))
                await credentials.ResetAsync(retire: true).ConfigureAwait(false);
            await SetStoredAsync(profileId, SecretKind.Password, "").ConfigureAwait(false);
            await SetStoredAsync(profileId, SecretKind.Passphrase, "").ConfigureAwait(false);
            var matching = _open.Values.Where(provider => provider.Profile.Id == profileId).ToArray();
            foreach (var provider in matching)
            {
                _open.TryRemove(provider.Id, out _);
                await provider.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<bool> HasSavedPasswordAsync(Guid profileId, CancellationToken ct) =>
        HasSavedSecretAsync(profileId, SecretKind.Password, ct);

    public async Task<bool> HasSavedSecretAsync(Guid profileId, SecretKind kind, CancellationToken ct) =>
        await GetStoredAsync(profileId, kind, ct).ConfigureAwait(false) is not null;

    public async Task<VfsPath> ConnectAsync(Guid profileId, VfsPath origin, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var profile = FindProfile(profileId);
            var credentials = SourceOf(profileId);
            var existing = _open.Values.FirstOrDefault(provider => SameSession(provider.Profile, profile));
            if (existing is not null)
            {
                await existing.ConnectAsync(ct).ConfigureAwait(false);
                await credentials.FlushAsync().WaitAsync(ct).ConfigureAwait(false);
                return new VfsPath(existing.Id, profile.InitialPath);
            }

            if (!_factories.TryGetValue(profile.Protocol, out var factory))
                throw new NotSupportedException($"No connection factory for {profile.Protocol}.");
            var id = ProviderIdPrefix + Interlocked.Increment(ref s_nextProvider).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var created = new RemoteProvider(id, profile, () => factory.Create(profile, credentials, Trust),
                new HostLocation(origin, null));
            try
            {
                await created.ConnectAsync(ct).ConfigureAwait(false);
                await credentials.FlushAsync().WaitAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _providers.Register(created);
                _open[id] = created;
                return new VfsPath(id, profile.InitialPath);
            }
            catch
            {
                await created.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ConnectionProfile? ProfileOf(string providerId)
    {
        if (!_open.TryGetValue(providerId, out var provider)) return null;
        return Profiles.FirstOrDefault(profile => SameSession(provider.Profile, profile)) ?? provider.Profile;
    }

    public ICredentialSource CredentialsOf(Guid profileId)
    {
        _ = FindProfile(profileId);
        return SourceOf(profileId);
    }

    public async Task DisconnectAsync(string providerId)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_open.TryGetValue(providerId, out var provider))
                await provider.CloseConnectionsAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await Task.WhenAll(_credentials.Values.Select(source => source.ResetAsync(retire: true)))
                .ConfigureAwait(false);
            _credentials.Clear();
            var close = _open.Values.Select(provider => provider.DisposeAsync().AsTask()).ToArray();
            _open.Clear();
            await Task.WhenAll(close).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private ConnectionProfile FindProfile(Guid id) =>
        Profiles.FirstOrDefault(profile => profile.Id == id) ??
        throw new KeyNotFoundException($"Unknown connection {id}.");

    private Credentials SourceOf(Guid id) => _credentials.GetOrAdd(id, _ => new Credentials(this));

    private static bool SameSession(ConnectionProfile first, ConnectionProfile second) =>
        first == second with { Name = first.Name, InitialPath = first.InitialPath };

    private static IReadOnlyList<ConnectionProfile> Sorted(IEnumerable<ConnectionProfile> profiles) =>
        Array.AsReadOnly(profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());

    private async Task<string?> GetStoredAsync(Guid id, SecretKind kind, CancellationToken ct)
    {
        try
        {
            return await _secrets.GetAsync(SecretKey(id, kind), ct).ConfigureAwait(false);
        }
        catch (SecretStoreException)
        {
            return null;
        }
    }

    private async Task SetStoredAsync(Guid id, SecretKind kind, string? value, bool allowEmpty = false)
    {
        if (value is null) return;
        try
        {
            var key = SecretKey(id, kind);
            if (value.Length == 0 && !allowEmpty)
                await _secrets.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            else
                await _secrets.SetAsync(key, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SecretStoreException)
        {
            // Even a broken keychain must not prevent use of credentials accepted by the server.
            SecretSaveFailed?.Invoke(this, new SecretStoreException("The credential could not be stored in the secret store."));
        }
    }

    private sealed class Credentials(NetConnections owner) : ICredentialSource
    {
        private readonly Lock _gate = new();
        private readonly ConditionalWeakTable<NetCredentials, Attempt> _attempts = new();
        private NetCredentials? _accepted;
        private ConnectionProfile? _acceptedProfile;
        private Task _writes = Task.CompletedTask;
        private TaskCompletionSource? _update;
        private int _version;
        private bool _retired;

        public async Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct)
        {
            int version;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task? updating;
                lock (_gate)
                {
                    updating = _update?.Task;
                    if (updating is null)
                    {
                        if (_retired || !IsCurrent(profile)) return null;
                        if (!previousWasWrong && _acceptedProfile is { } prior && SameSession(prior, profile) &&
                            _accepted is { } accepted)
                            return accepted;
                        version = _version;
                        break;
                    }
                }
                await updating.WaitAsync(ct).ConfigureAwait(false);
            }

            if (profile.Auth == NetAuth.Anonymous) return new NetCredentials(null, null, null);
            var kind = profile.Auth == NetAuth.PrivateKey ? SecretKind.Passphrase : SecretKind.Password;
            var key = kind == SecretKind.Passphrase ? await ReadKeyAsync(profile, ct).ConfigureAwait(false) : null;
            var needsSecret = key is null || NeedsPassphrase(profile, key);
            var secret = needsSecret && !previousWasWrong
                ? await owner.GetStoredAsync(profile.Id, kind, ct).ConfigureAwait(false)
                : null;
            var remember = false;
            if (secret is null && needsSecret)
            {
                var answer = await owner._prompts.AskSecretAsync(profile, kind, previousWasWrong, ct).ConfigureAwait(false);
                if (answer is null) return null;
                secret = answer.Secret;
                remember = answer.Remember;
            }

            lock (_gate)
            {
                if (_retired || version != _version || !IsCurrent(profile)) return null;
                var credentials = kind == SecretKind.Password ? new NetCredentials(secret, null, null) :
                    new NetCredentials(null, key, secret);
                _attempts.Add(credentials, new Attempt(version, remember, kind));
                return credentials;
            }
        }

        public void Accepted(ConnectionProfile profile, NetCredentials credentials)
        {
            lock (_gate)
            {
                if (_retired || !IsCurrent(profile) || !_attempts.TryGetValue(credentials, out var attempt) ||
                    attempt.Version != _version)
                    return;
                _accepted = credentials;
                _acceptedProfile = profile;
                if (attempt.Remember)
                {
                    var secret = attempt.Kind == SecretKind.Password ? credentials.Password : credentials.Passphrase;
                    var previous = _writes;
                    _writes = Task.Run(async () =>
                    {
                        await previous.ConfigureAwait(false);
                        await owner.SetStoredAsync(profile.Id, attempt.Kind, secret, allowEmpty: true)
                            .ConfigureAwait(false);
                    });
                }
            }
        }

        public Task FlushAsync()
        {
            lock (_gate) return _writes;
        }

        public Task BeginUpdate()
        {
            lock (_gate)
            {
                _update = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return ResetAsync();
            }
        }

        public void EndUpdate()
        {
            lock (_gate)
            {
                _update?.TrySetResult();
                _update = null;
            }
        }

        public Task ResetAsync(bool retire = false)
        {
            lock (_gate)
            {
                ++_version;
                _retired |= retire;
                _accepted = null;
                _acceptedProfile = null;
                return _writes;
            }
        }

        private async Task<byte[]> ReadKeyAsync(ConnectionProfile profile, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                throw new NetAuthenticationException("No private key file was selected.");
            try
            {
                return await owner._readKeyFile(profile.PrivateKeyPath, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new NetAuthenticationException($"Cannot read the private key file '{profile.PrivateKeyPath}'.");
            }
        }

        private static bool NeedsPassphrase(ConnectionProfile profile, byte[] key)
        {
            try
            {
                return SftpAuth.NeedsPassphrase(key);
            }
            catch (Exception error) when (error is SshException or IOException or ArgumentException or FormatException)
            {
                throw new NetAuthenticationException($"Cannot read the private key file '{profile.PrivateKeyPath}'.");
            }
        }

        private bool IsCurrent(ConnectionProfile profile) => owner.Profiles.Any(item => SameSession(item, profile));

        private sealed record Attempt(int Version, bool Remember, SecretKind Kind);
    }

    private sealed class SharedTrust(TrustStore store, INetPrompts prompts) : ITrustDecisions
    {
        private readonly SemaphoreSlim _gate = new(1);

        public async Task<bool> TrustHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var host = HostOf(profile);
                var previous = store.HostKeyOf(host, key.Algorithm);
                if (previous == key) return true;
                if (!await prompts.ConfirmHostKeyAsync(profile, key, previous, ct).ConfigureAwait(false)) return false;
                ct.ThrowIfCancellationRequested();
                store.TrustHostKey(host, key);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> TrustCertificateAsync(ConnectionProfile profile, CertificateInfo certificate,
            CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var host = HostOf(profile);
                if (store.CertificateOf(host) == certificate.Sha256) return true;
                if (!await prompts.ConfirmCertificateAsync(profile, certificate, ct).ConfigureAwait(false)) return false;
                ct.ThrowIfCancellationRequested();
                store.TrustCertificate(host, certificate.Sha256);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string HostOf(ConnectionProfile profile) => $"{profile.Host}:{profile.Port}";
    }
}
