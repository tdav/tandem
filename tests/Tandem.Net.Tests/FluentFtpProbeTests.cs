using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP;

namespace Tandem.Net.Tests;

/// <summary>
/// Pins what FluentFTP 55.1.0 does, for the FTP and FTPS source (task 16). It brings no other package.
/// <list type="bullet">
/// <item>A closed port: <c>Connect(ct)</c> throws <c>SocketException</c> (<c>ConnectionRefused</c>) at once.</item>
/// <item>A listener that accepts and says nothing: the greeting read waits <c>ReadTimeout</c>, then throws
/// <c>System.TimeoutException</c>; a listener that hangs up straight away is <b>not</b> seen as closed either: it also
/// ends in <c>TimeoutException</c> after <c>ReadTimeout</c> (15 s by default). So set <c>ReadTimeout</c>.</item>
/// <item><c>Connect(ct)</c> honours the token: <c>OperationCanceledException</c>.</item>
/// <item>Defaults (all in milliseconds): <c>ConnectTimeout</c> 0 (none of its own), <c>ReadTimeout</c> 15000,
/// <c>WriteTimeout</c> -1, <c>DataConnectionConnectTimeout</c> 15000, <c>DataConnectionReadTimeout</c> 15000;
/// <c>EncryptionMode</c> None, <c>DataConnectionType</c> AutoPassive, <c>RetryAttempts</c> 3; uploads, downloads and
/// listings are Binary (TYPE I); <c>ValidateAnyCertificate</c> false.</item>
/// </list>
/// The rest is in <see cref="FluentFtpApiShape"/>.
/// </summary>
public sealed class FluentFtpProbeTests
{
    [Fact]
    public async Task Connect_to_a_closed_local_port_fails_within_the_timeout()
    {
        using var client = new AsyncFtpClient("127.0.0.1", "user", "not-a-real-password", ProbeSupport.ClosedPort(),
            new FtpConfig { ConnectTimeout = 5000, ReadTimeout = 5000 });
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<SocketException>(() => client.Connect(TestContext.Current.CancellationToken));

        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connect_to_a_listener_that_hangs_up_or_stays_silent_waits_for_the_read_timeout(bool hangUp)
    {
        using var listener = new LoopbackListener(hangUp);
        using var client = new AsyncFtpClient("127.0.0.1", "user", "not-a-real-password", listener.Port,
            new FtpConfig { ConnectTimeout = 5000, ReadTimeout = 1000 });
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => client.Connect(TestContext.Current.CancellationToken));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Connect_honours_cancellation()
    {
        using var listener = new LoopbackListener(hangUp: false);
        using var client = new AsyncFtpClient("127.0.0.1", "user", "not-a-real-password", listener.Port,
            new FtpConfig { ConnectTimeout = 30000, ReadTimeout = 30000 });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Connect(cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Defaults()
    {
        var config = new FtpConfig();

        Assert.Equal(0, config.ConnectTimeout);
        Assert.Equal(15000, config.ReadTimeout);
        Assert.Equal(-1, config.WriteTimeout);
        Assert.Equal(15000, config.DataConnectionConnectTimeout);
        Assert.Equal(15000, config.DataConnectionReadTimeout);
        Assert.Equal(FtpEncryptionMode.None, config.EncryptionMode);
        Assert.Equal(FtpDataConnectionType.AutoPassive, config.DataConnectionType);
        Assert.Equal(3, config.RetryAttempts);
        Assert.Equal(FtpDataType.Binary, config.UploadDataType);
        Assert.Equal(FtpDataType.Binary, config.DownloadDataType);
        Assert.Equal(FtpDataType.Binary, config.ListingDataType);
        Assert.False(config.ValidateAnyCertificate);
    }
}

/// <summary>
/// Never called: it only has to compile, which pins the FluentFTP 55.1.0 API later tasks use. The facts below are read
/// from the assembly (its IL) and the package's XML docs, not probed against a server; task 16 checks them against its
/// in-process test server.
/// <list type="bullet">
/// <item>Every call on <c>AsyncFtpClient</c> takes a <c>CancellationToken</c> (named <c>token</c>, default none).</item>
/// <item>Certificate check: the <c>ValidateCertificate</c> <b>event on the client</b> (<c>BaseFtpClient</c>), not a
/// property of <c>FtpConfig</c>; its <c>FtpSslValidationEventArgs</c> carry <c>Certificate</c>, <c>Chain</c>,
/// <c>PolicyErrors</c> and the answer <c>Accept</c>. <c>FtpConfig.ValidateAnyCertificate</c> accepts every one: never
/// set it.</item>
/// <item><c>FtpEncryptionMode</c>: None, Implicit, Explicit, Auto (Auto may fall back to plain FTP: never use it).</item>
/// <item><c>GetListing(path, FtpListOption.AllFiles, ct)</c> lists hidden files; <c>FtpListItem.Type</c> is File,
/// Directory or Link (<c>LinkTarget</c> holds the target when the server says).</item>
/// <item><c>GetObjectInfo(path, dateModified, ct)</c> (docs and IL disagree). The XML doc says it throws
/// <c>InvalidOperationException</c> when the server lacks MLST (check <c>HasFeature(FtpCapability.MLST)</c>) and
/// returns null for <b>any</b> failure completion code or unparsable reply, not only "does not exist". The IL of 55.1.0
/// has no such throw: with MLST it sends <c>MLST path</c> and returns null on any failure; without MLST it lists the
/// parent folder and returns the entry whose <c>FullName</c> equals the path, or null. It never exposes the reply code.
/// <b>For task 16:</b> an existence check must never take null or an exception for "absent". When MLST is supported,
/// send <c>Execute("MLST " + path, ct)</c> itself and count only a 550 reply for that path as absent; otherwise list
/// the parent folder with <c>GetListing</c> and count as absent only a name missing from a listing that succeeded.
/// Any other failure is an error.</item>
/// <item><c>OpenRead</c>/<c>OpenWrite</c> take an <c>FtpDataType</c> (Binary by default); after the stream is disposed,
/// <c>GetReply(ct)</c> reads the transfer's final reply (<c>FtpReply.Success</c>, <c>Code</c>, <c>Message</c>).</item>
/// <item><c>Rename(path, dest, ct)</c> is RNFR/RNTO and nothing else (IL): it does not check whether <c>dest</c>
/// exists, contrary to its doc comment ("throws … if the destination file already exists"); many servers replace.
/// </item>
/// </list>
/// </summary>
internal static class FluentFtpApiShape
{
    public static async Task Ftp(CancellationToken ct)
    {
        var config = new FtpConfig
        {
            EncryptionMode = FtpEncryptionMode.Explicit,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ConnectTimeout = 15000,
            ReadTimeout = 30000,
            DataConnectionConnectTimeout = 15000,
            DataConnectionReadTimeout = 30000,
            SslProtocols = SslProtocols.None,
        };
        using var client = new AsyncFtpClient("127.0.0.1", "user", "password", 21, config);
        client.ValidateCertificate += (_, e) =>
        {
            var certificate = e.Certificate;
            var chain = e.Chain;
            e.Accept = e.PolicyErrors == System.Net.Security.SslPolicyErrors.None && certificate is not null &&
                       chain is not null;
        };
        await client.Connect(ct);

        FtpListItem[] items = await client.GetListing("/", FtpListOption.AllFiles, ct);
        foreach (var item in items)
            if (item.Type == FtpObjectType.Link && item.LinkTarget is null && item.Modified > DateTime.MinValue &&
                item.Size >= 0 && item.Chmod >= 0)
                return;

        FtpListItem? info = await client.GetObjectInfo("/a", dateModified: true, ct);
        await using (var read = await client.OpenRead("/a", FtpDataType.Binary, 0, checkIfFileExists: false, ct)) { }
        FtpReply reply = await client.GetReply(ct);
        await using (var write = await client.OpenWrite("/b", FtpDataType.Binary, checkIfFileExists: false, ct)) { }
        reply = await client.GetReply(ct);
        if (info is null || !reply.Success || reply.Code.Length == 0 || reply.Message.Length == 0) return;
        await client.Rename("/b", "/c", ct);
        await client.DeleteFile("/c", ct);
        await client.CreateDirectory("/d", ct);
        await client.DeleteDirectory("/d", ct);
        await client.SetModifiedTime("/a", DateTime.UtcNow, ct);
        await client.Chmod("/a", 644, ct);
    }
}
