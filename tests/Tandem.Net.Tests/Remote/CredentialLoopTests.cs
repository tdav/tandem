using Tandem.Net.Remote;

namespace Tandem.Net.Tests.Remote;

public sealed class CredentialLoopTests
{
    private static readonly ConnectionProfile Profile =
        new(Guid.NewGuid(), "test", NetProtocol.Sftp, "host.test", 22, "user", NetAuth.Password);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Wrong_credentials_are_asked_again_at_most_three_times()
    {
        var source = new ScriptedSource("one", "two", "three", "four");
        var tried = new List<string?>();

        var error = await Assert.ThrowsAsync<NetAuthenticationException>(() => CredentialLoop.RunAsync(Profile, source,
            credentials =>
            {
                tried.Add(credentials.Password);
                throw new NetAuthenticationException("refused");
            }, Ct));

        Assert.False(error.Declined);
        Assert.Equal(["one", "two", "three"], tried);
        Assert.Equal([false, true, true], source.PreviousWasWrong);
        Assert.Empty(source.Accepted);
    }

    [Fact]
    public async Task Declined_credentials_end_as_declined()
    {
        var source = new ScriptedSource("wrong", null);
        var attempts = 0;

        var error = await Assert.ThrowsAsync<NetAuthenticationException>(() => CredentialLoop.RunAsync(Profile, source,
            _ =>
            {
                attempts++;
                throw new NetAuthenticationException("refused");
            }, Ct));

        Assert.True(error.Declined);
        Assert.Equal(1, attempts);
        Assert.Empty(source.Accepted);
    }

    [Fact]
    public async Task Accepted_credentials_are_reported_to_the_source()
    {
        var source = new ScriptedSource("wrong", "right");

        await CredentialLoop.RunAsync(Profile, source, credentials => credentials.Password == "right"
            ? Task.CompletedTask
            : throw new NetAuthenticationException("refused"), Ct);

        Assert.Equal("right", Assert.Single(source.Accepted).Password);
        Assert.Equal([false, true], source.PreviousWasWrong);
    }

    [Fact]
    public async Task Another_error_is_not_asked_again()
    {
        var source = new ScriptedSource("one", "two");

        await Assert.ThrowsAsync<Tandem.Core.Vfs.VfsConnectionLostException>(() => CredentialLoop.RunAsync(Profile,
            source, _ => throw new Tandem.Core.Vfs.VfsConnectionLostException("gone"), Ct));

        Assert.Single(source.PreviousWasWrong);
        Assert.Empty(source.Accepted);
    }

    private sealed class ScriptedSource(params string?[] passwords) : ICredentialSource
    {
        private readonly Queue<string?> _passwords = new(passwords);

        public List<bool> PreviousWasWrong { get; } = [];

        public List<NetCredentials> Accepted { get; } = [];

        public Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct)
        {
            PreviousWasWrong.Add(previousWasWrong);
            var password = _passwords.Dequeue();
            return Task.FromResult(password is null ? null : new NetCredentials(password, null, null));
        }

        void ICredentialSource.Accepted(ConnectionProfile profile, NetCredentials credentials) =>
            Accepted.Add(credentials);
    }
}
