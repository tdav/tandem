using System.Runtime.Versioning;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Vfs.Local;

namespace Tandem.Core.Tests.Operations;

[SupportedOSPlatform("macos")]
public sealed class CopyOperationTests
{
    private static readonly DateTimeOffset OldTime = new(2020, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private readonly InMemoryFileSystem _mem = new();
    private readonly ProviderRegistry _providers = new();
    private readonly ScriptedInteraction _interaction = new();

    public CopyOperationTests() => _providers.Register(_mem);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private CopyOperation Copy(IReadOnlyList<VfsPath> sources, VfsPath destination, CopyOptions? options = null,
        INativeCopy? nativeCopy = null) =>
        new(_providers, sources, destination, options ?? new CopyOptions(), _interaction, nativeCopy);

    private CopyOperation Copy(string source, string destination, CopyOptions? options = null) =>
        Copy([Mem(source)], Mem(destination), options);

    [Fact]
    public void Copy_operation_accepts_Unpack_kind_and_rejects_others()
    {
        Assert.Equal(OperationKind.Copy, Copy("/src", "/dst").Kind);
        Assert.Equal(OperationKind.Unpack,
            new CopyOperation(_providers, [Mem("/src")], Mem("/dst"), new CopyOptions(), _interaction,
                kind: OperationKind.Unpack).Kind);
        foreach (var kind in new[] { OperationKind.Move, OperationKind.Delete, OperationKind.Pack, (OperationKind)99 })
        {
            var error = Assert.Throws<ArgumentException>(() =>
                new CopyOperation(_providers, [Mem("/src")], Mem("/dst"), new CopyOptions(), _interaction, kind: kind));
            Assert.Equal("kind", error.ParamName);
        }
    }

    [Fact]
    public async Task Copies_file_with_content_and_mtime()
    {
        _mem.AddFile("/src/a.txt", "hello", OldTime);
        _mem.AddDirectory("/dst");

        var result = await Copy("/src/a.txt", "/dst").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Empty(result.Skipped);
        Assert.Equal("hello", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal(OldTime, (await _mem.GetEntryAsync(Mem("/dst/a.txt"), Ct))!.Modified);
        Assert.Equal("hello", _mem.ReadAllText("/src/a.txt"));
    }

    [Fact]
    public async Task Copies_mode_when_both_providers_support_it()
    {
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        _mem.AddFile("/src/run.sh", "x");
        await _mem.SetModeAsync(Mem("/src/run.sh"), mode, Ct);
        _mem.AddDirectory("/dst");

        await Copy("/src/run.sh", "/dst").RunAsync();

        Assert.Equal(mode, (await _mem.GetEntryAsync(Mem("/dst/run.sh"), Ct))!.Mode);
    }

    [Fact]
    public async Task Copies_tree_recursively()
    {
        _mem.AddFile("/src/a/b/c.txt", "deep");
        _mem.AddFile("/src/a/top.txt", "top");
        await _mem.SetModifiedAsync(Mem("/src/a/b"), OldTime, Ct);
        _mem.AddDirectory("/dst");

        var result = await Copy("/src/a", "/dst").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("deep", _mem.ReadAllText("/dst/a/b/c.txt"));
        Assert.Equal("top", _mem.ReadAllText("/dst/a/top.txt"));
        Assert.Equal(OldTime, (await _mem.GetEntryAsync(Mem("/dst/a/b"), Ct))!.Modified);
    }

    [Fact]
    public async Task Copies_between_providers()
    {
        var mem1 = new InMemoryFileSystem("mem1");
        var mem2 = new InMemoryFileSystem("mem2");
        _providers.Register(mem1);
        _providers.Register(mem2);
        mem1.AddFile("/src/a.txt", "across", OldTime);
        mem2.AddDirectory("/dst");

        var result = await Copy([new VfsPath("mem1", "/src/a.txt")], new VfsPath("mem2", "/dst")).RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("across", mem2.ReadAllText("/dst/a.txt"));
        Assert.Equal(OldTime, (await mem2.GetEntryAsync(new VfsPath("mem2", "/dst/a.txt"), Ct))!.Modified);
    }

    [Fact]
    public async Task NewName_renames_single_source()
    {
        _mem.AddFile("/src/a.txt", "x");
        _mem.AddDirectory("/dst");

        await Copy("/src/a.txt", "/dst", new CopyOptions(NewName: "b.txt")).RunAsync();

        Assert.Equal("x", _mem.ReadAllText("/dst/b.txt"));
        Assert.False(_mem.Exists("/dst/a.txt"));
    }

    [Fact]
    public async Task Symlink_copied_as_link()
    {
        _mem.AddFile("/src/target.txt", "t");
        await _mem.CreateSymlinkAsync(Mem("/src/link"), "target.txt", Ct);
        _mem.AddDirectory("/dst");

        var result = await Copy("/src/link", "/dst").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        var copied = (await _mem.GetEntryAsync(Mem("/dst/link"), Ct))!;
        Assert.Equal(EntryKind.Symlink, copied.Kind);
        Assert.Equal("target.txt", copied.LinkTarget);
    }

    [Fact]
    public async Task Symlink_to_directory_is_not_followed()
    {
        _mem.AddFile("/src/dir/inner.txt", "i");
        _mem.AddDirectory("/src/tree");
        await _mem.CreateSymlinkAsync(Mem("/src/tree/dirlink"), "/src/dir", Ct);
        _mem.AddDirectory("/dst");

        await Copy("/src/tree", "/dst").RunAsync();

        Assert.Equal(EntryKind.Symlink, (await _mem.GetEntryAsync(Mem("/dst/tree/dirlink"), Ct))!.Kind);
        Assert.DoesNotContain(_mem.AllPaths, p => p.StartsWith("/dst/tree/dirlink/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Symlink_is_skipped_with_reason_when_destination_has_no_links()
    {
        var plain = new NoSymlinkFileSystem(new InMemoryFileSystem("plain"));
        _providers.Register(plain);
        await _mem.CreateSymlinkAsync(Mem("/link"), "anything", Ct);
        _mem.AddFile("/file.txt", "f");
        await plain.CreateDirectoryAsync(new VfsPath("plain", "/dst"), Ct);

        var result = await Copy([Mem("/link"), Mem("/file.txt")], new VfsPath("plain", "/dst")).RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(Mem("/link"), skipped.Path);
        Assert.Equal(SkipReason.SymlinksNotSupported, skipped.Reason);
        Assert.Null(skipped.Detail);
        Assert.Null(await plain.GetEntryAsync(new VfsPath("plain", "/dst/link"), Ct));
        Assert.NotNull(await plain.GetEntryAsync(new VfsPath("plain", "/dst/file.txt"), Ct));
    }

    [Fact]
    public async Task Progress_totals_match()
    {
        _mem.AddFile("/src/one.txt", "0123456789");
        _mem.AddFile("/src/two.txt", "0123456789");
        _mem.AddFile("/src/sub/three.txt", "0123456789");
        _mem.AddDirectory("/dst");
        var op = Copy("/src", "/dst");

        await op.RunAsync();

        var progress = op.Progress;
        Assert.Equal(3, progress.FilesTotal);
        Assert.Equal(30, progress.BytesTotal);
        Assert.Equal(progress.FilesTotal, progress.FilesDone);
        Assert.Equal(progress.BytesTotal, progress.BytesDone);
    }

    [Fact]
    public async Task State_changes_are_announced_in_order()
    {
        _mem.AddFile("/src/a.txt", "x");
        _mem.AddDirectory("/dst");
        var op = Copy("/src/a.txt", "/dst");
        var states = new List<OperationState>();
        op.Changed += (_, _) =>
        {
            lock (states)
                if (states.Count == 0 || states[^1] != op.State) states.Add(op.State);
        };

        Assert.Equal(OperationState.Queued, op.State);
        await op.RunAsync();

        lock (states)
            Assert.Equal([OperationState.Scanning, OperationState.Running, OperationState.Completed], states);
        Assert.Equal(OperationState.Completed, op.State);
    }

    [Fact]
    public async Task No_part_files_left_after_success()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/src/d/b.txt", "b");
        _mem.AddDirectory("/dst");

        await Copy([Mem("/src/a.txt"), Mem("/src/d")], Mem("/dst")).RunAsync();

        Assert.Contains("/dst/a.txt", _mem.AllPaths);
        Assert.Contains("/dst/d/b.txt", _mem.AllPaths);
        Assert.DoesNotContain(_mem.AllPaths, p => p.EndsWith(".tandem-part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task File_is_written_under_part_name_first()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        var opened = new List<string>();
        _mem.FailOn((operation, path) =>
        {
            if (operation == "OpenWrite") lock (opened) opened.Add(path.Path);
            return false;
        }, () => new IOException("unused"));

        await Copy("/src/a.txt", "/dst").RunAsync();

        lock (opened) Assert.Equal(["/dst/.a.txt.tandem-part"], opened);
    }

    [Fact]
    public async Task Rejects_copy_into_itself()
    {
        _mem.AddFile("/a/x.txt", "x");
        _mem.AddDirectory("/a/b");
        var op = Copy("/a", "/a/b");

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.DestinationInsideSource, error.Reason);
        Assert.Equal(Mem("/a"), error.SourcePath);
        Assert.Equal(Mem("/a/b"), error.DestinationPath);

        Assert.Equal(OperationState.Failed, op.State);
        Assert.DoesNotContain(_mem.AllPaths, p => p.StartsWith("/a/b/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejects_copy_onto_itself()
    {
        _mem.AddFile("/a/x.txt", "x");

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => Copy("/a/x.txt", "/a").RunAsync());

        Assert.Equal(RejectionReason.SourceEqualsDestination, error.Reason);
        Assert.Equal(Mem("/a/x.txt"), error.SourcePath);
        Assert.Equal(Mem("/a/x.txt"), error.DestinationPath);

        Assert.Equal("x", _mem.ReadAllText("/a/x.txt"));
        Assert.Equal(["/a", "/a/x.txt"], _mem.AllPaths);
    }

    [Fact]
    public async Task Rejects_NewName_with_several_sources()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/src/b.txt", "b");
        _mem.AddDirectory("/dst");

        var op = Copy([Mem("/src/a.txt"), Mem("/src/b.txt")], Mem("/dst"), new CopyOptions(NewName: "c.txt"));

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.NewNameWithMultipleSources, error.Reason);
        Assert.Equal("c.txt", error.Name);
        Assert.DoesNotContain(_mem.AllPaths, p => p.StartsWith("/dst/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Rejects_invalid_NewName(string name)
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");

        var op = Copy("/src/a.txt", "/dst", new CopyOptions(NewName: name));

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.InvalidName, error.Reason);
        Assert.Equal(Mem("/src/a.txt"), error.SourcePath);
        Assert.Equal(name, error.Name);
        Assert.DoesNotContain(_mem.AllPaths, p => p.StartsWith("/dst/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_destination_directory_fails()
    {
        _mem.AddFile("/src/a.txt", "a");

        await Assert.ThrowsAsync<VfsNotFoundException>(() => Copy("/src/a.txt", "/nowhere").RunAsync());

        Assert.False(_mem.Exists("/nowhere"));
    }

    [Fact]
    public async Task Symlinked_destination_directory_is_accepted()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/real");
        await _mem.CreateSymlinkAsync(Mem("/dstlink"), "real", Ct);

        var result = await Copy("/src/a.txt", "/dstlink").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("a", _mem.ReadAllText("/real/a.txt"));
        Assert.Equal(EntryKind.Symlink, (await _mem.GetEntryAsync(Mem("/dstlink"), Ct))!.Kind);
    }

    [Fact]
    public async Task Symlink_to_a_file_is_not_a_destination_directory()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddFile("/other.txt", "o");
        await _mem.CreateSymlinkAsync(Mem("/filelink"), "other.txt", Ct);

        await Assert.ThrowsAsync<VfsNotFoundException>(() => Copy("/src/a.txt", "/filelink").RunAsync());
    }

    [Fact]
    public async Task Rejects_copy_into_itself_through_a_symlinked_destination()
    {
        _mem.AddFile("/a/x.txt", "x");
        _mem.AddDirectory("/a/b");
        await _mem.CreateSymlinkAsync(Mem("/link"), "/a/b", Ct);

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => Copy("/a", "/link").RunAsync());

        Assert.Equal(RejectionReason.DestinationInsideSource, error.Reason);

        Assert.DoesNotContain(_mem.AllPaths, p => p.StartsWith("/a/b/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copies_into_symlinked_local_directory()
    {
        using var temp = new TempDir();
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        File.WriteAllText(temp.Combine("a.txt"), "local");
        Directory.CreateDirectory(temp.Combine("real"));
        File.CreateSymbolicLink(temp.Combine("link"), temp.Combine("real"));

        var result = await Copy([VfsPath.Local(temp.Combine("a.txt"))], VfsPath.Local(temp.Combine("link"))).RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("local", File.ReadAllText(temp.Combine("real/a.txt")));
        Assert.Equal(["a.txt"], Directory.GetFileSystemEntries(temp.Combine("real")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Uses_native_copy_for_local_to_local()
    {
        using var temp = new TempDir();
        _providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        var source = temp.Combine("a.bin");
        File.WriteAllText(source, "native content");
        File.SetLastWriteTimeUtc(source, OldTime.UtcDateTime);
        Directory.CreateDirectory(temp.Combine("dst"));
        var native = new FakeNativeCopy();

        var op = Copy([VfsPath.Local(source)], VfsPath.Local(temp.Combine("dst")), nativeCopy: native);
        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        var call = Assert.Single(native.Calls);
        Assert.Equal(source, call.Source);
        Assert.Equal(temp.Combine("dst/.a.bin.tandem-part"), call.Destination);
        Assert.Equal("native content", File.ReadAllText(temp.Combine("dst/a.bin")));
        Assert.Equal(OldTime.UtcDateTime, File.GetLastWriteTimeUtc(temp.Combine("dst/a.bin")));
        Assert.Equal(["a.bin"], Directory.GetFileSystemEntries(temp.Combine("dst")).Select(Path.GetFileName));
        Assert.Equal(op.Progress.BytesTotal, op.Progress.BytesDone);
    }

    [Fact]
    public async Task Native_copy_is_not_used_when_a_side_is_not_local()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        var native = new FakeNativeCopy();

        await Copy([Mem("/src/a.txt")], Mem("/dst"), nativeCopy: native).RunAsync();

        Assert.Empty(native.Calls);
        Assert.Equal("a", _mem.ReadAllText("/dst/a.txt"));
    }

    [Fact]
    public async Task Empty_directory_is_copied()
    {
        _mem.AddDirectory("/src/empty");
        _mem.AddDirectory("/dst");

        var result = await Copy("/src/empty", "/dst").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(EntryKind.Directory, (await _mem.GetEntryAsync(Mem("/dst/empty"), Ct))!.Kind);
    }

    [Fact]
    public async Task Existing_directory_is_merged()
    {
        _mem.AddFile("/src/d/new.txt", "n");
        _mem.AddFile("/dst/d/old.txt", "o");

        var result = await Copy("/src/d", "/dst").RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("n", _mem.ReadAllText("/dst/d/new.txt"));
        Assert.Equal("o", _mem.ReadAllText("/dst/d/old.txt"));
    }

    [Fact]
    public async Task RunAsync_twice_throws()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        var op = Copy("/src/a.txt", "/dst");

        var first = op.RunAsync();

        Assert.Throws<InvalidOperationException>(() => { _ = op.RunAsync(); }); // synchronous, by contract
        Assert.Same(op.Completion, first);
        await first;
    }

    [Fact]
    public async Task Cancel_before_run_copies_nothing()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        var op = Copy("/src/a.txt", "/dst");

        op.Cancel();
        var result = await op.RunAsync();

        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(OperationState.Cancelled, op.State);
        Assert.False(_mem.Exists("/dst/a.txt"));
    }

    [Fact]
    public async Task RunAsync_returns_before_the_work_is_done()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        using var release = new ManualResetEventSlim();
        // Every provider call blocks until released: run synchronously, RunAsync would only return after the
        // wait timed out and the copy had finished.
        _mem.FailOn((_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return false;
        }, () => new IOException("unused"));

        var run = Copy("/src/a.txt", "/dst").RunAsync();

        Assert.False(run.IsCompleted);
        release.Set();
        Assert.Equal(OperationState.Completed, (await run).State);
    }

    [Fact]
    public void Describes_itself_for_the_title()
    {
        var single = Copy("/src/a.txt", "/dst");
        var several = Copy([Mem("/src/a.txt"), Mem("/src/b.txt"), Mem("/src/c")], Mem("/dst"));

        Assert.Equal(OperationKind.Copy, single.Kind);
        Assert.Equal(1, single.ItemCount);
        Assert.Equal(Mem("/dst"), single.Destination);
        Assert.Equal(OperationKind.Copy, several.Kind);
        Assert.Equal(3, several.ItemCount);
        Assert.Equal(Mem("/dst"), several.Destination);
    }

    [Fact]
    public void Sources_are_the_selected_items_and_do_not_follow_the_callers_list()
    {
        var list = new List<VfsPath> { Mem("/src/a.txt"), Mem("/src/c") };
        var op = Copy(list, Mem("/dst"));

        list.Add(Mem("/src/later.txt"));

        Assert.Equal([Mem("/src/a.txt"), Mem("/src/c")], op.Sources);
        Assert.False(op.Sources is ICollection<VfsPath> { IsReadOnly: false });
    }

    [Fact]
    public async Task Volume_keys_cover_sources_and_destination()
    {
        _mem.AddFile("/src/a.txt", "a");
        _mem.AddDirectory("/dst");
        _mem.SetVolumeKey("/src", "v1");
        _mem.SetVolumeKey("/dst", "v2");

        var keys = await Copy("/src/a.txt", "/dst").GetVolumeKeysAsync(Ct);

        Assert.Equal(["mem:v1", "mem:v2"], keys.Order(StringComparer.Ordinal));
    }

    private sealed class FakeNativeCopy : INativeCopy
    {
        private readonly Lock _lock = new();
        private readonly List<(string Source, string Destination)> _calls = [];

        public IReadOnlyList<(string Source, string Destination)> Calls
        {
            get { lock (_lock) return _calls.ToList(); }
        }

        public Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
            Func<CancellationToken, ValueTask> pauseCheckpoint, CancellationToken ct)
        {
            lock (_lock) _calls.Add((sourcePath, destinationPath));
            File.Copy(sourcePath, destinationPath);
            bytesCopied.Report(new FileInfo(destinationPath).Length);
            return Task.CompletedTask;
        }

            public Task CopyFlagsAsync(string sourcePath, string destinationPath, CancellationToken ct) => Task.CompletedTask;

        public Task CopyFileMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
            Task.CompletedTask;

        public Task CopyDirectoryMetadataAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Copying_a_file_into_a_case_variant_of_its_own_folder_is_rejected()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("a"));
        File.WriteAllText(temp.Combine("a/x.txt"), "x");
        Assert.SkipUnless(Directory.Exists(temp.Combine("A")), "The temp volume is case-sensitive.");
        _interaction.OnConflict(ConflictAction.Overwrite, applyToAll: true);
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        var op = new CopyOperation(providers, [VfsPath.Local(temp.Combine("a/x.txt"))], VfsPath.Local(temp.Combine("A")),
            new CopyOptions(), _interaction);

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.SourceEqualsDestination, error.Reason);
        Assert.Empty(_interaction.Conflicts);
        Assert.Equal(["a", "a/x.txt"], Directory.EnumerateFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(temp.Path, p)).Order(StringComparer.Ordinal));
        Assert.Equal("x", File.ReadAllText(temp.Combine("a/x.txt")));
    }

    [Fact]
    public async Task A_link_whose_target_cannot_be_read_is_an_item_error()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("src/dir"));
        Directory.CreateDirectory(temp.Combine("dst"));
        var link = temp.Combine("src/link");
        File.CreateSymbolicLink(link, "dir");
        using (var chmod = System.Diagnostics.Process.Start("/bin/chmod", ["-h", "000", link])) chmod.WaitForExit();
        _interaction.OnError(ErrorAction.Skip);
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        var op = new CopyOperation(providers, [VfsPath.Local(link)], VfsPath.Local(temp.Combine("dst")),
            new CopyOptions(), _interaction);

        var result = await op.RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.IsType<VfsAccessDeniedException>(Assert.Single(_interaction.Errors).Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Combine("dst")));
    }

    [Fact]
    public async Task Copying_a_file_onto_a_case_variant_of_its_own_name_is_rejected()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("x.txt"), "x");
        Assert.SkipUnless(File.Exists(temp.Combine("X.TXT")), "The temp volume is case-sensitive.");
        _interaction.OnConflict(ConflictAction.Overwrite, applyToAll: true);
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(new UnrestrictedAccessBroker()));
        var op = new CopyOperation(providers, [temp.Root.Combine("x.txt")], temp.Root, new CopyOptions(NewName: "X.txt"),
            _interaction);

        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => op.RunAsync());

        Assert.Equal(RejectionReason.SourceEqualsDestination, error.Reason);
        Assert.Empty(_interaction.Conflicts);
        Assert.Equal(["x.txt"], Directory.EnumerateFileSystemEntries(temp.Path).Select(Path.GetFileName));
        Assert.Equal("x", File.ReadAllText(temp.Combine("x.txt")));
    }

    /// <summary>A provider that does not support symlinks (otherwise delegates to an in-memory one).</summary>
    private sealed class NoSymlinkFileSystem(InMemoryFileSystem inner) : IFileSystemProvider
    {
        public string Id => inner.Id;
        public ProviderCapabilities Capabilities => inner.Capabilities & ~ProviderCapabilities.Symlinks;
        public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
            inner.EnumerateAsync(directory, ct);
        public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => inner.GetEntryAsync(path, ct);
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
            inner.DeleteAsync(path, recursive, ct);
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
            inner.MoveAsync(source, destination, ct);
        public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
            inner.SetModifiedAsync(path, modified, ct);
        public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) =>
            inner.SetModeAsync(path, mode, ct);
        public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => inner.GetVolumeKeyAsync(path, ct);
        public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) => inner.IsSameEntryAsync(a, b, ct);
        public IDisposable? Watch(VfsPath directory, Action changed) => inner.Watch(directory, changed);
    }
}
