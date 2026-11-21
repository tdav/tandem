using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tandem.Core.Tests.Vfs;
using System.Runtime.Versioning;

namespace Tandem.Net.Tests.Integration;

/// <summary>An opt-in, loopback-only SSH test process with fresh keys and a private configuration.</summary>
[SupportedOSPlatform("macos")]
internal sealed class LocalSshd : IAsyncDisposable
{
    private readonly TempDir _directory = new();
    private Process? _process;
    private Task<string>? _stderr;

    public LiveServer Server { get; private set; } = null!;

    public static async Task<LocalSshd?> StartIfEnabledAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("TANDEM_TEST_LOCAL_SSHD") != "1") return null;
        var instance = new LocalSshd();
        try
        {
            await instance.StartAsync(ct);
            return instance;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await instance.DisposeAsync();
            Assert.Skip($"Local sshd could not start: {error.Message}");
            return null;
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        var hostKey = _directory.Combine("host");
        var userKey = _directory.Combine("user");
        await GenerateAsync(hostKey, ct);
        await GenerateAsync(userKey, ct);
        var port = ProbeSupport.ClosedPort();
        var config = _directory.Combine("sshd_config");
        await File.WriteAllLinesAsync(config,
        [
            "ListenAddress 127.0.0.1", $"Port {port}", $"HostKey \"{hostKey}\"",
            $"AuthorizedKeysFile \"{userKey}.pub\"", $"PidFile \"{_directory.Combine("sshd.pid")}\"",
            "UsePAM no", "PasswordAuthentication no", "KbdInteractiveAuthentication no",
            "PubkeyAuthentication yes", "StrictModes no", "AllowTcpForwarding no", "X11Forwarding no",
            "Subsystem sftp internal-sftp",
        ], ct);

        var options = new ProcessStartInfo("/usr/sbin/sshd")
            { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-D", "-e", "-f", config }) options.ArgumentList.Add(arg);
        _process = Process.Start(options) ?? throw new IOException("sshd process could not start.");
        _stderr = _process.StandardError.ReadToEndAsync(ct);
        for (var i = 0; i < 40; i++)
        {
            if (_process.HasExited)
                throw new IOException((await _stderr).Trim());
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port, ct);
                var profile = new ConnectionProfile(Guid.NewGuid(), "Local SSH test", NetProtocol.Sftp,
                    "127.0.0.1", port, Environment.UserName, NetAuth.PrivateKey, userKey, _directory.Path);
                Server = new(profile, new LiveServer.TestCredentials(null, await File.ReadAllBytesAsync(userKey, ct), null),
                    new LiveServer.TestTrust(false));
                return;
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
            {
                await Task.Delay(50, ct);
            }
        }
        throw new TimeoutException("sshd did not listen on loopback.");
    }

    private static async Task GenerateAsync(string path, CancellationToken ct)
    {
        var options = new ProcessStartInfo("/usr/bin/ssh-keygen")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", "", "-f", path }) options.ArgumentList.Add(arg);
        using var process = Process.Start(options) ?? throw new IOException("ssh-keygen could not start.");
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        if (process.ExitCode != 0) throw new IOException((await error).Trim());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is not { } process) return;
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            process.Dispose();
            _process = null;
        }
        finally { _directory.Dispose(); }
    }
}
