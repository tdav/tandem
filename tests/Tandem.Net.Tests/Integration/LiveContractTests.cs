using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;
using Tandem.Net.Ssh;
using Tandem.Net.Terminal;

namespace Tandem.Net.Tests.Integration;

[SupportedOSPlatform("macos")]
public abstract class LiveContractTests : ProviderContractTests, IAsyncDisposable
{
    private readonly List<(RemoteProvider Provider, VfsPath Root)> _roots = [];
    private LocalSshd? _localSshd;

    protected abstract string Variable { get; }
    protected override TimeSpan TimeResolution => TimeSpan.FromSeconds(1);

    protected override async Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync()
    {
        var server = LiveServer.Read(Variable);
        if (server is null && Variable == LiveServer.SftpVariable)
        {
            _localSshd = await LocalSshd.StartIfEnabledAsync(Ct);
            server = _localSshd?.Server;
        }

        Assert.SkipWhen(server is null, $"Set {Variable} and its credentials to run the live server tests (see README).");
        var pair = await LiveServer.OpenDirectoryAsync(server!, Ct);
        _roots.Add(pair);
        return pair;
    }

    protected override bool CanStoreModifiedTime(IFileSystemProvider provider) =>
        ((RemoteProvider)provider).SupportsModifiedTime;

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var (provider, root) in _roots)
            {
                try { await provider.DeleteAsync(root, recursive: true, CancellationToken.None); }
                finally { await provider.DisposeAsync(); }
            }
        }
        finally
        {
            if (_localSshd is not null) await _localSshd.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }
}

[SupportedOSPlatform("macos")]
public sealed class FtpLiveContractTests : LiveContractTests
{
    protected override string Variable => LiveServer.FtpVariable;
}

[SupportedOSPlatform("macos")]
public sealed class FtpsLiveContractTests : LiveContractTests
{
    protected override string Variable => LiveServer.FtpsVariable;
}

[SupportedOSPlatform("macos")]
public sealed class SftpLiveContractTests : LiveContractTests
{
    protected override string Variable => LiveServer.SftpVariable;
}

[SupportedOSPlatform("macos")]
public sealed class SshShellLiveTests
{
    [Fact]
    public async Task Idle_shell_read_cancels_and_closes_its_channel()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var server = LiveServer.Read(LiveServer.SftpVariable);
        await using var local = server is null ? await LocalSshd.StartIfEnabledAsync(ct) : null;
        server ??= local?.Server;
        Assert.SkipWhen(server is null, $"Set {LiveServer.SftpVariable} and its credentials, or TANDEM_TEST_LOCAL_SSHD=1.");
        await using var shell = new SshShellTransportFactory().Create(server!.Profile, server.Credentials, server.Trust);
        await shell.ConnectAsync(80, 24, ct);
        await shell.WriteAsync("sleep 10\r"u8.ToArray(), ct);
        var buffer = new byte[4096];
        using var cancelRead = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancelRead.CancelAfter(TimeSpan.FromSeconds(1));
        var draining = Task.Run(async () =>
        {
            while (await shell.ReadAsync(buffer, cancelRead.Token) > 0) { }
        }, ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draining.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    [Theory]
    [InlineData(NetProtocol.Sftp)]
    [InlineData(NetProtocol.Ssh)]
    public async Task Shell_echoes_commands_resizes_and_exits(NetProtocol protocol)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var server = LiveServer.Read(LiveServer.SftpVariable);
        await using var local = server is null ? await LocalSshd.StartIfEnabledAsync(ct) : null;
        server ??= local?.Server;
        Assert.SkipWhen(server is null, $"Set {LiveServer.SftpVariable} and its credentials, or TANDEM_TEST_LOCAL_SSHD=1.");
        await using var shell = new SshShellTransportFactory().Create(server!.Profile with { Protocol = protocol },
            server.Credentials, server.Trust);
        await shell.ConnectAsync(80, 24, ct);
        await shell.ResizeAsync(100, 30, ct);
        await shell.WriteAsync("echo tandem-$((6*7)); stty size; exit\r"u8.ToArray(), ct);
        var buffer = new byte[4096];
        var output = new StringBuilder();
        while (await shell.ReadAsync(buffer, ct) is var count && count > 0)
            output.Append(Encoding.UTF8.GetString(buffer, 0, count));
        Assert.Contains("tandem-42", output.ToString());
        Assert.Contains("30 100", output.ToString());
        using var screen = new TerminalEmulator(100, 30);
        screen.Feed(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.False(screen.IsAlternateScreen);
    }
}
