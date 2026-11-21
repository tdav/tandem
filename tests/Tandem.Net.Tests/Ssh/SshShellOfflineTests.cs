using Tandem.Core.Vfs;
using Tandem.Net.Ssh;
using Tandem.Net.Tests.Sftp;

namespace Tandem.Net.Tests.Ssh;

public sealed class SshShellOfflineTests
{
    [Fact]
    public async Task Closed_port_reports_connection_loss_without_credentials()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "SSH", NetProtocol.Sftp, "127.0.0.1",
            ProbeSupport.ClosedPort(), "user", NetAuth.Password);
        await using var shell = new SshShellTransportFactory(TimeSpan.FromSeconds(2))
            .Create(profile, new SftpTests.Credentials("never-log-me"), new SftpTests.Trust());
        var error = await Assert.ThrowsAsync<VfsConnectionLostException>(() =>
            shell.ConnectAsync(80, 24, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("never-log-me", error.ToString());
    }

    [Fact]
    public async Task Cancellation_stops_connection()
    {
        using var listener = new LoopbackListener(hangUp: false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var profile = new ConnectionProfile(Guid.NewGuid(), "SSH", NetProtocol.Sftp, "127.0.0.1",
            listener.Port, "user", NetAuth.Password);
        await using var shell = new SshShellTransportFactory()
            .Create(profile, new SftpTests.Credentials(), new SftpTests.Trust());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shell.ConnectAsync(80, 24, cancel.Token));
    }
}
