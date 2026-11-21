using System.Runtime.Versioning;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Tools.Sync;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Net.Tests.Integration;

[SupportedOSPlatform("macos")]
public sealed class LiveSyncTests
{
    [Theory]
    [InlineData(LiveServer.FtpVariable)]
    [InlineData(LiveServer.FtpsVariable)]
    [InlineData(LiveServer.SftpVariable)]
    public async Task Folder_synchronization_between_disk_and_a_live_server_ends_equal(string variable)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var server = LiveServer.Read(variable);
        await using var localSshd = server is null && variable == LiveServer.SftpVariable
            ? await LocalSshd.StartIfEnabledAsync(ct) : null;
        server ??= localSshd?.Server;
        Assert.SkipWhen(server is null, $"Set {variable} and its credentials to run the live sync tests (see README).");

        var (provider, root) = await LiveServer.OpenDirectoryAsync(server!, ct);
        await using var remote = provider;
        using var folder = new TempDir();
        try
        {
            var registry = new ProviderRegistry();
            registry.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
            registry.Register(remote);
            await File.WriteAllTextAsync(folder.Combine("disk.txt"), "From disk", ct);
            await using (var output = await remote.OpenWriteAsync(root.Combine("server.txt"), ct))
                await output.WriteAsync("From server"u8.ToArray(), ct);
            var options = new SyncOptions(SyncCompareMode.Content);
            var plan = await SyncPlanner.CompareAsync(registry, folder.Root, root, options, null, ct);
            var operation = new SyncOperation(registry, plan,
                plan.Items.Select(item => new SyncDecision(item, item.DefaultAction)).ToArray(), new ScriptedInteraction());
            using var registration = ct.Register(operation.Cancel);
            var result = await operation.RunAsync();
            Assert.Empty(result.Skipped);
            var after = await SyncPlanner.CompareAsync(registry, folder.Root, root, options, null, ct);

            Assert.Empty(after.UnreadableFolders);
            Assert.Equal(2, after.Items.Count);
            Assert.All(after.Items, item => Assert.Equal(SyncState.Equal, item.State));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await remote.DeleteAsync(root, recursive: true, cleanup.Token);
        }
    }
}
