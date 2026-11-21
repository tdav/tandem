using Renci.SshNet;
using Renci.SshNet.Common;
using Tandem.Core.Vfs;
using Tandem.Net.Sftp;

namespace Tandem.Net.Tests.Sftp;

public sealed class SftpTests
{
    private static readonly ConnectionProfile Profile = new(Guid.NewGuid(), "SSH", NetProtocol.Sftp,
        "127.0.0.1", 22, "user", NetAuth.Password);

    [Fact]
    public void Password_and_keyboard_interactive_are_offered()
    {
        var info = SftpAuth.Build(Profile, new("test-password", null, null), TimeSpan.FromSeconds(2));
        Assert.Collection(info.AuthenticationMethods, a => Assert.IsType<PasswordAuthenticationMethod>(a),
            a => Assert.IsType<KeyboardInteractiveAuthenticationMethod>(a));
    }

    [Theory]
    [InlineData("ed25519", null)]
    [InlineData("rsa3072", null)]
    [InlineData("ecdsa256", null)]
    [InlineData("rsa-pem", null)]
    [InlineData("ed25519-pass", "secret")]
    public void Reads_supported_keys(string fixture, string? passphrase)
    {
        var bytes = File.ReadAllBytes(ProbeSupport.KeyPath(fixture));
        Assert.Equal(passphrase is not null, SftpAuth.NeedsPassphrase(bytes));
        var info = SftpAuth.Build(Profile with { Auth = NetAuth.PrivateKey }, new(null, bytes, passphrase),
            TimeSpan.FromSeconds(2));
        using var method = Assert.IsType<PrivateKeyAuthenticationMethod>(Assert.Single(info.AuthenticationMethods));
    }

    [Fact]
    public void Invalid_passphrase_is_an_authentication_error()
    {
        var bytes = File.ReadAllBytes(ProbeSupport.KeyPath("ed25519-pass"));
        var error = Assert.Throws<NetAuthenticationException>(() =>
            SftpAuth.Build(Profile with { Auth = NetAuth.PrivateKey }, new(null, bytes, "bad-phrase"),
                TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("bad-phrase", error.ToString());
        Assert.Equal("SHA256:abc", SftpAuth.Describe("ssh-ed25519", "abc").Sha256);
    }

    [Fact]
    public void Errors_use_vfs_contracts()
    {
        Assert.IsType<VfsNotFoundException>(SftpErrors.Map(new SftpPathNotFoundException("missing"), "/a"));
        Assert.IsType<VfsAccessDeniedException>(SftpErrors.Map(new SftpPermissionDeniedException("denied"), "/a"));
        Assert.IsType<VfsConnectionLostException>(SftpErrors.Map(new SshOperationTimeoutException("timeout"), "/a"));
        var canceled = new OperationCanceledException();
        Assert.Same(canceled, SftpErrors.Map(canceled, "/a"));
    }

    [Fact]
    public async Task Closed_port_is_a_connection_loss()
    {
        await using var session = new SftpSessionFactory(TimeSpan.FromSeconds(2)).Create(
            Profile with { Port = ProbeSupport.ClosedPort() }, new Credentials(), new Trust());
        await Assert.ThrowsAsync<VfsConnectionLostException>(() =>
            session.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Connect_honors_cancellation()
    {
        using var listener = new LoopbackListener(hangUp: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await using var session = new SftpSessionFactory().Create(Profile with { Port = listener.Port },
            new Credentials(), new Trust());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(timeout.Token));
        Assert.False(session.IsConnected);
    }

    internal sealed class Credentials(string password = "probe-secret") : ICredentialSource
    {
        public Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct)
            => Task.FromResult<NetCredentials?>(new(password, null, null));
        public void Accepted(ConnectionProfile profile, NetCredentials credentials) { }
    }

    internal sealed class Trust : ITrustDecisions
    {
        public Task<bool> TrustHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, CancellationToken ct)
            => Task.FromResult(false);
        public Task<bool> TrustCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct)
            => Task.FromResult(false);
    }
}
