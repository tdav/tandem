using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Tests.Platform;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Net.Connections;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Remote;

namespace Tandem.Net.Tests.Connections;

[SupportedOSPlatform("macos")]
public sealed class NetConnectionsVaultTests : IDisposable
{
    private const string MasterPassword = "synthetic master password";
    private readonly TempDir _directory = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly ScriptedNetPrompts _prompts = new();
    private readonly ConnectionProfile _profile =
        new(Guid.NewGuid(), "Test", NetProtocol.Sftp, "example.test", 22, "demo", NetAuth.Password);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_publishes_legacy_profiles_and_available_secrets_without_changing_the_sources()
    {
        var withKey = _profile with { Id = Guid.NewGuid(), Auth = NetAuth.PrivateKey, PrivateKeyPath = "/example/key" };
        var withNoPassword = _profile with { Id = Guid.NewGuid(), Name = "Without a password" };
        var key = await FixtureKeyAsync("ed25519-pass");
        using var vault = new CredentialVault(_directory.Path);
        await using var connections = Open(vault, (_, _) => Task.FromResult(key.ToArray()));
        await connections.SaveAsync(_profile, "legacy password", Ct);
        await connections.SaveAsync(withKey, null, "test passphrase", Ct);
        await connections.SaveAsync(withNoPassword, null, Ct);
        var settingsPath = Path.Combine(_directory.Path, ConnectionStore.FileName);
        var settings = await File.ReadAllTextAsync(settingsPath, Ct);
        var legacyCallCount = _secrets.Calls.Count;
        var changeCount = 0;
        connections.Changed += (_, _) => changeCount++;

        await connections.EnableVaultAsync(MasterPassword, Ct);

        Assert.True(connections.VaultConfigured);
        Assert.True(connections.VaultUnlocked);
        Assert.Equal(1, changeCount);
        Assert.Equal(3, connections.Profiles.Count);
        var entries = await vault.ReadAsync(Ct);
        Assert.Equal("legacy password", Assert.Single(entries, entry => entry.Profile.Id == _profile.Id).Password);
        Assert.Null(Assert.Single(entries, entry => entry.Profile.Id == withNoPassword.Id).Password);
        var migrated = Assert.Single(entries, entry => entry.Profile.Id == withKey.Id);
        Assert.Equal(key, migrated.PrivateKey);
        Assert.Equal("test passphrase", migrated.Passphrase);
        Assert.Equal(settings, await File.ReadAllTextAsync(settingsPath, Ct));
        Assert.Equal(2, _secrets.Secrets.Count);
        Assert.DoesNotContain(_secrets.Calls.Skip(legacyCallCount), call => call.Method is "Set" or "Delete");
    }

    [Theory]
    [InlineData("key")]
    [InlineData("keychain")]
    [InlineData("cancelled")]
    public async Task Failed_or_cancelled_migration_keeps_the_legacy_source(string reason)
    {
        using var vault = new CredentialVault(_directory.Path);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var connections = Open(vault, (_, ct) =>
        {
            if (reason == "cancelled")
            {
                cancel.Cancel();
                return Task.FromCanceled<byte[]>(ct);
            }
            return Task.FromException<byte[]>(new IOException("sensitive read error"));
        });
        var profile = _profile with { Auth = NetAuth.PrivateKey, PrivateKeyPath = "/example/missing-key" };
        await connections.SaveAsync(profile, null, Ct);
        if (reason == "keychain") _secrets.Fail = new SecretStoreException("sensitive keychain error");
        var changeCount = 0;
        connections.Changed += (_, _) => changeCount++;

        if (reason == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connections.EnableVaultAsync(MasterPassword,
                cancel.Token));
        else
        {
            var error = await Assert.ThrowsAsync<CredentialVaultException>(() =>
                connections.EnableVaultAsync(MasterPassword, Ct));
            Assert.DoesNotContain("sensitive", error.ToString());
        }

        Assert.False(connections.VaultConfigured);
        Assert.Equal(0, changeCount);
        Assert.Equal(profile, Assert.Single(connections.Profiles));
        Assert.False(File.Exists(Path.Combine(_directory.Path, CredentialVault.FileName)));
    }

    [Fact]
    public async Task Configured_vault_hides_legacy_profiles_until_unlocked_and_uses_stored_bytes()
    {
        var profile = _profile with { Auth = NetAuth.PrivateKey, PrivateKeyPath = "/example/unavailable-key" };
        new ConnectionStore(_directory.Path).Save([_profile]);
        var key = await FixtureKeyAsync("ed25519");
        using (var writer = new CredentialVault(_directory.Path))
            await writer.CreateAsync(MasterPassword, [new VaultEntry(profile, null, key, null)], Ct);
        using var vault = new CredentialVault(_directory.Path);
        await using var connections = Open(vault, (_, _) => throw new InvalidOperationException("Key file read."));
        connections.Load();

        Assert.True(connections.VaultConfigured);
        Assert.False(connections.VaultUnlocked);
        Assert.Empty(connections.Profiles);
        await Assert.ThrowsAsync<CredentialVaultException>(() => connections.SaveAsync(_profile, "ignored", Ct));
        await Assert.ThrowsAsync<CredentialVaultException>(() => connections.UnlockVaultAsync("wrong password", Ct));
        Assert.Empty(connections.Profiles);

        await connections.UnlockVaultAsync(MasterPassword, Ct);

        Assert.True(connections.VaultUnlocked);
        Assert.Equal(profile, Assert.Single(connections.Profiles));
        Assert.True(await connections.HasStoredPrivateKeyAsync(profile.Id, Ct));
        var offered = await connections.CredentialsOf(profile.Id).GetAsync(profile, false, Ct);
        Assert.Equal(key, Assert.IsType<NetCredentials>(offered).PrivateKey);
        Assert.Empty(_secrets.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_refuses_to_replace_an_invalid_legacy_profile_list_with_an_empty_vault(bool loadedFirst)
    {
        const string invalid = "{ malformed legacy configuration";
        var path = Path.Combine(_directory.Path, ConnectionStore.FileName);
        await File.WriteAllTextAsync(path, invalid, Ct);
        using var vault = new CredentialVault(_directory.Path);
        await using var connections = Open(vault);
        if (loadedFirst) connections.Load();

        await Assert.ThrowsAsync<CredentialVaultException>(() => connections.EnableVaultAsync(MasterPassword, Ct));

        Assert.False(connections.VaultConfigured);
        Assert.Equal(invalid, await File.ReadAllTextAsync(loadedFirst ? path + ".bad" : path, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/example/unavailable-key")]
    [InlineData("  /example/unavailable-key  ")]
    public async Task Vault_edits_keep_the_stored_key_until_user_selects_a_new_path(string? previousPath)
    {
        var profile = _profile with { Auth = NetAuth.PrivateKey, PrivateKeyPath = previousPath };
        var oldKey = await FixtureKeyAsync("ed25519");
        var newKey = await FixtureKeyAsync("ed25519-pass");
        using var vault = new CredentialVault(_directory.Path);
        await vault.CreateAsync(MasterPassword, [new VaultEntry(profile, null, oldKey, null)], Ct);
        await using var connections = Open(vault, (path, _) => path == "/example/new-key"
            ? Task.FromResult(newKey.ToArray())
            : throw new InvalidOperationException("Old key file read."));
        connections.Load();

        await connections.SaveAsync(profile with { Name = "Renamed" }, null, Ct);
        Assert.Equal(oldKey, Assert.Single(await vault.ReadAsync(Ct)).PrivateKey);

        var edited = profile with { Name = "Renamed", PrivateKeyPath = "/example/new-key" };
        await connections.SaveAsync(edited, null, "test passphrase", Ct);
        var entry = Assert.Single(await vault.ReadAsync(Ct));
        Assert.Equal(edited, entry.Profile);
        Assert.Equal(newKey, entry.PrivateKey);
        Assert.Equal("test passphrase", entry.Passphrase);
        Assert.Empty(_secrets.Calls);
        Assert.False(File.Exists(Path.Combine(_directory.Path, ConnectionStore.FileName)));
    }

    [Fact]
    public async Task Accepted_remembered_password_and_deletion_use_the_vault()
    {
        using var vault = new CredentialVault(_directory.Path);
        await using var connections = Open(vault);
        await connections.EnableVaultAsync(MasterPassword, Ct);
        await connections.SaveAsync(_profile, null, Ct);
        _prompts.Secrets.Enqueue(new SecretAnswer("remembered", true));
        await CredentialLoop.RunAsync(_profile, connections.CredentialsOf(_profile.Id), _ => Task.CompletedTask, Ct);

        // A subsequent profile edit must wait for any pending write before reading and updating the vault entry.
        await connections.SaveAsync(_profile with { Name = "Renamed" }, null, Ct);
        Assert.Equal("remembered", Assert.Single(await vault.ReadAsync(Ct)).Password);
        Assert.True(await connections.HasSavedPasswordAsync(_profile.Id, Ct));
        await connections.DeleteAsync(_profile.Id, Ct);
        Assert.Empty(await vault.ReadAsync(Ct));
        Assert.Empty(connections.Profiles);
        Assert.Empty(_secrets.Calls);
        Assert.False(File.Exists(Path.Combine(_directory.Path, ConnectionStore.FileName)));
    }

    [Fact]
    public async Task Migration_waits_for_a_pending_remembered_legacy_password()
    {
        var secrets = new DelayedSecretStore();
        using var vault = new CredentialVault(_directory.Path);
        await using var connections = Open(vault, secrets: secrets);
        await connections.SaveAsync(_profile, null, Ct);
        _prompts.Secrets.Enqueue(new SecretAnswer("remembered just before migration", true));
        await CredentialLoop.RunAsync(_profile, connections.CredentialsOf(_profile.Id), _ => Task.CompletedTask, Ct);
        try
        {
            await secrets.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var migrating = connections.EnableVaultAsync(MasterPassword, Ct);
            Assert.False(migrating.IsCompleted);
            secrets.AllowWrite.TrySetResult();
            await migrating;
            Assert.Equal("remembered just before migration", Assert.Single(await vault.ReadAsync(Ct)).Password);
        }
        finally
        {
            secrets.AllowWrite.TrySetResult();
        }
    }

    [Fact]
    public async Task A_failed_vault_edit_keeps_the_profile_and_accepted_credentials()
    {
        using var vault = new CredentialVault(_directory.Path);
        await using var connections = Open(vault);
        await connections.EnableVaultAsync(MasterPassword, Ct);
        await connections.SaveAsync(_profile, "stored password", Ct);
        _prompts.Secrets.Enqueue(new SecretAnswer("session password", false));
        var source = connections.CredentialsOf(_profile.Id);
        var accepted = Assert.IsType<NetCredentials>(await source.GetAsync(_profile, true, Ct));
        source.Accepted(_profile, accepted);
        File.Delete(vault.FilePath);

        await Assert.ThrowsAsync<CredentialVaultException>(() =>
            connections.SaveAsync(_profile with { Name = "Failed edit" }, "new password", Ct));

        Assert.Equal(_profile, Assert.Single(connections.Profiles));
        Assert.Equal("session password", (await source.GetAsync(_profile, false, Ct))?.Password);
        Assert.Empty(_secrets.Calls);
    }

    [Fact]
    public async Task An_in_flight_authentication_cannot_replace_an_edited_vault_password()
    {
        using var vault = new CredentialVault(_directory.Path);
        await using var connections = Open(vault);
        await connections.EnableVaultAsync(MasterPassword, Ct);
        await connections.SaveAsync(_profile, "old", Ct);
        _prompts.Secrets.Enqueue(new SecretAnswer("late remembered", true));
        var source = connections.CredentialsOf(_profile.Id);
        var attempted = Assert.IsType<NetCredentials>(await source.GetAsync(_profile, true, Ct));

        await connections.SaveAsync(_profile, "edited password", Ct);
        source.Accepted(_profile, attempted);
        await connections.SaveAsync(_profile with { Name = "Renamed" }, null, Ct);

        Assert.Equal("edited password", Assert.Single(await vault.ReadAsync(Ct)).Password);
        Assert.Equal("edited password", (await source.GetAsync(_profile, false, Ct))?.Password);
    }

    [Fact]
    public async Task An_in_flight_legacy_key_read_cannot_read_keychain_after_migration()
    {
        using var vault = new CredentialVault(_directory.Path);
        var profile = _profile with { Auth = NetAuth.PrivateKey, PrivateKeyPath = "/example/key" };
        var key = await FixtureKeyAsync("ed25519-pass");
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var connections = Open(vault, async (_, ct) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                reading.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return key.ToArray();
        });
        await connections.SaveAsync(profile, null, "test passphrase", Ct);
        var source = connections.CredentialsOf(profile.Id);
        var authenticating = source.GetAsync(profile, false, Ct);
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await connections.EnableVaultAsync(MasterPassword, Ct);
            var legacyCallCount = _secrets.Calls.Count;
            release.TrySetResult();
            Assert.Null(await authenticating);
            Assert.Equal(legacyCallCount, _secrets.Calls.Count);
        }
        finally
        {
            release.TrySetResult();
            await authenticating;
        }
    }

    private NetConnections Open(CredentialVault vault, Func<string, CancellationToken, Task<byte[]>>? readKey = null,
        ISecretStore? secrets = null) =>
        new(new ProviderRegistry(), new ConnectionStore(_directory.Path), new TrustStore(_directory.Path), secrets ?? _secrets,
            _prompts, new Dictionary<NetProtocol, IRemoteSessionFactory>(), readKey ?? File.ReadAllBytesAsync, vault);

    private static Task<byte[]> FixtureKeyAsync(string name) =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "keys", name), Ct);

    public void Dispose() => _directory.Dispose();

    private sealed class DelayedSecretStore : ISecretStore
    {
        private readonly FakeSecretStore _inner = new();
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetAsync(string key, CancellationToken ct) => _inner.GetAsync(key, ct);

        public async Task SetAsync(string key, string value, CancellationToken ct)
        {
            Writing.TrySetResult();
            await AllowWrite.Task.WaitAsync(ct);
            await _inner.SetAsync(key, value, ct);
        }

        public Task DeleteAsync(string key, CancellationToken ct) => _inner.DeleteAsync(key, ct);
    }
}
