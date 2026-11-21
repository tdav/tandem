using System.IO.Compression;
using System.Text;
using Tandem.Archives;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Vfs.Local;

namespace Tandem.Net.Tests.Integration;

public sealed class LiveScenarioTests
{
    [Theory]
    [InlineData(LiveServer.FtpVariable)]
    [InlineData(LiveServer.FtpsVariable)]
    [InlineData(LiveServer.SftpVariable)]
    public async Task Sparse_two_gigabyte_upload_reports_progress_and_can_be_cancelled(string variable)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TANDEM_TEST_LARGE") == "1",
            $"Set TANDEM_TEST_LARGE=1 and {variable} with credentials to run large upload tests.");
        var server = LiveServer.Read(variable);
        Assert.SkipWhen(server is null, $"Set {variable} and its credentials (see README).");
        var ct = TestContext.Current.CancellationToken;
        var (remote, root) = await LiveServer.OpenDirectoryAsync(server!, ct);
        await using var lease = remote;
        var temporary = Directory.CreateTempSubdirectory("tandem-large-upload-");
        try
        {
            var path = Path.Combine(temporary.FullName, "sparse.bin");
            const long length = 2L * 1024 * 1024 * 1024;
            using (var file = File.Create(path)) file.SetLength(length);
            var providers = new ProviderRegistry();
            providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
            providers.Register(remote);
            var operation = new CopyOperation(providers, [VfsPath.Local(path)], root, new CopyOptions(),
                new ScriptedInteraction());
            long observed = 0;
            operation.Changed += (_, _) =>
            {
                var bytes = operation.Progress.BytesDone;
                if (bytes <= 0 || operation.State != OperationState.Running) return;
                Interlocked.Exchange(ref observed, bytes);
                operation.Cancel();
            };
            var result = await operation.RunAsync();
            Assert.Equal(OperationState.Cancelled, result.State);
            Assert.InRange(Interlocked.Read(ref observed), 1, length - 1);
            Assert.Null(await remote.GetEntryAsync(root.Combine("sparse.bin"), ct));
        }
        finally
        {
            try { await remote.DeleteAsync(root, recursive: true, CancellationToken.None); }
            finally { temporary.Delete(recursive: true); }
        }
    }

    [Theory]
    [InlineData(LiveServer.FtpVariable)]
    [InlineData(LiveServer.FtpsVariable)]
    [InlineData(LiveServer.SftpVariable)]
    public async Task Copies_from_zip_to_server_and_back_and_replaces_safely(string variable)
    {
        var server = LiveServer.Read(variable);
        Assert.SkipWhen(server is null, $"Set {variable} and its credentials (see README).");
        var ct = TestContext.Current.CancellationToken;
        var (remote, root) = await LiveServer.OpenDirectoryAsync(server!, ct);
        await using var lease = remote;
        var directory = Directory.CreateTempSubdirectory("tandem-live-zip-");
        try
        {
            var data = Enumerable.Range(0, 16384).Select(i => (byte)i).ToArray();
            using var archiveBytes = new MemoryStream();
            using (var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
            await using (var item = zip.CreateEntry("payload.bin").Open())
                await item.WriteAsync(data, ct);

            var local = new InMemoryFileSystem();
            local.AddFile("/archive.zip", archiveBytes.ToArray());
            local.AddDirectory("/out");
            var providers = new ProviderRegistry();
            providers.Register(local);
            providers.Register(remote);
            using var temps = new TempFileStore(directory.FullName);
            using var archive = await ArchiveProvider.OpenAsync("zip-test", providers,
                new VfsPath(local.Id, "/archive.zip"), temps, null, null, ct);
            providers.Register(archive);

            var remoteFile = root.Combine("payload.bin");
            await using (var stream = await remote.OpenWriteAsync(remoteFile, ct))
                await stream.WriteAsync(Encoding.UTF8.GetBytes("old"), ct);
            var script = new ScriptedInteraction().OnConflict(ConflictAction.Overwrite);
            await CopyAsync(new VfsPath(archive.Id, "/payload.bin"), root, script);
            await CopyAsync(remoteFile, new VfsPath(local.Id, "/out"), new ScriptedInteraction());

            await using var copied = await local.OpenReadAsync(new VfsPath(local.Id, "/out/payload.bin"), ct);
            using var actual = new MemoryStream();
            await copied.CopyToAsync(actual, ct);
            Assert.Equal(data, actual.ToArray());
            var names = new List<string>();
            await foreach (var entry in remote.EnumerateAsync(root, ct)) names.Add(entry.Name);
            Assert.Equal(["payload.bin"], names);

            async Task CopyAsync(VfsPath from, VfsPath to, ScriptedInteraction interaction)
            {
                var result = await new CopyOperation(providers, [from], to, new CopyOptions(), interaction).RunAsync();
                Assert.Equal(OperationState.Completed, result.State);
                Assert.Empty(result.Skipped);
            }
        }
        finally
        {
            try { await remote.DeleteAsync(root, recursive: true, CancellationToken.None); }
            finally { directory.Delete(recursive: true); }
        }
    }
}
