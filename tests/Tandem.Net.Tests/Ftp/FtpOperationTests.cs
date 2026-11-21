using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Net.Ftp;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Sftp;

namespace Tandem.Net.Tests.Ftp;

[SupportedOSPlatform("macos")]
public sealed class FtpOperationTests : IDisposable
{
    private readonly TempDir _directory = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_control_connection_closed_while_idle_is_replaced_without_losing_the_listing()
    {
        await File.WriteAllTextAsync(_directory.Combine("item.txt"), "hello", Ct);
        await using var server = new TestFtpServer(_directory.Path);
        await using var remote = Remote(server);
        await remote.ConnectAsync(Ct);
        Assert.Equal("item.txt", Assert.Single(await ListAsync(remote)).Name);
        Assert.Equal(1, server.Commands.Count(command => command == "USER user"));

        server.DropAllConnections();

        Assert.Equal("item.txt", Assert.Single(await ListAsync(remote)).Name);
        Assert.Equal(2, server.Commands.Count(command => command == "USER user"));
    }

    [Theory]
    [InlineData(ErrorAction.Retry)]
    [InlineData(ErrorAction.Skip)]
    public async Task An_upload_cut_off_in_flight_is_reported_and_can_be_retried_safely(ErrorAction action)
    {
        var original = "previous contents"u8.ToArray();
        await File.WriteAllBytesAsync(_directory.Combine("item.txt"), original, Ct);
        await using var server = new TestFtpServer(_directory.Path);
        await using var remote = Remote(server);
        await remote.ConnectAsync(Ct);
        const string source = "/item.txt";
        var data = new byte[4 * 1024 * 1024];
        new Random(143).NextBytes(data);
        var local = new InMemoryFileSystem();
        local.AddFile(source, data);
        var providers = new ProviderRegistry();
        providers.Register(local);
        providers.Register(remote);
        var interaction = new ScriptedInteraction().OnConflict(ConflictAction.Overwrite).OnError(action);
        server.DropNextDataAfterBytes(4096);

        var result = await new CopyOperation(providers, [new VfsPath(local.Id, source)], remote.Root, new(),
            interaction).RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.IsType<VfsConnectionLostException>(Assert.Single(interaction.Errors).Error);
        if (action == ErrorAction.Retry)
        {
            Assert.Empty(result.Skipped);
            Assert.Equal(data, await File.ReadAllBytesAsync(_directory.Combine("item.txt"), Ct));
            Assert.Equal(2, server.Commands.Count(command => command.StartsWith("STOR ", StringComparison.Ordinal)));
        }
        else
        {
            Assert.Single(result.Skipped);
            Assert.Equal(original, await File.ReadAllBytesAsync(_directory.Combine("item.txt"), Ct));
        }
        Assert.Equal(["item.txt"], Directory.EnumerateFileSystemEntries(_directory.Path).Select(Path.GetFileName));
    }

    private static async Task<IReadOnlyList<FileEntry>> ListAsync(RemoteProvider provider)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in provider.EnumerateAsync(provider.Root, Ct)) entries.Add(entry);
        return entries;
    }

    private static RemoteProvider Remote(TestFtpServer server)
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "FTP", NetProtocol.Ftp, "127.0.0.1", server.Port,
            "user", NetAuth.Password);
        var factory = new FtpSessionFactory(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        return new("ftp-operations", profile,
            () => factory.Create(profile, new SftpTests.Credentials("test-password"), new SftpTests.Trust()),
            new HostLocation(VfsPath.Local("/"), null));
    }

    public void Dispose() => _directory.Dispose();
}
