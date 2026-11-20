using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;

namespace Tandem.Archives.Tests;

/// <summary>The archive provider over an in-memory host holding the fixture's bytes.</summary>
[SupportedOSPlatform("macos")]
public sealed class ArchiveProviderTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset HostTime = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly byte[] DataBin = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private readonly CountingHostProvider _host = new();
    private readonly ProviderRegistry _providers = new();
    private readonly TempDir _tempDir = new();
    private readonly TempFileStore _temp;

    public ArchiveProviderTests()
    {
        _temp = new TempFileStore(_tempDir.Combine("store"));
        _providers.Register(_host);
    }

    public void Dispose()
    {
        _temp.Dispose();
        _tempDir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Arc(string path) => new("arc", path);

    private static VfsPath Mem(string path) => new("mem", path);

    private async Task<ArchiveProvider> OpenAsync(string fixture, string? password = null, string? hostPath = null)
    {
        var path = hostPath ?? "/arc/" + fixture;
        _host.Files.AddFile(path, Fixture.Bytes(fixture), HostTime);
        return await OpenHostAsync(path, password);
    }

    private async Task<ArchiveProvider> OpenHostAsync(string path, string? password = null)
    {
        var provider = await ArchiveProvider.OpenAsync("arc", _providers, Mem(path), _temp, password, prompt: null,
            Ct);
        _providers.Register(provider);
        return provider;
    }

    private static async Task<List<FileEntry>> ListAsync(IFileSystemProvider fs, VfsPath directory)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(directory, Ct)) entries.Add(entry);
        return entries;
    }

    private static async Task<byte[]> ReadAsync(IFileSystemProvider fs, VfsPath path)
    {
        await using var stream = await fs.OpenReadAsync(path, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    /// <summary>Copies what <paramref name="sources"/> name (the root's entries when none) into
    /// <paramref name="destination"/>, the way unpacking will.</summary>
    private async Task<OperationResult> CopyOutAsync(ArchiveProvider archive, VfsPath destination,
        params string[] sources)
    {
        var paths = sources.Length > 0
            ? sources.Select(Arc).ToList()
            : (await ListAsync(archive, Arc("/"))).Select(e => e.Path).ToList();
        var op = new CopyOperation(_providers, paths, destination, new CopyOptions(), new ScriptedInteraction());
        return await op.RunAsync().WaitAsync(Timeout, Ct);
    }

    // ---- Listing and reading ---------------------------------------------------------------------------

    [Theory]
    [InlineData("sample.zip", ArchiveFormat.Zip)]
    [InlineData("sample.tar", ArchiveFormat.Tar)]
    [InlineData("sample.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("sample.tar.bz2", ArchiveFormat.TarBz2)]
    [InlineData("sample.tar.xz", ArchiveFormat.TarXz)]
    [InlineData("sample.7z", ArchiveFormat.SevenZip)]
    [InlineData("solid.7z", ArchiveFormat.SevenZip)]
    public async Task Lists_folders_and_reads_files(string fixture, ArchiveFormat format)
    {
        var archive = await OpenAsync(fixture);

        Assert.Equal(format, archive.Format);
        Assert.Equal(Mem("/arc/" + fixture), archive.Host);
        Assert.False(archive.HasEncryptedEntries);
        var root = await ListAsync(archive, Arc("/"));
        Assert.Equal(["a.txt", "docs", "empty", "привет.txt"],
            root.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.All(root, e => Assert.Equal(e.Path, Arc("/" + e.Name)));
        Assert.Equal(EntryKind.Directory, root.Single(e => e.Name == "docs").Kind);
        var docs = await ListAsync(archive, Arc("/docs"));
        Assert.Equal(["readme.txt", "sub"], docs.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["data.bin"], (await ListAsync(archive, Arc("/docs/sub"))).Select(e => e.Name));

        Assert.Equal("a"u8.ToArray(), await ReadAsync(archive, Arc("/a.txt")));
        Assert.Equal("hello\n"u8.ToArray(), await ReadAsync(archive, Arc("/docs/readme.txt")));
        Assert.Equal(DataBin, await ReadAsync(archive, Arc("/docs/sub/data.bin")));
        Assert.Equal(Encoding.UTF8.GetBytes("привет\n"), await ReadAsync(archive, Arc("/привет.txt")));

        var readme = (await archive.GetEntryAsync(Arc("/docs/readme.txt"), Ct))!;
        Assert.Equal(EntryKind.File, readme.Kind);
        Assert.Equal(6, readme.Size);
        Assert.Null(readme.Mode);
        Assert.False(readme.IsHidden);
        Assert.Equal(readme.Modified, readme.Created);
        // Zip keeps zone-less wall-clock time (rounded up to two seconds), tar and 7z the instant.
        if (format == ArchiveFormat.Zip)
            Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 6), readme.Modified.LocalDateTime);
        else
            Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), readme.Modified);
        Assert.Equal(EntryKind.Directory, (await archive.GetEntryAsync(Arc("/"), Ct))!.Kind);
        Assert.Null(await archive.GetEntryAsync(Arc("/missing.txt"), Ct));
        Assert.Null(await archive.GetEntryAsync(Arc("/a.txt/below"), Ct));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => ListAsync(archive, Arc("/missing")));
        await Assert.ThrowsAsync<VfsException>(() => ListAsync(archive, Arc("/a.txt")));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => archive.OpenReadAsync(Arc("/missing.txt"), Ct));
        await Assert.ThrowsAsync<VfsException>(() => archive.OpenReadAsync(Arc("/docs"), Ct));
    }

    [Fact]
    public async Task Empty_folder_and_unicode_name_are_listed()
    {
        var archive = await OpenAsync("sample.zip");

        var empty = (await archive.GetEntryAsync(Arc("/empty"), Ct))!;
        Assert.Equal(EntryKind.Directory, empty.Kind);
        Assert.Empty(await ListAsync(archive, Arc("/empty")));
        var unicode = (await archive.GetEntryAsync(Arc("/привет.txt"), Ct))!;
        Assert.Equal(EntryKind.File, unicode.Kind);
        Assert.Equal(Encoding.UTF8.GetByteCount("привет\n"), unicode.Size);
    }

    [Fact]
    public async Task Cp866_names_without_utf8_flag_are_decoded()
    {
        var archive = await OpenAsync("cp866.zip");

        var entry = Assert.Single(await ListAsync(archive, Arc("/")));
        Assert.Equal("привет.txt", entry.Name);
        Assert.NotEmpty(await ReadAsync(archive, entry.Path));
    }

    [Fact]
    public async Task Utf8_names_without_the_utf8_flag_are_decoded_as_utf8()
    {
        var bytes = Zip(("привет.txt", "п"));
        ClearUtf8Flags(bytes);
        _host.Files.AddFile("/arc/noflag.zip", bytes, HostTime);
        var archive = await OpenHostAsync("/arc/noflag.zip");

        var entry = Assert.Single(await ListAsync(archive, Arc("/")));
        Assert.Equal("привет.txt", entry.Name);
        Assert.Equal(Encoding.UTF8.GetBytes("п"), await ReadAsync(archive, entry.Path));
    }

    /// <summary>Clears the general-purpose bit 11 ("names are UTF-8") in every local and central header, the way
    /// macOS's Archive Utility writes UTF-8 names.</summary>
    private static void ClearUtf8Flags(byte[] zip)
    {
        var cleared = 0;
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            var at = zip.AsSpan(i);
            int flags;
            if (at.StartsWith("PK\u0003\u0004"u8)) flags = i + 6;
            else if (at.StartsWith("PK\u0001\u0002"u8)) flags = i + 8;
            else continue;
            Assert.NotEqual(0, zip[flags + 1] & 0x08); // the writer had set it
            zip[flags + 1] &= 0xF7;
            cleared++;
        }
        Assert.Equal(2, cleared);
    }

    [Fact]
    public async Task Reads_from_many_threads_at_once()
    {
        var archive = await OpenAsync("sample.zip");
        var expected = new Dictionary<string, byte[]>
        {
            ["/a.txt"] = "a"u8.ToArray(), ["/docs/readme.txt"] = "hello\n"u8.ToArray(),
            ["/docs/sub/data.bin"] = DataBin,
        };

        var reads = Enumerable.Range(0, 30).Select(i => expected.ElementAt(i % expected.Count)).Select(pair =>
            Task.Run(async () => Assert.Equal(pair.Value, await ReadAsync(archive, Arc(pair.Key))), Ct));

        await Task.WhenAll(reads).WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task Hidden_names_start_with_a_dot()
    {
        _host.Files.AddFile("/arc/dot.zip", Zip(("dir/.hidden", "h"), ("dir/shown", "s")), HostTime);
        var archive = await OpenHostAsync("/arc/dot.zip");

        var entries = await ListAsync(archive, Arc("/dir"));
        Assert.True(entries.Single(e => e.Name == ".hidden").IsHidden);
        Assert.False(entries.Single(e => e.Name == "shown").IsHidden);
    }

    // ---- Dangerous names (RF2) --------------------------------------------------------------------------

    [Fact]
    public async Task Copying_slip_archive_out_writes_only_under_the_destination()
    {
        var archive = await OpenAsync("slip.zip");
        _host.Files.AddDirectory("/dst/out");
        var before = _host.Files.AllPaths.ToHashSet(StringComparer.Ordinal);

        var result = await CopyOutAsync(archive, Mem("/dst/out"));

        Assert.Equal(OperationState.Completed, result.State);
        var added = _host.Files.AllPaths.Where(p => !before.Contains(p)).ToList();
        Assert.All(added, p => Assert.StartsWith("/dst/out/", p, StringComparison.Ordinal));
        Assert.Equal(["/dst/out/a", "/dst/out/a/b.txt", "/dst/out/abs.txt", "/dst/out/dir", "/dst/out/dir/win.txt",
                      "/dst/out/evil.txt"], added.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Copying_trap_tar_out_never_writes_through_the_link()
    {
        var archive = await OpenAsync("trap.tar");
        _host.Files.AddDirectory("/dst/out");

        var link = Assert.Single(await ListAsync(archive, Arc("/")));
        Assert.Equal((EntryKind.Symlink, "/tmp"), (link.Kind, link.LinkTarget));
        Assert.Null(await archive.GetEntryAsync(Arc("/link/file.txt"), Ct));
        var result = await CopyOutAsync(archive, Mem("/dst/out"));

        Assert.Equal(OperationState.Completed, result.State);
        var copied = (await _host.GetEntryAsync(Mem("/dst/out/link"), Ct))!;
        Assert.Equal((EntryKind.Symlink, "/tmp"), (copied.Kind, copied.LinkTarget));
        Assert.DoesNotContain(_host.Files.AllPaths, p => p.Contains("file.txt", StringComparison.Ordinal));
    }

    // ---- One pass over sequential archives --------------------------------------------------------------

    [Theory]
    [InlineData("sample.tar.gz")]
    [InlineData("sample.tar.xz")]
    public async Task Sequential_archive_copy_reads_the_host_in_one_pass(string fixture)
    {
        var archive = await OpenAsync(fixture);
        _host.Files.AddDirectory("/dst");

        var result = await CopyOutAsync(archive, Mem("/dst"), "/a.txt", "/docs");

        Assert.Equal(OperationState.Completed, result.State);
        Assert.InRange(_host.ReadsOf("/arc/" + fixture), 1, 2);
        Assert.Equal("a", _host.Files.ReadAllText("/dst/a.txt"));
        Assert.Equal("hello\n", _host.Files.ReadAllText("/dst/docs/readme.txt"));
        Assert.Equal(DataBin, await ReadAsync(_host, Mem("/dst/docs/sub/data.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory)); // the batch released its files
    }

    [Fact]
    public async Task Solid_archive_copy_reads_the_host_in_one_pass()
    {
        var archive = await OpenAsync("solid.7z");
        _host.Files.AddDirectory("/dst");

        var result = await CopyOutAsync(archive, Mem("/dst"), "/a.txt", "/docs");

        Assert.Equal(OperationState.Completed, result.State);
        Assert.InRange(_host.ReadsOf("/arc/solid.7z"), 1, 2);
        Assert.Equal("hello\n", _host.Files.ReadAllText("/dst/docs/readme.txt"));
        Assert.Equal(DataBin, await ReadAsync(_host, Mem("/dst/docs/sub/data.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Directory));
    }

    [Fact]
    public async Task Unseekable_host_stream_is_copied_once_per_host_version()
    {
        _host.Unseekable = true;
        var archive = await OpenAsync("sample.zip");

        Assert.Equal("a"u8.ToArray(), await ReadAsync(archive, Arc("/a.txt")));
        Assert.Equal(DataBin, await ReadAsync(archive, Arc("/docs/sub/data.bin")));
        Assert.Equal(1, _host.ReadsOf("/arc/sample.zip"));

        _host.Files.AddFile("/arc/sample.zip", Fixture.Bytes("cp866.zip"), HostTime.AddMinutes(1));
        Assert.Equal("привет.txt", Assert.Single(await ListAsync(archive, Arc("/"))).Name);
        Assert.Equal(2, _host.ReadsOf("/arc/sample.zip"));
    }

    // ---- Host changes and damage ------------------------------------------------------------------------

    [Fact]
    public async Task Changed_host_reloads_the_listing_and_missing_host_is_not_found()
    {
        var archive = await OpenAsync("sample.zip");
        Assert.Equal(4, (await ListAsync(archive, Arc("/"))).Count);

        _host.Files.AddFile("/arc/sample.zip", Fixture.Bytes("cp866.zip"), HostTime.AddMinutes(1));

        Assert.Equal("привет.txt", Assert.Single(await ListAsync(archive, Arc("/"))).Name);
        Assert.Null(await archive.GetEntryAsync(Arc("/a.txt"), Ct));

        await _host.Files.DeleteAsync(Mem("/arc/sample.zip"), recursive: false, Ct);

        await Assert.ThrowsAsync<VfsNotFoundException>(() => ListAsync(archive, Arc("/")));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => archive.GetEntryAsync(Arc("/привет.txt"), Ct));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => archive.OpenReadAsync(Arc("/привет.txt"), Ct));
    }

    [Fact]
    public async Task Truncated_zip_is_reported_as_damaged() =>
        await Assert.ThrowsAsync<ArchiveDamagedException>(() => OpenAsync("broken.zip"));

    [Theory]
    [InlineData("/arc/a.txt")]
    [InlineData("/arc/text.zip")]
    [InlineData("/arc/text.tar.gz")]
    public async Task Plain_text_file_is_reported_as_damaged(string hostPath) =>
        await Assert.ThrowsAsync<ArchiveDamagedException>(() => OpenAsync("a.txt", hostPath: hostPath));

    [Fact]
    public async Task Missing_host_is_not_found() =>
        await Assert.ThrowsAsync<VfsNotFoundException>(() => OpenHostAsync("/arc/none.zip"));

    // ---- Passwords --------------------------------------------------------------------------------------

    [Fact]
    public async Task Encrypted_entry_without_or_with_a_wrong_password_asks_for_it()
    {
        var archive = await OpenAsync("encrypted.zip");
        Assert.True(archive.HasEncryptedEntries);

        var missing = await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() =>
            ReadAsync(archive, Arc("/a.txt")));
        Assert.Equal(archive.Host, missing.Archive);
        Assert.False(missing.WrongPassword);

        archive.Password = "wrong";
        var wrong = await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() =>
            ReadAsync(archive, Arc("/a.txt")));
        Assert.True(wrong.WrongPassword);

        archive.Password = "secret";
        Assert.Equal("hello\n"u8.ToArray(), await ReadAsync(archive, Arc("/docs/readme.txt")));
    }

    [Fact]
    public async Task Entry_stream_reports_its_close_even_when_closing_the_library_stream_throws()
    {
        var archive = await OpenAsync("sample.zip");
        bool? closedOk = null;
        var stream = new ArchiveProvider.EntryStream(archive, new ThrowingOnDisposeStream(), 0, null,
            ok => closedOk = ok);

        Assert.Throws<IOException>(stream.Dispose);
        Assert.False(closedOk);
    }

    private sealed class ThrowingOnDisposeStream() : MemoryStream([])
    {
        protected override void Dispose(bool disposing) => throw new IOException("The host went away.");
    }

    // ---- Read-only --------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_write_is_refused()
    {
        var archive = await OpenAsync("sample.zip");
        var file = Arc("/a.txt");

        Assert.Equal(ProviderCapabilities.None, archive.Capabilities);
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.OpenWriteAsync(Arc("/new.txt"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.CreateDirectoryAsync(Arc("/new"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.DeleteAsync(file, recursive: false, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.MoveAsync(file, Arc("/b.txt"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => archive.CreateSymlinkAsync(Arc("/l"), "/a", Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() =>
            archive.SetModifiedAsync(file, DateTimeOffset.UnixEpoch, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() =>
            archive.SetModeAsync(file, UnixFileMode.UserRead, Ct));
        Assert.Equal(["a.txt", "docs", "empty", "привет.txt"],
            (await ListAsync(archive, Arc("/"))).Select(e => e.Name).Order(StringComparer.Ordinal));

        await using (var batch = await archive.BeginBatchAsync(Ct)) await batch.CommitAsync(Ct);
        Assert.Null(archive.BatchHost); // the in-memory host takes no batches
        Assert.Null(archive.Watch(Arc("/"), () => { }));
        Assert.Equal("archive", await archive.GetVolumeKeyAsync(file, Ct));
        Assert.True(await archive.IsSameEntryAsync(file, Arc("/a.txt"), Ct));
        Assert.False(await archive.IsSameEntryAsync(file, Arc("/docs"), Ct));
        Assert.False(await archive.IsSameEntryAsync(Arc("/missing"), Arc("/missing"), Ct));
    }

    [Fact]
    public async Task Up_from_root_is_the_archive_file_and_display_path_joins_host_and_inner()
    {
        await OpenAsync("sample.zip");

        Assert.Equal(new HostLocation(Mem("/arc"), "sample.zip"), _providers.UpOf(Arc("/")));
        Assert.Equal(new HostLocation(Arc("/"), "docs"), _providers.UpOf(Arc("/docs")));
        Assert.Equal("/arc/sample.zip", _providers.DisplayPath(Arc("/")));
        Assert.Equal("/arc/sample.zip/docs/sub", _providers.DisplayPath(Arc("/docs/sub")));
        Assert.Equal(Mem("/arc"), _providers.PersistentLocationOf(Arc("/docs")));
    }

    [Fact]
    public async Task Twenty_thousand_entries_list_and_enumerate_a_folder()
    {
        var names = Enumerable.Range(0, 20_000).Select(i => ($"many/f{i:D5}.txt", i.ToString())).ToList();
        _host.Files.AddFile("/arc/big.zip", Zip([.. names, ("top.txt", "top")]), HostTime);
        var archive = await OpenHostAsync("/arc/big.zip");

        Assert.Equal(["many", "top.txt"], (await ListAsync(archive, Arc("/"))).Select(e => e.Name).Order());
        var many = await ListAsync(archive, Arc("/many"));
        Assert.Equal(20_000, many.Count);
        Assert.Equal("12345"u8.ToArray(), await ReadAsync(archive, Arc("/many/f12345.txt")));
        Assert.Equal(5, (await archive.GetEntryAsync(Arc("/many/f19999.txt"), Ct))!.Size);
    }

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return buffer.ToArray();
    }
}
