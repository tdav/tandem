using System.Text;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Net;
using Tandem.Net.Ftp;
using Tandem.Net.Remote;
using Tandem.Net.Sftp;
using Tandem.Net.Tests.Ftp;

namespace Tandem.App.Tests.Integration;

public sealed class CompositionNetTests
{
    [Fact]
    public void Every_protocol_has_a_factory()
    {
        var factories = Composition.CreateSessionFactories();
        Assert.All(Enum.GetValues<NetProtocol>(), protocol => Assert.True(factories.ContainsKey(protocol)));
        Assert.IsType<SftpSessionFactory>(factories[NetProtocol.Sftp]);
        Assert.IsType<FtpSessionFactory>(factories[NetProtocol.Ftp]);
        Assert.Same(factories[NetProtocol.Ftp], factories[NetProtocol.FtpsExplicit]);
        Assert.Same(factories[NetProtocol.Ftp], factories[NetProtocol.FtpsImplicit]);
    }

    [Fact]
    public async Task Reader_expands_tilde_and_uses_the_registered_local_provider()
    {
        var providers = new ProviderRegistry();
        var local = new InMemoryFileSystem(VfsPath.LocalProviderId);
        var data = Encoding.UTF8.GetBytes("fixture");
        local.AddFile("/test-home/.ssh/test_key", data);
        providers.Register(local);

        var read = Composition.CreateKeyReader(providers, "/test-home");

        Assert.Equal(data, await read("~/.ssh/test_key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ftp_factory_transfers_through_the_provider()
    {
        var folder = Directory.CreateTempSubdirectory("tandem-ftp-app-");
        try
        {
            await using var server = new TestFtpServer(folder.FullName);
            var profile = new ConnectionProfile(Guid.NewGuid(), "FTP Test", NetProtocol.Ftp,
                "127.0.0.1", server.Port, "user", NetAuth.Password);
            var factory = Composition.CreateSessionFactories()[NetProtocol.Ftp];
            await using var remote = new RemoteProvider("net:ftp", profile,
                () => factory.Create(profile, new Credentials(), new Trust()), new HostLocation(VfsPath.Local("/"), null));
            var ct = TestContext.Current.CancellationToken;
            await remote.ConnectAsync(ct);
            var file = remote.Root.Combine("проверка.bin");
            var bytes = new byte[] { 0, 1, 2, 10, 13, 255 };
            await using (var write = await remote.OpenWriteAsync(file, ct))
                await write.WriteAsync(bytes, ct);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder.FullName, file.Name), ct));
            Assert.Equal(bytes.Length, (await remote.GetEntryAsync(file, ct))?.Size);
        }
        finally { folder.Delete(recursive: true); }
    }

    private sealed class Credentials : ICredentialSource
    {
        public Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct)
            => Task.FromResult<NetCredentials?>(new("test-password", null, null));
        public void Accepted(ConnectionProfile profile, NetCredentials credentials) { }
    }

    private sealed class Trust : ITrustDecisions
    {
        public Task<bool> TrustHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, CancellationToken ct)
            => Task.FromResult(false);
        public Task<bool> TrustCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct)
            => Task.FromResult(false);
    }
}
