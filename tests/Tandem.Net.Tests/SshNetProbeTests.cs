using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Tandem.Net.Tests;

/// <summary>
/// Pins what SSH.NET 2026.0.1 does, for the SFTP source and the console (tasks 16, 17, 26, 28). Brought along:
/// BouncyCastle.Cryptography 2.7.0. Its other dependency, Microsoft.Extensions.Logging.Abstractions 8.0.3, is part of
/// the .NET 11 shared framework, so restore prunes it.
/// <list type="bullet">
/// <item>Keys: <c>new PrivateKeyFile(path)</c> or <c>(path, passphrase)</c> (also from a <c>Stream</c>) reads OpenSSH
/// ed25519, RSA and ECDSA keys and PEM RSA keys; <c>Key.ToString()</c> is the algorithm ("ssh-ed25519", "ssh-rsa",
/// "ecdsa-sha2-nistp256").</item>
/// <item>An encrypted key without a passphrase, or with an empty one, throws <c>SshPassPhraseNullOrEmptyException</c>
/// (a <c>SshException</c>). A wrong passphrase throws a plain <c>SshException</c> ("The random check bytes of the
/// OpenSSH key do not match …"); so does a file that is not a private key ("Invalid private key file."). Only the
/// type tells "needs a passphrase" from the rest; a wrong passphrase is not a type of its own.</item>
/// <item>Connecting: <c>ConnectionInfo.Timeout</c> (default 30 s) bounds the whole connect, which then throws
/// <c>SshOperationTimeoutException</c>. A closed port throws <c>SocketException</c> (<c>ConnectionRefused</c>) at
/// once; a listener that hangs up gives <c>SocketException</c> (<c>ConnectionReset</c>) or
/// <c>SshConnectionException</c>, depending on timing. <c>ConnectAsync(ct)</c> honours the token
/// (<c>OperationCanceledException</c>), also while the server says nothing.</item>
/// <item><c>SftpClient.OperationTimeout</c> and <c>KeepAliveInterval</c> default to -1 ms (infinite, off): set them.
/// </item>
/// </list>
/// The rest is in <see cref="SshNetApiShape"/>.
/// </summary>
public sealed class SshNetProbeTests
{
    [Theory]
    [InlineData("ed25519", "ssh-ed25519")]
    [InlineData("rsa3072", "ssh-rsa")]
    [InlineData("ecdsa256", "ecdsa-sha2-nistp256")]
    [InlineData("rsa-pem", "ssh-rsa")]
    public void Private_key_file_loads(string fixture, string algorithm)
    {
        using var key = new PrivateKeyFile(ProbeSupport.KeyPath(fixture));

        Assert.Equal(algorithm, key.Key.ToString());
        using var stream = File.OpenRead(ProbeSupport.KeyPath(fixture));
        using var fromStream = new PrivateKeyFile(stream);
        Assert.Equal(algorithm, fromStream.Key.ToString());
    }

    [Fact]
    public void Encrypted_key_loads_with_its_passphrase()
    {
        using var key = new PrivateKeyFile(ProbeSupport.KeyPath("ed25519-pass"), "secret");

        Assert.Equal("ssh-ed25519", key.Key.ToString());
    }

    [Fact]
    public void Encrypted_key_without_or_with_wrong_passphrase_fails_recognisably()
    {
        var path = ProbeSupport.KeyPath("ed25519-pass");

        Assert.Throws<SshPassPhraseNullOrEmptyException>(() => new PrivateKeyFile(path));
        Assert.Throws<SshPassPhraseNullOrEmptyException>(() => new PrivateKeyFile(path, ""));
        var wrong = Assert.Throws<SshException>(() => new PrivateKeyFile(path, "wrong"));
        Assert.IsNotType<SshPassPhraseNullOrEmptyException>(wrong);
        Assert.DoesNotContain("wrong", wrong.Message, StringComparison.Ordinal); // the passphrase is not echoed
        var notAKey = Assert.Throws<SshException>(() => new PrivateKeyFile(ProbeSupport.KeyPath("ed25519.pub")));
        Assert.Equal("Invalid private key file.", notAKey.Message);
    }

    [Fact]
    public async Task Connect_to_a_closed_local_port_fails_within_the_timeout()
    {
        using var client = new SftpClient(Info(ProbeSupport.ClosedPort(), TimeSpan.FromSeconds(5)));
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<SocketException>(() =>
            client.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Connect_to_a_listener_that_hangs_up_fails_recognisably()
    {
        using var listener = new LoopbackListener(hangUp: true);
        using var client = new SftpClient(Info(listener.Port, TimeSpan.FromSeconds(5)));

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.True(error is SocketException or SshConnectionException, error.GetType().FullName);
    }

    [Fact]
    public async Task Connect_to_a_silent_listener_times_out()
    {
        using var listener = new LoopbackListener(hangUp: false);
        using var client = new SftpClient(Info(listener.Port, TimeSpan.FromSeconds(1)));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<SshOperationTimeoutException>(() =>
            client.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Connect_honours_cancellation()
    {
        using var listener = new LoopbackListener(hangUp: false);
        using var client = new SftpClient(Info(listener.Port, TimeSpan.FromSeconds(30)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Timeouts_are_infinite_until_set()
    {
        using var client = new SftpClient(Info(22, TimeSpan.FromSeconds(15))); // never connects

        Assert.Equal(Timeout.InfiniteTimeSpan, client.OperationTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), new ConnectionInfo("127.0.0.1", "u", Password()).Timeout);
    }

    /// <summary>ShellStream.ChangeWindowSize(columns, rows, widthPixels, heightPixels) sends the new size ("window-change"
    /// request). That a column/row size wins over the pixel size when nonzero is protocol knowledge (RFC 4254, 6.7) and the
    /// method's doc, not probed: pass 0 for the pixels.</summary>
    [Fact]
    public void Shell_stream_can_be_resized()
    {
        var resize = typeof(ShellStream).GetMethod("ChangeWindowSize", BindingFlags.Public | BindingFlags.Instance,
            [typeof(uint), typeof(uint), typeof(uint), typeof(uint)]);

        Assert.NotNull(resize);
        Assert.Equal(typeof(void), resize.ReturnType);
    }

    private static ConnectionInfo Info(int port, TimeSpan timeout) =>
        new("127.0.0.1", port, "user", Password()) { Timeout = timeout };

    private static PasswordAuthenticationMethod Password() => new("user", "not-a-real-password");
}

/// <summary>
/// Never called: it only has to compile, which pins the SSH.NET 2026.0.1 API later tasks use. Facts read from the
/// assembly (its IL), not from a live server; task 17 checks them against one (<c>TANDEM_TEST_SFTP_URL</c>):
/// <list type="bullet">
/// <item>Every async call takes a <c>CancellationToken</c>. <c>DeleteFileAsync</c> and <c>RenameFileAsync</c> have no
/// default for it; the others do.</item>
/// <item><b>Paths are resolved by the server first.</b> <c>GetAttributes(Async)</c>, <c>Get(Async)</c>,
/// <c>Exists(Async)</c>, <c>DeleteFileAsync</c>, <c>DeleteDirectoryAsync</c>, <c>CreateDirectoryAsync</c>,
/// <c>RenameFileAsync</c>, <c>SetAttributes</c> and <c>SymbolicLink</c> send <c>SSH_FXP_REALPATH</c> for the whole
/// path and use the answer (falling back to the parent's real path plus the name when that fails). OpenSSH's
/// realpath follows symbolic links, so on a link these act on <b>its target</b>: <c>GetAttributesAsync(link)</c> (an
/// lstat, but of the resolved path) describes the target, and <c>DeleteFileAsync(link)</c> would delete the target.
/// </item>
/// <item>Information about the link itself: only the entries of <c>ListDirectoryAsync</c> of its folder
/// (<c>ISftpFile.Attributes.IsSymbolicLink</c>, from READDIR). Removing the link itself: <c>ISftpFile.DeleteAsync</c>
/// on that listed entry, which sends <c>SSH_FXP_REMOVE</c> for its <c>FullName</c> without resolving it.</item>
/// <item>Information about the target: <c>GetAttributesAsync(path)</c> (see above). Reading the target of a link
/// (readlink): <b>no public call</b> (<c>SSH_FXP_READLINK</c> is internal). Creating a link:
/// <c>SymbolicLink(path, linkPath)</c>, synchronous only, and both paths are resolved first, so a relative target
/// becomes absolute. Opening a link with <c>OpenAsync</c> opens its target (the server's open follows it).</item>
/// <item><c>RenameFileAsync</c> sends plain <c>SSH_FXP_RENAME</c> (OpenSSH refuses when the new name exists);
/// <c>RenameFile(old, new, isPosix: true)</c>, synchronous only, sends posix-rename@openssh.com, which replaces.</item>
/// <item><c>SftpFileStream.CanSeek</c> is true when the fstat right after opening succeeds with a size (normally);
/// false when the server refuses fstat.</item>
/// <item><c>HostKeyReceived</c> (on <c>BaseClient</c>) gives <c>HostKeyEventArgs</c>: <c>HostKeyName</c> (e.g.
/// "ssh-ed25519"), <c>FingerPrintSHA256</c> (base64 without padding, as OpenSSH prints it after "SHA256:"),
/// <c>HostKey</c>, <c>KeyLength</c>; setting <c>CanTrust = false</c> makes the connect fail.</item>
/// <item>Shell: <c>SshClient.CreateShellStream(terminalName, columns, rows, widthPixels, heightPixels, bufferSize,
/// terminalModes)</c>; resize with <c>ShellStream.ChangeWindowSize</c>; output arrives through <c>Read</c>/<c>ReadAsync
/// </c> or the <c>DataReceived</c> event; <c>Closed</c> tells the channel ended.</item>
/// </list>
/// </summary>
internal static class SshNetApiShape
{
    public static async Task Sftp(SftpClient client, CancellationToken ct)
    {
        client.HostKeyReceived += (_, e) =>
        {
            string name = e.HostKeyName;
            string sha256 = e.FingerPrintSHA256;
            byte[] key = e.HostKey;
            e.CanTrust = name.Length > 0 && sha256.Length > 0 && key.Length > 0;
        };
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        client.OperationTimeout = TimeSpan.FromSeconds(30);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        await client.ConnectAsync(ct);

        await foreach (ISftpFile entry in client.ListDirectoryAsync("/", ct))
        {
            SftpFileAttributes attributes = entry.Attributes;
            bool link = attributes.IsSymbolicLink;
            bool directory = attributes.IsDirectory;
            long size = attributes.Size;
            DateTime modified = attributes.LastWriteTimeUtc;
            if (link && !directory && size >= 0 && modified > DateTime.MinValue) await entry.DeleteAsync(ct);
        }

        SftpFileStream stream = await client.OpenAsync("/a", FileMode.Open, FileAccess.Read, ct);
        bool canSeek = stream.CanSeek;
        await stream.DisposeAsync();
        await client.CreateDirectoryAsync("/d", ct);
        await client.DeleteFileAsync("/a", ct);
        await client.DeleteDirectoryAsync("/d", ct);
        await client.RenameFileAsync("/a", "/b", ct);
        SftpFileAttributes target = await client.GetAttributesAsync("/b", ct);
        target.LastWriteTimeUtc = DateTime.UtcNow;
        if (canSeek) target.SetPermissions(0b110_100_100);
        client.SetAttributes("/b", target);
        client.SymbolicLink("/b", "/link");
        client.RenameFile("/b", "/c", isPosix: true);
    }

    public static ShellStream Shell(SshClient client)
    {
        var shell = client.CreateShellStream("xterm-256color", 80, 24, 0, 0, 64 * 1024,
            new Dictionary<TerminalModes, uint> { [TerminalModes.IUTF8] = 1 });
        shell.ChangeWindowSize(120, 40, 0, 0);
        shell.DataReceived += (_, e) => _ = e.Data;
        shell.Closed += (_, _) => { };
        return shell;
    }
}
