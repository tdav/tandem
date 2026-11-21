using System.Runtime.Versioning;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Net.Ftp;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Sftp;

namespace Tandem.Net.Tests.Ftp;

[SupportedOSPlatform("macos")]
public sealed class FtpSessionTests : IDisposable
{
    private readonly TempDir _directory = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Binary_transfer_and_listing_round_trip(bool mlsd)
    {
        await using var server = new TestFtpServer(_directory.Path, mlsd);
        await using var session = Session(server);
        var ct = TestContext.Current.CancellationToken;
        await session.ConnectAsync(ct);
        await session.CreateDirectoryAsync("/test", ct);
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        const string path = "/test/имя с пробелами";
        await using (var stream = await session.OpenWriteAsync(path, ct))
            await stream.WriteAsync(bytes, ct);

        var item = Assert.IsType<RemoteEntry>(await session.StatAsync(path, ct));
        Assert.Equal("имя с пробелами", item.Name);
        Assert.Equal(bytes.Length, item.Size);

        await using var output = new MemoryStream();
        await using (var stream = await session.OpenReadAsync(path, ct))
            await stream.CopyToAsync(output, ct);
        Assert.Equal(bytes, output.ToArray());
        Assert.DoesNotContain("TYPE A", server.Commands);
        await session.DeleteFileAsync(path, ct);
        await session.DeleteDirectoryAsync("/test", ct);
        Assert.Null(await session.StatAsync(path, ct));
    }

    [Fact]
    public async Task Store_failure_is_reported_when_stream_closes()
    {
        await using var server = new TestFtpServer(_directory.Path) { StoreCompletionCode = 552 };
        await using var session = Session(server);
        var ct = TestContext.Current.CancellationToken;
        await session.ConnectAsync(ct);
        var stream = await session.OpenWriteAsync("/target", ct);
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, ct);
        await Assert.ThrowsAsync<IOException>(() => stream.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Explicit_tls_never_sends_credentials_to_a_plain_server()
    {
        await using var server = new TestFtpServer(_directory.Path);
        await using var session = Session(server, NetProtocol.FtpsExplicit);
        await Assert.ThrowsAnyAsync<IOException>(() => session.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Contains("AUTH TLS", server.Commands);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("USER") || command.StartsWith("PASS"));
    }

    [Fact]
    public async Task Missing_folder_is_not_found()
    {
        await using var server = new TestFtpServer(_directory.Path);
        await using var session = Session(server);
        var ct = TestContext.Current.CancellationToken;
        await session.ConnectAsync(ct);
        await Assert.ThrowsAsync<VfsNotFoundException>(async () =>
        {
            await foreach (var _ in session.ListAsync("/missing", ct)) { }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrong_password_causes_another_prompt_and_only_success_is_accepted(bool eventuallyCorrect)
    {
        const string incorrect = "incorrect-password";
        await using var server = new TestFtpServer(_directory.Path);
        var credentials = new CredentialScript(eventuallyCorrect ? [incorrect, "test-password"]
            : [incorrect, incorrect, incorrect]);
        await using var session = Session(server, credentials: credentials);
        var ct = TestContext.Current.CancellationToken;

        if (eventuallyCorrect)
        {
            await session.ConnectAsync(ct);
            Assert.True(session.IsConnected);
            Assert.Equal([false, true], credentials.PreviousFailures);
            Assert.Equal("test-password", Assert.Single(credentials.AcceptedPasswords));
        }
        else
        {
            var error = await Assert.ThrowsAsync<NetAuthenticationException>(() => session.ConnectAsync(ct));
            Assert.False(session.IsConnected);
            Assert.Equal([false, true, true], credentials.PreviousFailures);
            Assert.Empty(credentials.AcceptedPasswords);
            Assert.DoesNotContain(incorrect, error.ToString());
        }

        Assert.Equal(credentials.PreviousFailures.Count, server.Commands.Count(command => command == "PASS"));
        Assert.All(server.Commands, command => Assert.DoesNotContain(incorrect, command));
    }

    [Fact]
    public async Task Anonymous_login_uses_no_credential_prompt()
    {
        await using var server = new TestFtpServer(_directory.Path);
        var credentials = new CredentialScript([]);
        await using var session = Session(server, auth: NetAuth.Anonymous, credentials: credentials);

        await session.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(session.IsConnected);
        Assert.Empty(credentials.PreviousFailures);
        Assert.Empty(credentials.AcceptedPasswords);
        Assert.Contains("USER anonymous", server.Commands);
        Assert.Contains("PASS", server.Commands);
    }

    private static IRemoteSession Session(TestFtpServer server, NetProtocol protocol = NetProtocol.Ftp,
        NetAuth auth = NetAuth.Password, ICredentialSource? credentials = null) =>
        new FtpSessionFactory(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3)).Create(
            new(Guid.NewGuid(), "FTP", protocol, "127.0.0.1", server.Port, "user", auth),
            credentials ?? new SftpTests.Credentials("test-password"), new SftpTests.Trust());

    private sealed class CredentialScript(IReadOnlyList<string> passwords) : ICredentialSource
    {
        public List<bool> PreviousFailures { get; } = [];
        public List<string?> AcceptedPasswords { get; } = [];

        public Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct)
        {
            var password = passwords[PreviousFailures.Count];
            PreviousFailures.Add(previousWasWrong);
            return Task.FromResult<NetCredentials?>(new(password, null, null));
        }

        public void Accepted(ConnectionProfile profile, NetCredentials credentials) =>
            AcceptedPasswords.Add(credentials.Password);
    }

    public void Dispose() => _directory.Dispose();
}
