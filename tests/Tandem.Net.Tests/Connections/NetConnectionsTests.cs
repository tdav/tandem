using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Tests.Platform;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Net.Connections;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Remote;

namespace Tandem.Net.Tests.Connections;

[SupportedOSPlatform("macos")]
public sealed class NetConnectionsTests : IAsyncDisposable
{
    private readonly TempDir _directory = new();
    private readonly ProviderRegistry _providers = new();
    private readonly FakeRemoteServer _server = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly ScriptedNetPrompts _prompts = new();
    private readonly ConnectionProfile _profile =
        new(Guid.NewGuid(), "Test files", NetProtocol.Sftp, "example.test", 22, "test", NetAuth.Password,
            InitialPath: "/work");
    private readonly NetConnections _connections;
    private static readonly VfsPath Origin = VfsPath.Local("/origin");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Func<string, CancellationToken, Task<byte[]>> _readKey = File.ReadAllBytesAsync;

    public NetConnectionsTests()
    {
        _providers.Register(new InMemoryFileSystem("local"));
        _server.AddDirectory("/work");
        _connections = new NetConnections(_providers, new ConnectionStore(_directory.Path), new TrustStore(_directory.Path),
            _secrets, _prompts, new Dictionary<NetProtocol, IRemoteSessionFactory>
            {
                [NetProtocol.Sftp] = _server.AsFactory(),
            }, (path, ct) => _readKey(path, ct));
    }

    [Fact]
    public async Task Connecting_registers_one_provider_with_initial_folder_and_first_origin()
    {
        await _connections.SaveAsync(_profile, null, Ct);
        var first = await _connections.ConnectAsync(_profile.Id, Origin, Ct);
        var second = await _connections.ConnectAsync(_profile.Id, VfsPath.Local("/other"), Ct);

        Assert.Equal("/work", first.Path);
        Assert.Equal(first, second);
        Assert.Equal(1, _server.ConnectCount);
        Assert.Equal(_profile, _connections.ProfileOf(first.ProviderId));
        var provider = Assert.IsType<RemoteProvider>(_providers.For(first));
        Assert.Equal(Origin, _providers.PersistentLocationOf(first));
        Assert.True(_connections.HasOpenConnections);

        await _connections.DisconnectAsync(first.ProviderId);
        Assert.False(_connections.HasOpenConnections);
        Assert.Equal(0, _server.OpenNow);
        Assert.Equal(first, await _connections.ConnectAsync(_profile.Id, VfsPath.Local("/other"), Ct));
        Assert.Same(provider, _providers.For(first));
        Assert.Equal(2, _server.ConnectCount);
    }

    [Fact]
    public async Task Failed_connection_can_be_retried_from_a_new_origin()
    {
        await _connections.SaveAsync(_profile, null, Ct);
        _server.Offline = true;
        await Assert.ThrowsAsync<VfsConnectionLostException>(() => _connections.ConnectAsync(_profile.Id, Origin, Ct));
        Assert.False(_connections.HasOpenConnections);
        _server.Offline = false;
        var secondOrigin = VfsPath.Local("/elsewhere");
        var opened = await _connections.ConnectAsync(_profile.Id, secondOrigin, Ct);
        Assert.Equal(secondOrigin, _providers.PersistentLocationOf(opened));
    }

    [Fact]
    public async Task Unknown_protocol_is_refused_before_connecting()
    {
        await _connections.SaveAsync(_profile with { Protocol = NetProtocol.Ftp, Port = 21 }, null, Ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => _connections.ConnectAsync(_profile.Id, Origin, Ct));
        Assert.Equal(0, _server.ConnectCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompted_password_is_reused_and_persisted_only_when_remembered(bool remember)
    {
        await _connections.SaveAsync(_profile, null, Ct);
        _prompts.Secrets.Enqueue(new SecretAnswer("generated-secret", remember));
        var credentials = _connections.CredentialsOf(_profile.Id);
        await CredentialLoop.RunAsync(_profile, credentials, _ => Task.CompletedTask, Ct);
        await _connections.ConnectAsync(_profile.Id, Origin, Ct);
        Assert.Equal("generated-secret", (await credentials.GetAsync(_profile, false, Ct))!.Password);
        Assert.Equal((SecretKind.Password, false), Assert.Single(_prompts.SecretQuestions));
        Assert.Equal(remember, await _connections.HasSavedPasswordAsync(_profile.Id, Ct));
        AssertSecretAbsentFromFiles("generated-secret");
    }

    [Fact]
    public async Task Wrong_saved_password_prompts_and_is_replaced_when_remembered()
    {
        await _connections.SaveAsync(_profile, "stale", Ct);
        _prompts.Secrets.Enqueue(new SecretAnswer("fresh", true));
        var attempted = new List<string?>();
        var credentials = _connections.CredentialsOf(_profile.Id);
        await CredentialLoop.RunAsync(_profile, credentials, offered =>
        {
            attempted.Add(offered.Password);
            return offered.Password == "fresh" ? Task.CompletedTask :
                Task.FromException(new NetAuthenticationException("refused"));
        }, Ct);
        await _connections.ConnectAsync(_profile.Id, Origin, Ct);

        Assert.Equal(new[] { "stale", "fresh" }, attempted);
        Assert.Equal((SecretKind.Password, true), Assert.Single(_prompts.SecretQuestions));
        Assert.Equal("fresh", _secrets.Secrets[NetConnections.SecretKey(_profile.Id, SecretKind.Password)]);
    }

    [Fact]
    public async Task Replacing_password_discards_accepted_credentials_without_changing_provider()
    {
        await _connections.SaveAsync(_profile, "old", Ct);
        var source = _connections.CredentialsOf(_profile.Id);
        await CredentialLoop.RunAsync(_profile, source, _ => Task.CompletedTask, Ct);
        var connected = await _connections.ConnectAsync(_profile.Id, Origin, Ct);

        await _connections.SaveAsync(_profile, "new", Ct);

        Assert.Empty(_prompts.SecretQuestions);
        Assert.Equal("new", (await source.GetAsync(_profile, false, Ct))!.Password);
        Assert.Equal(connected, await _connections.ConnectAsync(_profile.Id, Origin, Ct));
    }

    [Fact]
    public async Task Edited_endpoint_cannot_leak_new_password_to_a_previous_session()
    {
        await _connections.SaveAsync(_profile, "old", Ct);
        var source = _connections.CredentialsOf(_profile.Id);
        var edited = _profile with { Host = "new.example.test" };
        await _connections.SaveAsync(edited, "current", Ct);

        Assert.Null(await source.GetAsync(_profile, false, Ct));
        Assert.Equal("current", (await source.GetAsync(edited, false, Ct))!.Password);
    }

    [Fact]
    public async Task Authentication_during_password_update_waits_for_the_new_password()
    {
        var secrets = new DelayedSecretStore();
        await using var connections = new NetConnections(_providers, new ConnectionStore(_directory.Path),
            new TrustStore(_directory.Path), secrets, _prompts, new Dictionary<NetProtocol, IRemoteSessionFactory>(),
            File.ReadAllBytesAsync);
        await connections.SaveAsync(_profile, "old", Ct);
        var credentials = connections.CredentialsOf(_profile.Id);
        await CredentialLoop.RunAsync(_profile, credentials, _ => Task.CompletedTask, Ct);
        var saving = connections.SaveAsync(_profile, "new", Ct);
        try
        {
            await secrets.WritingNewPassword.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var requesting = credentials.GetAsync(_profile, false, Ct);
            Assert.False(requesting.IsCompleted);
            secrets.AllowWrite.TrySetResult();
            await saving;
            Assert.Equal("new", (await requesting)!.Password);
        }
        finally
        {
            secrets.AllowWrite.TrySetResult();
            await saving;
        }
    }

    [Fact]
    public async Task Declined_prompt_ends_as_declined_authentication()
    {
        await _connections.SaveAsync(_profile, null, Ct);
        var error = await Assert.ThrowsAsync<NetAuthenticationException>(() => CredentialLoop.RunAsync(_profile,
            _connections.CredentialsOf(_profile.Id), _ => throw new InvalidOperationException("No credentials expected"), Ct));
        Assert.True(error.Declined);
    }

    [Fact]
    public async Task Unavailable_keychain_still_allows_login_and_does_not_publish_secrets_in_errors()
    {
        await _connections.SaveAsync(_profile, null, Ct);
        const string secret = "unrecorded-secret";
        _secrets.Fail = new SecretStoreException($"Keychain failure mentioning {secret}");
        _prompts.Secrets.Enqueue(new SecretAnswer(secret, true));
        Exception? observed = null;
        _connections.SecretSaveFailed += (_, error) => observed = error;
        await CredentialLoop.RunAsync(_profile, _connections.CredentialsOf(_profile.Id), _ => Task.CompletedTask, Ct);
        await _connections.ConnectAsync(_profile.Id, Origin, Ct);
        Assert.IsType<SecretStoreException>(observed);
        Assert.DoesNotContain(secret, observed.ToString());
    }

    [Fact]
    public async Task Confirmed_key_is_remembered_and_declining_its_replacement_preserves_the_old_key()
    {
        var old = new HostKeyInfo("ssh-ed25519", "SHA256:old");
        var replacement = old with { Sha256 = "SHA256:new" };
        _prompts.ConfirmKeys = true;
        Assert.True(await _connections.Trust.TrustHostKeyAsync(_profile, old, Ct));
        Assert.True(await _connections.Trust.TrustHostKeyAsync(_profile, old, Ct));
        _prompts.ConfirmKeys = false;
        Assert.False(await _connections.Trust.TrustHostKeyAsync(_profile, replacement, Ct));
        Assert.True(await _connections.Trust.TrustHostKeyAsync(_profile, old, Ct));
        Assert.Equal(new[] { null, old }, _prompts.PreviousHostKeys);
    }

    [Fact]
    public async Task Declining_a_replacement_certificate_preserves_the_previous_exception()
    {
        var certificate = new CertificateInfo("subject", "issuer", "old", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1), "expired");
        _prompts.ConfirmCertificates = true;
        Assert.True(await _connections.Trust.TrustCertificateAsync(_profile, certificate, Ct));
        _prompts.ConfirmCertificates = false;
        Assert.False(await _connections.Trust.TrustCertificateAsync(_profile, certificate with { Sha256 = "new" }, Ct));
        Assert.True(await _connections.Trust.TrustCertificateAsync(_profile, certificate, Ct));
        Assert.Equal(2, _prompts.Certificates.Count);
    }

    [Theory]
    [InlineData("ed25519", false)]
    [InlineData("ed25519-pass", true)]
    public async Task Private_key_reader_supplies_bytes_and_only_encrypted_key_prompts_first(string filename, bool encrypted)
    {
        var key = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "keys", filename), Ct);
        var profile = _profile with { Auth = NetAuth.PrivateKey, PrivateKeyPath = "/managed/key" };
        await _connections.SaveAsync(profile, null, Ct);
        _prompts.Secrets.Enqueue(new SecretAnswer("test passphrase", false));
        _readKey = (path, _) =>
        {
            Assert.Equal("/managed/key", path);
            return Task.FromResult(key);
        };

        var offered = Assert.IsType<NetCredentials>(await _connections.CredentialsOf(profile.Id).GetAsync(profile, false, Ct));
        Assert.Equal(key, offered.PrivateKey);
        if (encrypted)
        {
            Assert.Equal("test passphrase", offered.Passphrase);
            Assert.Equal((SecretKind.Passphrase, false), Assert.Single(_prompts.SecretQuestions));
        }
        else
        {
            Assert.Null(offered.Passphrase);
            Assert.Empty(_prompts.SecretQuestions);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unusable_key_reports_its_path_without_leaking_content(bool missing)
    {
        const string contents = "sensitive sample bytes";
        var profile = _profile with { Auth = NetAuth.PrivateKey, PrivateKeyPath = "/invalid/key" };
        await _connections.SaveAsync(profile, null, Ct);
        _readKey = (_, _) => missing ? Task.FromException<byte[]>(new IOException(contents)) :
            Task.FromResult(System.Text.Encoding.UTF8.GetBytes(contents));
        var error = await Assert.ThrowsAsync<NetAuthenticationException>(() =>
            _connections.CredentialsOf(profile.Id).GetAsync(profile, false, Ct));
        Assert.Contains("/invalid/key", error.Message);
        Assert.DoesNotContain(contents, error.ToString());
    }

    [Fact]
    public async Task Deleting_a_connection_closes_its_provider_and_removes_both_secrets()
    {
        await _connections.SaveAsync(_profile, "a password", "a passphrase", Ct);
        var path = await _connections.ConnectAsync(_profile.Id, Origin, Ct);
        AssertSecretAbsentFromFiles("a password");
        AssertSecretAbsentFromFiles("a passphrase");
        await _connections.DeleteAsync(_profile.Id, Ct);
        Assert.Empty(_connections.Profiles);
        Assert.Empty(_secrets.Secrets);
        Assert.Null(_connections.ProfileOf(path.ProviderId));
        Assert.Equal(0, _server.OpenNow);
        Assert.Equal(Origin, _providers.PersistentLocationOf(path));
    }

    private void AssertSecretAbsentFromFiles(string secret)
    {
        foreach (var file in Directory.EnumerateFiles(_directory.Path))
            Assert.DoesNotContain(secret, File.ReadAllText(file));
    }

    public async ValueTask DisposeAsync()
    {
        await _connections.DisposeAsync();
        _directory.Dispose();
    }

    private sealed class DelayedSecretStore : ISecretStore
    {
        private readonly FakeSecretStore _inner = new();
        public TaskCompletionSource WritingNewPassword { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetAsync(string key, CancellationToken ct) => _inner.GetAsync(key, ct);

        public async Task SetAsync(string key, string secret, CancellationToken ct)
        {
            if (secret == "new")
            {
                WritingNewPassword.TrySetResult();
                await AllowWrite.Task.WaitAsync(ct);
            }
            await _inner.SetAsync(key, secret, ct);
        }

        public Task DeleteAsync(string key, CancellationToken ct) => _inner.DeleteAsync(key, ct);
    }
}
