using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Archives.Tests;

/// <summary>Changing a zip that lies on the real disk.</summary>
[SupportedOSPlatform("macos")]
public sealed class ZipEditingLocalTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempFileStore _temp;
    private readonly ProviderRegistry _providers = new();
    private readonly string _folder;
    private readonly string _archivePath;
    private ArchiveProvider? _archive;

    public ZipEditingLocalTests()
    {
        _temp = new TempFileStore(_dir.Combine("store"));
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        _folder = _dir.Combine("archives");
        Directory.CreateDirectory(_folder);
        _archivePath = Path.Combine(_folder, "sample.zip");
    }

    public void Dispose()
    {
        _archive?.Dispose();
        _temp.Dispose();
        _dir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Arc(string path) => new("arc", path);

    private async Task<ArchiveProvider> OpenAsync()
    {
        await File.WriteAllBytesAsync(_archivePath, Fixture.Bytes("sample.zip"), Ct);
        _archive = await ArchiveProvider.OpenAsync("arc", _providers, VfsPath.Local(_archivePath), _temp, null,
            prompt: null, Ct);
        _providers.Register(_archive);
        return _archive;
    }

    private static async Task WriteAsync(IFileSystemProvider fs, VfsPath path, byte[] content)
    {
        await using var stream = await fs.OpenWriteAsync(path, Ct);
        await stream.WriteAsync(content, Ct);
    }

    /// <summary>Edits of every kind: a copy in through the engine (with a large compressible file and a Cyrillic
    /// name), a rename, a deletion, a new folder and an overwrite.</summary>
    private async Task EditAsync(ArchiveProvider archive)
    {
        var source = _dir.Combine("src");
        Directory.CreateDirectory(Path.Combine(source, "новая папка"));
        await File.WriteAllTextAsync(Path.Combine(source, "новая папка", "файл.txt"), "содержимое", Ct);
        await File.WriteAllTextAsync(Path.Combine(source, "big.txt"),
            string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i}\n")), Ct);
        var copy = new CopyOperation(_providers,
            [VfsPath.Local(Path.Combine(source, "новая папка")), VfsPath.Local(Path.Combine(source, "big.txt"))],
            Arc("/docs"), new CopyOptions(), new ScriptedInteraction());
        Assert.Equal(OperationState.Completed, (await copy.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct)).State);

        await archive.MoveAsync(Arc("/docs/readme.txt"), Arc("/docs/прочти.txt"), Ct);
        await archive.DeleteAsync(Arc("/a.txt"), recursive: false, Ct);
        await archive.CreateDirectoryAsync(Arc("/new/empty"), Ct);
        await WriteAsync(archive, Arc("/привет.txt"), "пока\n"u8.ToArray());
    }

    private static async Task AssertUnzipAcceptsAsync(string path)
    {
        var start = new ProcessStartInfo("/usr/bin/unzip", ["-t", path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var unzip = Process.Start(start)!;
        var output = await unzip.StandardOutput.ReadToEndAsync(Ct);
        await unzip.WaitForExitAsync(Ct);
        Assert.True(unzip.ExitCode == 0, output);
        Assert.Contains("No errors detected", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_unzip_accepts_the_archive_after_edits()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Runs the macOS unzip.");
        var archive = await OpenAsync();

        await EditAsync(archive);

        await AssertUnzipAcceptsAsync(_archivePath);

        using var reopened = await ArchiveProvider.OpenAsync("fresh", _providers, VfsPath.Local(_archivePath), _temp,
            null, prompt: null, Ct);
        Assert.Equal("содержимое", await TextAsync(reopened, "/docs/новая папка/файл.txt"));
        Assert.Equal("hello\n", await TextAsync(reopened, "/docs/прочти.txt"));
        Assert.Equal("пока\n", await TextAsync(reopened, "/привет.txt"));
        Assert.StartsWith("line 0\nline 1\n", await TextAsync(reopened, "/docs/big.txt"), StringComparison.Ordinal);
        Assert.Null(await reopened.GetEntryAsync(new VfsPath("fresh", "/a.txt"), Ct));
        Assert.True(new FileInfo(_archivePath).Length < 100_000); // the big text file was compressed
    }

    [Fact]
    public async Task System_unzip_accepts_an_archive_rewritten_through_a_stream_that_cannot_seek()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Runs the macOS unzip.");
        var host = new CountingHostProvider(); // its write streams do not seek, like a file inside another archive
        _providers.Register(host);
        host.Files.AddFile("/sample.zip", Fixture.Bytes("sample.zip"));
        using var archive = await ArchiveProvider.OpenAsync("streamed", _providers, new VfsPath("mem", "/sample.zip"),
            _temp, null, prompt: null, Ct);
        _providers.Register(archive);

        await WriteAsync(archive, new VfsPath("streamed", "/docs/новый.txt"), "новый\n"u8.ToArray());
        await archive.CreateDirectoryAsync(new VfsPath("streamed", "/made"), Ct);

        var copy = _dir.Combine("streamed.zip");
        await using (var read = await host.OpenReadAsync(new VfsPath("mem", "/sample.zip"), Ct))
        await using (var file = File.Create(copy))
            await read.CopyToAsync(file, Ct);
        await AssertUnzipAcceptsAsync(copy);
    }

    private static async Task<string> TextAsync(IFileSystemProvider fs, string path)
    {
        await using var stream = await fs.OpenReadAsync(new VfsPath(fs.Id, path), Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }

    [Fact]
    public async Task Edit_on_real_disk_leaves_no_temp_files_next_to_the_archive()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions.");
        var archive = await OpenAsync();

        await EditAsync(archive);

        Assert.Equal(["sample.zip"], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));

        // A rewrite that cannot even begin (the folder takes no new files) leaves the archive as it was.
        var before = await File.ReadAllBytesAsync(_archivePath, Ct);
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => archive.DeleteAsync(Arc("/docs"), recursive: true, Ct));
        }
        finally
        {
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(_archivePath, Ct));
        Assert.Equal(["sample.zip"], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
        Assert.Equal("hello\n", await TextAsync(archive, "/docs/прочти.txt"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    [Fact]
    public async Task Copying_a_name_the_zip_writer_would_change_is_an_item_error_and_the_others_land()
    {
        var archive = await OpenAsync();
        var source = _dir.Combine("names");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "plain.txt"), "plain", Ct);
        await File.WriteAllTextAsync(Path.Combine(source, "Report 11:21.txt"), "report", Ct);
        var interaction = new ScriptedInteraction().OnError(ErrorAction.Skip);
        var copy = new CopyOperation(_providers,
            [VfsPath.Local(Path.Combine(source, "plain.txt")), VfsPath.Local(Path.Combine(source, "Report 11:21.txt"))],
            Arc("/"), new CopyOptions(), interaction);

        var result = await copy.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(OperationState.Completed, result.State);
        var (item, error) = Assert.Single(interaction.Errors);
        Assert.Equal("Report 11:21.txt", item.Name);
        Assert.IsType<ArchiveEntryNameException>(error);
        Assert.Equal("plain", await TextAsync(archive, "/plain.txt"));
        Assert.Null(await archive.GetEntryAsync(Arc("/Report 11:21.txt"), Ct));
        Assert.Null(await archive.GetEntryAsync(Arc("/21.txt"), Ct));
    }

    [Theory]
    [InlineData("/a:b")]
    [InlineData("/back\\slash")]
    [InlineData("/docs/tail:")]
    public async Task Every_change_to_a_name_the_zip_writer_would_change_is_refused(string path)
    {
        var archive = await OpenAsync();

        await Assert.ThrowsAsync<ArchiveEntryNameException>(() => archive.OpenWriteAsync(Arc(path), Ct));
        await Assert.ThrowsAsync<ArchiveEntryNameException>(() => archive.CreateDirectoryAsync(Arc(path), Ct));
        await Assert.ThrowsAsync<ArchiveEntryNameException>(() => archive.MoveAsync(Arc("/a.txt"), Arc(path), Ct));
        await Assert.ThrowsAsync<ArchiveEntryNameException>(
            () => archive.CreateDirectoryAsync(Arc(path + "/inner"), Ct));
        Assert.NotNull(await archive.GetEntryAsync(Arc("/a.txt"), Ct));
    }

    [Fact]
    public async Task Zip_holding_a_name_the_writer_would_change_is_read_only()
    {
        using (var zip = System.IO.Compression.ZipFile.Open(_archivePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            await using (var entry = await zip.CreateEntry("Report 11:21.txt").OpenAsync(Ct))
                await entry.WriteAsync("report"u8.ToArray(), Ct);
            await using (var entry = await zip.CreateEntry("plain.txt").OpenAsync(Ct))
                await entry.WriteAsync("plain"u8.ToArray(), Ct);
        }
        _archive = await ArchiveProvider.OpenAsync("arc", _providers, VfsPath.Local(_archivePath), _temp, null,
            prompt: null, Ct);
        _providers.Register(_archive);

        Assert.Equal("report", await TextAsync(_archive, "/Report 11:21.txt"));
        Assert.Equal(ProviderCapabilities.None, _archive.Capabilities);
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _archive.OpenWriteAsync(Arc("/new.txt"), Ct));
    }
}
