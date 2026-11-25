using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Core.Tests.Vfs;

[SupportedOSPlatform("macos")]
public sealed class LocalProviderTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly LocalFileSystemProvider _fs = new(new UnrestrictedAccessBroker());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Has_local_id_and_capabilities()
    {
        Assert.Equal("local", _fs.Id);
        Assert.Equal(ProviderCapabilities.Write | ProviderCapabilities.Trash | ProviderCapabilities.UnixPermissions |
                     ProviderCapabilities.Symlinks | ProviderCapabilities.Watch, _fs.Capabilities);
    }

    /// <summary>/a/dir → /b/c/d and /a/dir/link → ../x: the system reads /b/c/x (".." of the real folder), not /a/x.</summary>
    [Fact]
    public async Task Links_resolve_against_the_real_folder_not_the_path_text()
    {
        Directory.CreateDirectory(_temp.Combine("a"));
        Directory.CreateDirectory(_temp.Combine("b/c/d"));
        File.WriteAllText(_temp.Combine("a/x"), "wrong");
        File.WriteAllText(_temp.Combine("b/c/x"), "right");
        Directory.CreateSymbolicLink(_temp.Combine("a/dir"), _temp.Combine("b/c/d"));
        File.CreateSymbolicLink(_temp.Combine("b/c/d/link"), "../x");
        File.CreateSymbolicLink(_temp.Combine("b/c/d/chain"), "link");

        var resolved = await _fs.ResolveLinksAsync(VfsPath.Local(_temp.Combine("a/dir/chain")), Ct);

        Assert.Equal(VfsPath.Local(RealPath(_temp.Combine("b/c/x"))), resolved);
        Assert.Equal("right", await File.ReadAllTextAsync(resolved.Path, Ct));
        Assert.Equal(VfsPath.Local(RealPath(_temp.Combine("a/x"))),
            await _fs.ResolveLinksAsync(VfsPath.Local(_temp.Combine("a/x")), Ct));
    }

    [Fact]
    public async Task Resolving_a_dangling_link_gives_the_missing_target_and_a_loop_fails()
    {
        File.CreateSymbolicLink(_temp.Combine("dangling"), "missing.txt");
        File.CreateSymbolicLink(_temp.Combine("loop1"), "loop2");
        File.CreateSymbolicLink(_temp.Combine("loop2"), "loop1");

        var resolved = await _fs.ResolveLinksAsync(VfsPath.Local(_temp.Combine("dangling")), Ct);

        Assert.Equal(VfsPath.Local(RealPath(_temp.Path) + "/missing.txt"), resolved);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            _fs.ResolveLinksAsync(VfsPath.Local(_temp.Combine("loop1")), Ct));
    }

    /// <summary>The path with the temp folder's own links (/var → /private/var) resolved, as realpath(3) gives it
    /// for an existing parent.</summary>
    private static string RealPath(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var real = Marshal.PtrToStringUTF8(realpath(parent, IntPtr.Zero))!;
        return real + "/" + Path.GetFileName(path);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolved);

    [Fact]
    public async Task Unrestricted_broker_grants_everything()
    {
        var broker = new UnrestrictedAccessBroker();

        broker.Access(_temp.Path).Dispose();

        Assert.True(await broker.RequestAccessAsync(_temp.Path, Ct));
    }

    [Fact]
    public async Task IsSameEntry_sees_a_case_variant_path_as_the_same_entry()
    {
        Directory.CreateDirectory(_temp.Combine("dir"));
        File.WriteAllText(_temp.Combine("dir/a.txt"), "a");
        Assert.SkipUnless(File.Exists(_temp.Combine("DIR/A.TXT")), "The temp volume is case-sensitive.");

        Assert.True(await _fs.IsSameEntryAsync(_temp.Root.Combine("dir").Combine("a.txt"),
            _temp.Root.Combine("DIR").Combine("A.TXT"), Ct));
        Assert.True(await _fs.IsSameEntryAsync(_temp.Root.Combine("dir"), _temp.Root.Combine("Dir"), Ct));
    }

    [Fact]
    public async Task Dotfile_is_hidden()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "On Windows only the Hidden attribute hides a file.");
        File.WriteAllText(_temp.Combine(".secret"), "x");
        File.WriteAllText(_temp.Combine("plain"), "x");

        var entries = await ListAsync(_fs, _temp.Root);

        Assert.True(Assert.Single(entries, e => e.Name == ".secret").IsHidden);
        Assert.False(Assert.Single(entries, e => e.Name == "plain").IsHidden);
        Assert.True((await _fs.GetEntryAsync(_temp.Root.Combine(".secret"), Ct))!.IsHidden);
    }

    [Fact]
    public async Task Mode_roundtrips()
    {
        var path = _temp.Root.Combine("f.txt");
        File.WriteAllText(path.Path, "x");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead; // 0640

        await _fs.SetModeAsync(path, mode, Ct);

        Assert.Equal(mode, (await _fs.GetEntryAsync(path, Ct))!.Mode);
        Assert.Equal(mode, File.GetUnixFileMode(path.Path));
    }

    [Fact]
    public async Task Unreadable_directory_throws_AccessDenied()
    {
        var locked = _temp.Combine("locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            await Assert.ThrowsAsync<VfsAccessDeniedException>(() => ListAsync(_fs, VfsPath.Local(locked)));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Watch_fires_once_for_burst()
    {
        var calls = 0;
        using var watch = _fs.Watch(_temp.Root, () => Interlocked.Increment(ref calls));
        Assert.NotNull(watch);
        await Task.Delay(200, Ct); // let the watcher start

        for (var i = 0; i < 20; i++)
            File.WriteAllText(_temp.Combine($"f{i}.txt"), "x");
        await Task.Delay(1500, Ct);

        Assert.InRange(Volatile.Read(ref calls), 1, 3);
    }

    [Fact]
    public async Task Watch_stops_after_dispose()
    {
        var calls = 0;
        var watch = _fs.Watch(_temp.Root, () => Interlocked.Increment(ref calls));
        watch!.Dispose();

        File.WriteAllText(_temp.Combine("f.txt"), "x");
        await Task.Delay(800, Ct);

        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public void Watch_missing_directory_throws_NotFound()
    {
        Assert.Throws<VfsNotFoundException>(() => _fs.Watch(_temp.Root.Combine("missing"), () => { }));
    }

    [Fact]
    public async Task Every_call_goes_through_broker()
    {
        var broker = new CountingBroker();
        var fs = new LocalFileSystemProvider(broker);
        var file = _temp.Root.Combine("f.txt");
        File.WriteAllText(file.Path, "x");

        await ListAsync(fs, _temp.Root);
        Assert.Equal(1, broker.Count);
        Assert.Equal(_temp.Path, broker.LastPath);

        (await fs.OpenReadAsync(file, Ct)).Dispose();
        Assert.Equal(2, broker.Count);
        Assert.Equal(file.Path, broker.LastPath);

        await fs.DeleteAsync(file, recursive: false, Ct);
        Assert.Equal(3, broker.Count);
        Assert.False(File.Exists(file.Path));
        Assert.Equal(0, broker.Open);
    }

    [Fact]
    public async Task Move_asks_broker_for_both_paths()
    {
        var broker = new CountingBroker();
        var fs = new LocalFileSystemProvider(broker);
        File.WriteAllText(_temp.Combine("a.txt"), "x");

        await fs.MoveAsync(_temp.Root.Combine("a.txt"), _temp.Root.Combine("b.txt"), Ct);

        Assert.Equal([_temp.Combine("a.txt"), _temp.Combine("b.txt")], broker.Paths);
    }

    [Fact]
    public async Task Broker_denial_surfaces_as_AccessDenied()
    {
        var fs = new LocalFileSystemProvider(new DenyingBroker());
        var file = _temp.Root.Combine("f.txt");
        File.WriteAllText(file.Path, "x");

        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => ListAsync(fs, _temp.Root));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => fs.GetEntryAsync(file, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => fs.OpenReadAsync(file, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => fs.DeleteAsync(file, recursive: false, Ct));
        Assert.Throws<VfsAccessDeniedException>(() => fs.Watch(_temp.Root, () => { }));
        Assert.True(File.Exists(file.Path));
    }

    [Fact]
    public async Task Enumerate_lists_large_directory_completely()
    {
        for (var i = 0; i < 1500; i++)
            File.WriteAllText(_temp.Combine($"f{i:D4}"), "");

        var entries = await ListAsync(_fs, _temp.Root);

        Assert.Equal(1500, entries.Select(e => e.Name).Distinct().Count());
    }

    [Fact]
    public async Task Enumerate_reports_symlinks_and_follows_final_link()
    {
        Directory.CreateDirectory(_temp.Combine("dir"));
        File.WriteAllText(_temp.Combine("dir/inside.txt"), "x");
        File.CreateSymbolicLink(_temp.Combine("dlink"), "dir");
        File.CreateSymbolicLink(_temp.Combine("broken"), "nowhere");

        var entries = await ListAsync(_fs, _temp.Root);
        var dlink = Assert.Single(entries, e => e.Name == "dlink");
        var broken = Assert.Single(entries, e => e.Name == "broken");
        var through = await ListAsync(_fs, _temp.Root.Combine("dlink"));

        Assert.Equal(EntryKind.Symlink, dlink.Kind);
        Assert.True(dlink.LinkPointsToDirectory);
        Assert.Equal(EntryKind.Symlink, broken.Kind);
        Assert.Equal("nowhere", broken.LinkTarget);
        Assert.False(broken.LinkPointsToDirectory);
        Assert.Equal(_temp.Root.Combine("dlink").Combine("inside.txt"), Assert.Single(through).Path);
    }

    [Fact]
    public async Task A_link_whose_target_cannot_be_read_is_still_a_link_never_a_directory()
    {
        Directory.CreateDirectory(_temp.Combine("dir"));
        File.WriteAllText(_temp.Combine("dir/inside.txt"), "x");
        var link = _temp.Combine("unreadable");
        File.CreateSymbolicLink(link, "dir");
        Chmod("000", link); // on macOS readlink needs read permission on the link itself
        try
        {
            Assert.Null(new FileInfo(link).LinkTarget); // .NET reads no target for it

            var listed = Assert.Single(await ListAsync(_fs, _temp.Root), e => e.Name == "unreadable");
            var single = await _fs.GetEntryAsync(_temp.Root.Combine("unreadable"), Ct);

            foreach (var entry in new[] { listed, single! })
            {
                Assert.Equal(EntryKind.Symlink, entry.Kind);
                Assert.Null(entry.LinkTarget);
                Assert.True(entry.LinkPointsToDirectory);
            }

            // Deleting it removes the link, never what it points to.
            await _fs.DeleteAsync(_temp.Root.Combine("unreadable"), recursive: false, Ct);
            Assert.False(Path.Exists(link) || new FileInfo(link).Exists);
            Assert.True(File.Exists(_temp.Combine("dir/inside.txt")));
        }
        finally
        {
            var attributes = new FileInfo(link).Attributes; // -1 once the link is gone
            if ((int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint)) Chmod("755", link);
        }
    }

    private static void Chmod(string mode, string path)
    {
        using var process = System.Diagnostics.Process.Start("/bin/chmod", ["-h", mode, path]);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Move_onto_broken_symlink_throws_AlreadyExists()
    {
        File.WriteAllText(_temp.Combine("a.txt"), "x");
        File.CreateSymbolicLink(_temp.Combine("broken"), "nowhere");

        await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => _fs.MoveAsync(_temp.Root.Combine("a.txt"), _temp.Root.Combine("broken"), Ct));
        Assert.True(File.Exists(_temp.Combine("a.txt")));
    }

    [Fact]
    public async Task Move_can_change_only_letter_case()
    {
        File.WriteAllText(_temp.Combine("name.txt"), "x");

        await _fs.MoveAsync(_temp.Root.Combine("name.txt"), _temp.Root.Combine("Name.txt"), Ct);

        Assert.Equal(["Name.txt"], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Move_goes_through_exclusive_rename_when_given()
    {
        var rename = new RecordingRename();
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), rename);
        File.WriteAllText(_temp.Combine("a.txt"), "x");

        await fs.MoveAsync(_temp.Root.Combine("a.txt"), _temp.Root.Combine("b.txt"), Ct);

        Assert.Equal([(_temp.Combine("a.txt"), _temp.Combine("b.txt"))], rename.Exclusive);
        Assert.Empty(rename.SameEntry);
        Assert.Equal(["b.txt"], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Case_only_rename_does_not_use_exclusive_rename_and_succeeds()
    {
        var rename = new RecordingRename();
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), rename);
        File.WriteAllText(_temp.Combine("name.txt"), "x");
        Assert.SkipUnless(File.Exists(_temp.Combine("NAME.TXT")), "The temp volume is case-sensitive.");

        await fs.MoveAsync(_temp.Root.Combine("name.txt"), _temp.Root.Combine("Name.txt"), Ct);

        Assert.Empty(rename.Exclusive);
        Assert.Equal([(_temp.Combine("name.txt"), _temp.Combine("Name.txt"))], rename.SameEntry);
        Assert.Equal(["Name.txt"], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Normalization_only_rename_succeeds()
    {
        var composed = "\u0439.txt".Normalize(NormalizationForm.FormC);   // "й" as one code point
        var decomposed = "\u0439.txt".Normalize(NormalizationForm.FormD); // "и" + combining breve
        Assert.NotEqual(composed, decomposed);
        var rename = new RecordingRename();
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), rename);
        File.WriteAllText(_temp.Combine(composed), "x");
        Assert.SkipUnless(File.Exists(_temp.Combine(decomposed)), "The temp volume is normalization-sensitive.");

        await fs.MoveAsync(_temp.Root.Combine(composed), _temp.Root.Combine(decomposed), Ct);

        Assert.Empty(rename.Exclusive);
        Assert.Equal([decomposed], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
        Assert.Equal("x", File.ReadAllText(_temp.Combine(decomposed)));
    }

    [Fact]
    public async Task Move_onto_a_distinct_entry_with_an_equivalent_name_still_uses_exclusive_rename()
    {
        var rename = new RecordingRename();
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), rename);
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        File.WriteAllText(_temp.Combine("A.txt"), "b");
        Assert.SkipWhen(Directory.GetFileSystemEntries(_temp.Path).Length == 1, "The temp volume is case-insensitive.");

        await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => fs.MoveAsync(_temp.Root.Combine("a.txt"), _temp.Root.Combine("A.txt"), Ct));

        Assert.Single(rename.Exclusive);
        Assert.Equal("a", File.ReadAllText(_temp.Combine("a.txt")));
        Assert.Equal("b", File.ReadAllText(_temp.Combine("A.txt")));
    }

    [Fact]
    public async Task Move_falls_back_to_check_and_rename_when_the_volume_has_no_exclusive_rename()
    {
        var rename = new RecordingRename { Unsupported = true };
        var fs = new LocalFileSystemProvider(new UnrestrictedAccessBroker(), rename);
        File.WriteAllText(_temp.Combine("a.txt"), "a");
        File.WriteAllText(_temp.Combine("b.txt"), "b");

        await fs.MoveAsync(_temp.Root.Combine("a.txt"), _temp.Root.Combine("c.txt"), Ct);
        await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => fs.MoveAsync(_temp.Root.Combine("c.txt"), _temp.Root.Combine("b.txt"), Ct));

        Assert.Equal(2, rename.Exclusive.Count);
        Assert.Equal("a", File.ReadAllText(_temp.Combine("c.txt")));
        Assert.Equal("b", File.ReadAllText(_temp.Combine("b.txt")));
        Assert.False(File.Exists(_temp.Combine("a.txt")));
    }

    [Fact]
    public async Task Delete_missing_throws_NotFound()
    {
        await Assert.ThrowsAsync<VfsNotFoundException>(
            () => _fs.DeleteAsync(_temp.Root.Combine("missing"), recursive: false, Ct));
    }

    [Fact]
    public async Task Delete_symlink_to_directory_keeps_target()
    {
        Directory.CreateDirectory(_temp.Combine("dir"));
        File.WriteAllText(_temp.Combine("dir/inside.txt"), "x");
        File.CreateSymbolicLink(_temp.Combine("dlink"), "dir");

        await _fs.DeleteAsync(_temp.Root.Combine("dlink"), recursive: true, Ct);

        Assert.Null(await _fs.GetEntryAsync(_temp.Root.Combine("dlink"), Ct));
        Assert.True(File.Exists(_temp.Combine("dir/inside.txt")));
    }

    [Fact]
    public async Task OpenRead_on_directory_is_not_access_denied()
    {
        Directory.CreateDirectory(_temp.Combine("dir"));

        var error = await Assert.ThrowsAnyAsync<IOException>(() => _fs.OpenReadAsync(_temp.Root.Combine("dir"), Ct));

        Assert.IsNotType<VfsAccessDeniedException>(error);
    }

    [Fact]
    public async Task Rejects_paths_of_another_provider()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _fs.GetEntryAsync(new VfsPath("mem", "/"), Ct));
    }

    private static async Task<List<FileEntry>> ListAsync(IFileSystemProvider fs, VfsPath directory)
    {
        var list = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(directory, Ct))
            list.Add(entry);
        return list;
    }

    /// <summary>Records the calls; the exclusive rename checks and then moves (or refuses as unsupported), the
    /// same-entry rename is a plain <c>rename(2)</c>.</summary>
    private sealed class RecordingRename : IExclusiveRename
    {
        public bool Unsupported { get; init; }

        public List<(string, string)> Exclusive { get; } = [];

        public List<(string, string)> SameEntry { get; } = [];

        public void Rename(string sourcePath, string destinationPath)
        {
            Exclusive.Add((sourcePath, destinationPath));
            if (Unsupported) throw new NotSupportedException("No exclusive rename on this volume.");
            if ((int)new FileInfo(destinationPath).Attributes != -1)
                throw new VfsAlreadyExistsException($"'{destinationPath}' already exists.");
            Directory.Move(sourcePath, destinationPath);
        }

        public void RenameSameEntry(string sourcePath, string destinationPath)
        {
            SameEntry.Add((sourcePath, destinationPath));
            if (rename(sourcePath, destinationPath) != 0)
                throw new IOException($"rename failed (errno {Marshal.GetLastPInvokeError()}).");
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string from,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
    }

    private sealed class CountingBroker : IFileAccessBroker
    {
        private readonly Lock _lock = new();
        private readonly List<string> _paths = [];
        private int _open;

        public int Count { get { lock (_lock) return _paths.Count; } }
        public string? LastPath { get { lock (_lock) return _paths.LastOrDefault(); } }
        public List<string> Paths { get { lock (_lock) return [.. _paths]; } }
        public int Open => Volatile.Read(ref _open);

        public IDisposable Access(string path)
        {
            lock (_lock) _paths.Add(path);
            Interlocked.Increment(ref _open);
            return new Scope(this);
        }

        public Task<bool> RequestAccessAsync(string path, CancellationToken ct) => Task.FromResult(true);

        private sealed class Scope(CountingBroker owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._open);
            }
        }
    }

    private sealed class DenyingBroker : IFileAccessBroker
    {
        public IDisposable Access(string path) => throw new VfsAccessDeniedException($"No access to '{path}'.");

        public Task<bool> RequestAccessAsync(string path, CancellationToken ct) => Task.FromResult(false);
    }
}
