using System.Runtime.Versioning;
using System.Text;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Vfs;

[SupportedOSPlatform("macos")]
public sealed class TempFileStoreTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly InMemoryFileSystem _mem = new();

    private string StoreDirectory => _temp.Combine("tmp");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Materialize_copies_bytes_and_keeps_the_name()
    {
        var content = new byte[300_000];
        new Random(1).NextBytes(content);
        _mem.AddFile("/a/report.pdf", content);
        using var store = new TempFileStore(StoreDirectory);
        var reported = new List<long>();

        var path = await store.MaterializeAsync(_mem, new VfsPath("mem", "/a/report.pdf"),
            new SyncProgress(reported.Add), Ct);

        Assert.Equal("report.pdf", Path.GetFileName(path));
        Assert.StartsWith(store.Directory + "/", path, StringComparison.Ordinal);
        Assert.Equal(content, await File.ReadAllBytesAsync(path, Ct));
        Assert.NotEmpty(reported);
        Assert.Equal(content.Length, reported[^1]);
        Assert.Equal(reported.Order(), reported);
    }

    [Fact]
    public async Task Same_name_twice_gives_two_files()
    {
        _mem.AddFile("/a/x.txt", "one");
        _mem.AddFile("/b/x.txt", "two");
        using var store = new TempFileStore(StoreDirectory);

        var first = await store.MaterializeAsync(_mem, new VfsPath("mem", "/a/x.txt"), null, Ct);
        var second = await store.MaterializeAsync(_mem, new VfsPath("mem", "/b/x.txt"), null, Ct);

        Assert.NotEqual(first, second);
        Assert.Equal("x.txt", Path.GetFileName(second));
        Assert.Equal("one", await File.ReadAllTextAsync(first, Ct));
        Assert.Equal("two", await File.ReadAllTextAsync(second, Ct));
        Assert.NotEqual(store.NewFile("x.txt"), store.NewFile("x.txt"));
    }

    [Fact]
    public async Task Cancelled_materialize_leaves_nothing()
    {
        _mem.AddFile("/a/big.bin", new byte[1_000_000]);
        using var store = new TempFileStore(StoreDirectory);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MaterializeAsync(_mem,
            new VfsPath("mem", "/a/big.bin"), new SyncProgress(_ => cancel.Cancel()), cancel.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Directory));
    }

    [Fact]
    public async Task Cancelled_materialize_stays_cancelled_when_cleanup_fails()
    {
        _mem.AddFile("/a/big.bin", new byte[1_000_000]);
        var store = new TempFileStore(StoreDirectory);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        string? folder = null;
        try
        {
            // The file's folder loses write permission mid-copy, so removing the part file fails.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MaterializeAsync(_mem,
                new VfsPath("mem", "/a/big.bin"), new SyncProgress(_ =>
                {
                    folder = Assert.Single(Directory.GetDirectories(store.Directory));
                    File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    cancel.Cancel();
                }), cancel.Token));
        }
        finally
        {
            if (folder is not null)
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            store.Dispose();
        }
    }

    [Fact]
    public async Task Failed_materialize_leaves_nothing()
    {
        _mem.AddFile("/a/big.bin", new byte[1_000_000]);
        _mem.FailOn((name, path) => name == "OpenRead" && path.Path == "/a/big.bin",
            () => new VfsAccessDeniedException("denied"));
        using var store = new TempFileStore(StoreDirectory);

        await Assert.ThrowsAsync<VfsAccessDeniedException>(() =>
            store.MaterializeAsync(_mem, new VfsPath("mem", "/a/big.bin"), null, Ct));

        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Directory));
    }

    [Fact]
    public async Task Clear_removes_everything_and_Release_removes_one()
    {
        _mem.AddFile("/a/x.txt", "x");
        _mem.AddFile("/a/y.txt", "y");
        using var store = new TempFileStore(StoreDirectory);
        var x = await store.MaterializeAsync(_mem, new VfsPath("mem", "/a/x.txt"), null, Ct);
        var y = await store.MaterializeAsync(_mem, new VfsPath("mem", "/a/y.txt"), null, Ct);
        var outside = _temp.Combine("outside.txt");
        await File.WriteAllTextAsync(outside, "keep", Ct);

        store.Release(x);
        store.Release(outside);
        store.Release(store.Directory);

        Assert.False(File.Exists(x));
        Assert.False(Directory.Exists(Path.GetDirectoryName(x)));
        Assert.True(File.Exists(y));
        Assert.True(File.Exists(outside));

        store.Clear();

        Assert.True(Directory.Exists(store.Directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Directory));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Clear_removes_a_link_without_following_it()
    {
        var kept = _temp.Combine("kept");
        Directory.CreateDirectory(kept);
        File.WriteAllText(Path.Combine(kept, "f.txt"), "keep");
        Directory.CreateDirectory(StoreDirectory);
        Directory.CreateSymbolicLink(Path.Combine(StoreDirectory, "link"), kept);

        using var store = new TempFileStore(StoreDirectory);

        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Directory));
        Assert.True(File.Exists(Path.Combine(kept, "f.txt")));
    }

    [Fact]
    public void New_store_clears_leftovers_of_previous_run()
    {
        Directory.CreateDirectory(Path.Combine(StoreDirectory, "old", "deep"));
        File.WriteAllText(Path.Combine(StoreDirectory, "old", "deep", "a.txt"), "a");
        File.WriteAllText(Path.Combine(StoreDirectory, "b.txt"), "b");

        using var store = new TempFileStore(StoreDirectory);

        Assert.Equal(Path.GetFullPath(StoreDirectory), store.Directory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Directory));
    }

    [Fact]
    public void Dispose_clears_the_store()
    {
        var store = new TempFileStore(StoreDirectory);
        File.WriteAllText(store.NewFile("a.txt"), "a");

        store.Dispose();

        Assert.Empty(Directory.EnumerateFileSystemEntries(StoreDirectory));
    }

    [Fact]
    public void New_file_is_named_inside_its_own_folder_and_not_created()
    {
        using var store = new TempFileStore(StoreDirectory);

        var path = store.NewFile("a b.txt");

        Assert.Equal("a b.txt", Path.GetFileName(path));
        Assert.Equal(store.Directory, Path.GetDirectoryName(Path.GetDirectoryName(path)));
        Assert.False(File.Exists(path));
        Assert.Throws<ArgumentException>(() => store.NewFile("a/b"));
        Assert.Throws<ArgumentException>(() => store.NewFile(".."));
        Assert.Throws<ArgumentException>(() => store.NewFile(""));
    }

    [Fact]
    public void Default_directory_is_in_the_apps_caches()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS folders; Windows uses AppDirectories.Temp.");
        Assert.Equal("/Users/u/Library/Caches/Tandem/tmp", TempFileStore.DefaultDirectory("/Users/u"));
    }

    /// <summary>Reports on the calling thread (unlike <see cref="Progress{T}"/>), so the test sees every report in
    /// order before the call returns.</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
