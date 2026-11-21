using System.Runtime.Versioning;
using Tandem.Core.Tests.Platform;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Net.Connections;
using Tandem.Net.Remote;
using Tandem.Net.Ssh;
using Tandem.Net.Tests.Remote;

namespace Tandem.Net.Tests.Ssh;

[SupportedOSPlatform("macos")]
public sealed class SshConsolesTests
{
    [Fact]
    public async Task Offers_only_sftp_profiles_and_reuses_saved_credentials()
    {
        using var directory = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeRemoteServer();
        var prompts = new Prompts();
        await using var connections = new NetConnections(new ProviderRegistry(), new ConnectionStore(directory.Path),
            new TrustStore(directory.Path), new FakeSecretStore(), prompts,
            new Dictionary<NetProtocol, IRemoteSessionFactory> { [NetProtocol.Sftp] = server.AsFactory() },
            (_, _) => throw new InvalidOperationException());
        var ssh = new ConnectionProfile(Guid.NewGuid(), "SSH", NetProtocol.Sftp, "127.0.0.1", 22,
            "user", NetAuth.Password);
        var ftp = ssh with { Id = Guid.NewGuid(), Protocol = NetProtocol.Ftp, Port = 21 };
        await connections.SaveAsync(ssh, "saved-value", ct);
        await connections.SaveAsync(ftp, null, ct);
        var factory = new RecordingFactory();
        var consoles = new SshConsoles(connections, factory);

        Assert.Equal(ssh.Id, Assert.Single(consoles.Profiles).Id);
        Assert.Throws<NotSupportedException>(() => consoles.Open(ftp.Id));
        await using var transport = consoles.Open(ssh.Id);
        await transport.ConnectAsync(80, 24, ct);
        Assert.Equal("saved-value", factory.Password);
        Assert.Equal(0, prompts.Questions);
    }

    private sealed class Prompts : INetPrompts
    {
        public int Questions { get; private set; }
        public Task<SecretAnswer?> AskSecretAsync(ConnectionProfile profile, SecretKind kind, bool previousWasWrong,
            CancellationToken ct)
        {
            Questions++;
            return Task.FromResult<SecretAnswer?>(null);
        }

        public Task<bool> ConfirmHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, HostKeyInfo? previous,
            CancellationToken ct) => Task.FromResult(false);

        public Task<bool> ConfirmCertificateAsync(ConnectionProfile profile, CertificateInfo certificate,
            CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingFactory : ITerminalTransportFactory
    {
        public string? Password { get; private set; }
        public ITerminalTransport Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust)
            => new Transport(this, profile, credentials);

        private sealed class Transport(RecordingFactory factory, ConnectionProfile profile, ICredentialSource credentials)
            : ITerminalTransport
        {
            public async Task ConnectAsync(int columns, int rows, CancellationToken ct) =>
                factory.Password = (await credentials.GetAsync(profile, false, ct))?.Password;
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => ValueTask.FromResult(0);
            public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
            public Task ResizeAsync(int columns, int rows, CancellationToken ct) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
