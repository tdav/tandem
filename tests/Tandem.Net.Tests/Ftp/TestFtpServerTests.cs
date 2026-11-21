using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Tests.Vfs;

namespace Tandem.Net.Tests.Ftp;

[SupportedOSPlatform("macos")]
public sealed class TestFtpServerTests
{
    [Fact]
    public async Task The_fixture_rejects_paths_and_symlinks_outside_its_storage()
    {
        using var work = new TempDir();
        var root = Directory.CreateDirectory(work.Combine("ftp")).FullName;
        const string outsideText = "private to the test";
        var outside = work.Combine("outside.txt");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(outside, outsideText, ct);
        await File.WriteAllTextAsync(Path.Combine(root, "inside.txt"), "safe", ct);
        File.CreateSymbolicLink(Path.Combine(root, "link.txt"), outside);
        await using var server = new TestFtpServer(root);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, ct);
        await using var wire = client.GetStream();
        using var reader = new StreamReader(wire, Encoding.ASCII);
        await using var writer = new StreamWriter(wire, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        Assert.StartsWith("220 ", await reader.ReadLineAsync(ct));
        Assert.StartsWith("331 ", await SendAsync("USER user"));
        Assert.StartsWith("230 ", await SendAsync("PASS test-password"));

        Assert.StartsWith("550 ", await SendAsync("SIZE ../outside.txt"));
        Assert.StartsWith("550 ", await SendAsync("SIZE /link.txt"));
        Assert.StartsWith("550 ", await SendAsync("DELE ../outside.txt"));
        Assert.StartsWith("550 ", await SendAsync("DELE /link.txt"));
        Assert.Equal(outsideText, await File.ReadAllTextAsync(outside, ct));
        Assert.Equal("213 4", await SendAsync("SIZE /inside.txt"));

        async Task<string?> SendAsync(string command)
        {
            await writer.WriteLineAsync(command.AsMemory(), ct);
            return await reader.ReadLineAsync(ct);
        }
    }
}
