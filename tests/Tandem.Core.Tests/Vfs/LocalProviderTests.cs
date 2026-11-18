using System.Runtime.Versioning;
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

    [Fact]
    public async Task Unrestricted_broker_grants_everything()
    {
        var broker = new UnrestrictedAccessBroker();

        broker.Access(_temp.Path).Dispose();

        Assert.True(await broker.RequestAccessAsync(_temp.Path, Ct));
    }

    [Fact]
    public async Task Dotfile_is_hidden()
    {
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
