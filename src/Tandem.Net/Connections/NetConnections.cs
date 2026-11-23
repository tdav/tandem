using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
    private readonly CredentialVault? _vault;
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly SemaphoreSlim _vaultEdits = new(1);
    private readonly ConcurrentDictionary<Guid, Credentials> _credentials = new();
    private readonly ConcurrentDictionary<string, RemoteProvider> _open = new(StringComparer.Ordinal);
    private static long s_nextProvider;
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private volatile bool _vaultProfilesPublished;
    private bool _disposed;

    public NetConnections(ProviderRegistry providers, ConnectionStore store, TrustStore trust, ISecretStore secrets,
        INetPrompts prompts, IReadOnlyDictionary<NetProtocol, IRemoteSessionFactory> factories,
        Func<string, CancellationToken, Task<byte[]>> readKeyFile, CredentialVault? vault = null)
    {
        _providers = providers;
        _store = store;
        _secrets = secrets;
        _prompts = prompts;
        _factories = factories;
        _readKeyFile = readKeyFile;
        _vault = vault;
        Trust = new SharedTrust(trust, prompts);
    }

    public IReadOnlyList<ConnectionProfile> Profiles =>
        VaultConfigured && !_vaultProfilesPublished ? [] : Volatile.Read(ref _profiles);
    public bool HasOpenConnections => _open.Values.Any(provider => provider.HasOpenConnections);
    public string? VaultPath => _vault?.FilePath;
    public bool VaultConfigured => _vault is { IsConfigured: true };
    public bool VaultUnlocked => _vault is { IsUnlocked: true };
    public ITrustDecisions Trust { get; }

    public event EventHandler? Changed;
    public event EventHandler<Exception>? SecretSaveFailed;

    public void Load()
    {
        _lifecycle.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!VaultConfigured)
                Volatile.Write(ref _profiles, Sorted(_store.Load()));
            else if (VaultUnlocked)
                ReadVaultProfilesAsync(RequireUnlockedVault()).GetAwaiter().GetResult();
        }
        finally
        {
            _lifecycle.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnableVaultAsync(string password, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var vault = RequireVault();
            if (vault.IsConfigured)
                throw new CredentialVaultException("The credential vault already exists.");

            var profiles = Sorted(_store.LoadForMigration());
            var sources = profiles.Concat(Profiles).Select(profile => profile.Id).Distinct().Select(SourceOf).ToArray();
            var entries = new List<VaultEntry>(profiles.Count);
            var committed = false;
            try
            {
                // Let queued Keychain writes finish before taking the migration snapshot.
                await Task.WhenAll(sources.Select(source => source.BeginUpdate())).WaitAsync(ct).ConfigureAwait(false);
                foreach (var profile in profiles)
                    entries.Add(await ReadLegacyEntryAsync(profile, ct).ConfigureAwait(false));
                await vault.CreateAsync(password, entries, ct).ConfigureAwait(false);
                committed = true;
                PublishVaultProfiles(profiles);
            }
            finally
            {
                foreach (var entry in entries)
                    ClearKey(entry.PrivateKey);
                foreach (var source in sources)
                    source.EndUpdate(committed);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UnlockVaultAsync(string password, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var vault = RequireVault();
            await vault.UnlockAsync(password, ct).ConfigureAwait(false);
            await ReadVaultProfilesAsync(vault).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> HasStoredPrivateKeyAsync(Guid id, CancellationToken ct)
    {
        if (!VaultUnlocked) return false;
        var entry = await ReadVaultEntryAsync(id, ct).ConfigureAwait(false);
        ClearKey(entry?.PrivateKey);
        return entry?.PrivateKey is not null;
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
            if (VaultConfigured)
            {
                await SaveVaultProfileAsync(profile, password, passphrase, ct).ConfigureAwait(false);
            }
            else
            {
                await SaveLegacyProfileAsync(profile, password, passphrase).ConfigureAwait(false);
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
            if (VaultConfigured)
            {
                var vault = RequireUnlockedVault();
                var credentials = SourceOf(profileId);
                var removed = false;
                try
                {
                    await credentials.BeginUpdate().WaitAsync(ct).ConfigureAwait(false);
                    await vault.RemoveAsync(profileId, ct).ConfigureAwait(false);
                    removed = true;
                    Volatile.Write(ref _profiles, updated);
                    _credentials.TryRemove(profileId, out _);
                    await credentials.ResetAsync(retire: true).ConfigureAwait(false);
                }
                finally
                {
                    credentials.EndUpdate(removed);
                }
            }
            else
            {
                _store.Save(updated);
                Volatile.Write(ref _profiles, updated);
                if (_credentials.TryRemove(profileId, out var credentials))
                    await credentials.ResetAsync(retire: true).ConfigureAwait(false);
                await SetLegacyStoredAsync(profileId, SecretKind.Password, "").ConfigureAwait(false);
                await SetLegacyStoredAsync(profileId, SecretKind.Passphrase, "").ConfigureAwait(false);
            }
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

    public async Task<bool> HasSavedSecretAsync(Guid profileId, SecretKind kind, CancellationToken ct)
    {
        if (!VaultConfigured)
            return await GetLegacyStoredAsync(profileId, kind, ct).ConfigureAwait(false) is not null;
        if (!VaultUnlocked) return false;
        var entry = await ReadVaultEntryAsync(profileId, ct).ConfigureAwait(false);
        ClearKey(entry?.PrivateKey);
        return SecretOf(entry, kind) is not null;
    }

    public async Task<VfsPath> ConnectAsync(Guid profileId, VfsPath origin, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var profile = FindProfile(profileId);
            if (profile.Protocol == NetProtocol.Ssh)
                throw new NotSupportedException("An SSH-only profile opens in a terminal.");
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
            try
            {
                await Task.WhenAll(_credentials.Values.Select(source => source.ResetAsync(retire: true)))
                    .ConfigureAwait(false);
                _credentials.Clear();
                var close = _open.Values.Select(provider => provider.DisposeAsync().AsTask()).ToArray();
                _open.Clear();
                await Task.WhenAll(close).ConfigureAwait(false);
            }
            finally
            {
                _vault?.Dispose();
            }
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

    private CredentialVault RequireVault() =>
        _vault ?? throw new CredentialVaultException("The credential vault is unavailable.");

    private CredentialVault RequireUnlockedVault() =>
        _vault is { IsUnlocked: true } vault ? vault : throw new CredentialVaultException("The credential vault is locked.");

    private void PublishVaultProfiles(IReadOnlyList<ConnectionProfile> profiles)
    {
        Volatile.Write(ref _profiles, profiles);
        _vaultProfilesPublished = true;
    }

    private async Task ReadVaultProfilesAsync(CredentialVault vault) =>
        PublishVaultProfiles(Sorted(await vault.ReadProfilesAsync(CancellationToken.None).ConfigureAwait(false)));

    private Task<VaultEntry?> ReadVaultEntryAsync(Guid id, CancellationToken ct) =>
        RequireUnlockedVault().FindAsync(id, ct);

    private async Task<VaultEntry> ReadLegacyEntryAsync(ConnectionProfile profile, CancellationToken ct)
    {
        string? password;
        string? passphrase;
        try
        {
            password = await _secrets.GetAsync(SecretKey(profile.Id, SecretKind.Password), ct).ConfigureAwait(false);
            passphrase = await _secrets.GetAsync(SecretKey(profile.Id, SecretKind.Passphrase), ct).ConfigureAwait(false);
        }
        catch (SecretStoreException)
        {
            throw new CredentialVaultException("The legacy credential store could not be read.");
        }
        var key = profile.Auth == NetAuth.PrivateKey
            ? await ReadVaultKeyFileAsync(profile.PrivateKeyPath, ct).ConfigureAwait(false) : null;
        return new VaultEntry(profile, password, key, passphrase);
    }

    private async Task<byte[]> ReadVaultKeyFileAsync(string? path, CancellationToken ct)
    {
        if (path is null)
            throw new CredentialVaultException("No private key file was selected.");
        byte[] key;
        try
        {
            key = await _readKeyFile(path, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new CredentialVaultException("The private key file could not be read.");
        }
        if (key.Length <= CredentialVault.MaxKeyBytes) return key;
        ClearKey(key);
        throw new CredentialVaultException("The private key file exceeds the supported size.");
    }

    private async Task SaveLegacyProfileAsync(ConnectionProfile profile, string? password, string? passphrase)
    {
        var updated = Sorted(Profiles.Where(item => item.Id != profile.Id).Append(profile));
        var previous = Profiles.FirstOrDefault(item => item.Id == profile.Id);
        _store.Save(updated);
        if (!CredentialsChanged(previous, profile, password, passphrase))
        {
            Volatile.Write(ref _profiles, updated);
            return;
        }

        var credentials = SourceOf(profile.Id);
        try
        {
            await credentials.BeginUpdate().ConfigureAwait(false);
            await SetLegacyStoredAsync(profile.Id, SecretKind.Password, password).ConfigureAwait(false);
            await SetLegacyStoredAsync(profile.Id, SecretKind.Passphrase, passphrase).ConfigureAwait(false);
            Volatile.Write(ref _profiles, updated);
        }
        finally
        {
            credentials.EndUpdate();
        }
    }

    private async Task SaveVaultProfileAsync(ConnectionProfile profile, string? password, string? passphrase,
        CancellationToken ct)
    {
        var vault = RequireUnlockedVault();
        var profiles = Profiles;
        var previous = profiles.FirstOrDefault(item => item.Id == profile.Id);
        var credentials = SourceOf(profile.Id);
        var invalidate = CredentialsChanged(previous, profile, password, passphrase);
        var committed = false;
        try
        {
            var pendingWrites = invalidate ? credentials.BeginUpdate() : credentials.FlushAsync();
            await pendingWrites.WaitAsync(ct).ConfigureAwait(false);
            await _vaultEdits.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await UpdateVaultEntryAsync(vault, profile, password, passphrase, ct).ConfigureAwait(false);
                committed = true;
                PublishVaultProfiles(Sorted(profiles.Where(item => item.Id != profile.Id).Append(profile)));
            }
            finally
            {
                _vaultEdits.Release();
            }
        }
        finally
        {
            if (invalidate) credentials.EndUpdate(committed);
        }
    }

    private async Task UpdateVaultEntryAsync(CredentialVault vault, ConnectionProfile profile, string? password,
        string? passphrase, CancellationToken ct)
    {
        var existing = await ReadVaultEntryAsync(profile.Id, ct).ConfigureAwait(false);
        byte[]? key = null;
        try
        {
            if (profile.Auth == NetAuth.PrivateKey)
                key = existing is { PrivateKey: not null } &&
                      existing.Profile.Normalized().PrivateKeyPath == profile.PrivateKeyPath
                    ? existing.PrivateKey : await ReadVaultKeyFileAsync(profile.PrivateKeyPath, ct).ConfigureAwait(false);
            await vault.UpsertAsync(new VaultEntry(profile, EditSecret(existing?.Password, password), key,
                EditSecret(existing?.Passphrase, passphrase)), ct).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(key, existing?.PrivateKey)) ClearKey(key);
            ClearKey(existing?.PrivateKey);
        }
    }

    private async Task RememberAcceptedAsync(ConnectionProfile profile, SecretKind kind, string? value)
    {
        if (value is null) return;
        if (!VaultConfigured)
        {
            await SetLegacyStoredAsync(profile.Id, kind, value, allowEmpty: true).ConfigureAwait(false);
            return;
        }

        try
        {
            await _vaultEdits.WaitAsync().ConfigureAwait(false);
            try
            {
                var vault = RequireUnlockedVault();
                var existing = await ReadVaultEntryAsync(profile.Id, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (existing is null || !SameSession(existing.Profile, profile)) return;
                    var edited = kind == SecretKind.Password ? existing with { Password = value } :
                        existing with { Passphrase = value };
                    await vault.UpsertAsync(edited, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    ClearKey(existing?.PrivateKey);
                }
            }
            finally
            {
                _vaultEdits.Release();
            }
        }
        catch (CredentialVaultException error)
        {
            SecretSaveFailed?.Invoke(this, error);
        }
    }

    private static bool CredentialsChanged(ConnectionProfile? previous, ConnectionProfile profile,
        string? password, string? passphrase) =>
        password is not null || passphrase is not null || previous is null || !SameSession(previous, profile);

    private static string? EditSecret(string? existing, string? update) =>
        update is { Length: 0 } ? null : update ?? existing;

    private static string? SecretOf(VaultEntry? entry, SecretKind kind) =>
        kind == SecretKind.Password ? entry?.Password : entry?.Passphrase;

    private static void ClearKey(byte[]? key)
    {
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }

    private async Task<string?> GetLegacyStoredAsync(Guid id, SecretKind kind, CancellationToken ct)
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

    private async Task SetLegacyStoredAsync(Guid id, SecretKind kind, string? value, bool allowEmpty = false)
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
            var useVault = owner.VaultConfigured;
            if (useVault && !owner.VaultUnlocked) return null;
            var entry = useVault ? await owner.ReadVaultEntryAsync(profile.Id, ct).ConfigureAwait(false) : null;
            byte[]? key = entry?.PrivateKey;
            try
            {
                if (kind == SecretKind.Passphrase && key is null)
                    key = useVault ? throw new NetAuthenticationException("No private key is stored for this connection.") :
                        await ReadKeyAsync(profile, ct).ConfigureAwait(false);
                var needsSecret = kind == SecretKind.Password || NeedsPassphrase(profile, key!, useVault);
                var secret = needsSecret && !previousWasWrong ? SecretOf(entry, kind) : null;
                if (needsSecret && !previousWasWrong && !useVault)
                {
                    Task<string?> lookup;
                    lock (_gate)
                    {
                        // A key file may finish reading after its authentication attempt was invalidated.
                        if (_retired || version != _version || !IsCurrent(profile) || owner.VaultConfigured) return null;
                        lookup = owner.GetLegacyStoredAsync(profile.Id, kind, ct);
                    }
                    secret = await lookup.ConfigureAwait(false);
                }
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
                    if (kind == SecretKind.Passphrase) key = null; // Ownership passed to the credentials.
                    _attempts.Add(credentials, new Attempt(version, remember, kind));
                    return credentials;
                }
            }
            finally
            {
                if (useVault) ClearKey(key);
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
                        await owner.RememberAcceptedAsync(profile, attempt.Kind, secret).ConfigureAwait(false);
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
                ++_version;
                return _writes;
            }
        }

        public void EndUpdate(bool clearCache = true)
        {
            lock (_gate)
            {
                if (clearCache)
                {
                    _accepted = null;
                    _acceptedProfile = null;
                }
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

        private static bool NeedsPassphrase(ConnectionProfile profile, byte[] key, bool fromVault)
        {
            try
            {
                return SftpAuth.NeedsPassphrase(key);
            }
            catch (Exception error) when (error is SshException or IOException or ArgumentException or FormatException)
            {
                throw new NetAuthenticationException(fromVault ? "The stored private key could not be read." :
                    $"Cannot read the private key file '{profile.PrivateKeyPath}'.");
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
