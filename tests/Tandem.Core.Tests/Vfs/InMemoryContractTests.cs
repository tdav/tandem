using System.Text;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Vfs;

public class InMemoryContractTests : ProviderContractTests
{
    protected override Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/work");
        return Task.FromResult<(IFileSystemProvider, VfsPath)>((fs, new VfsPath("mem", "/work")));
    }

    private static VfsPath Mem(string path) => new("mem", path);

    [Fact]
    public void Defaults_to_mem_id_and_all_capabilities_but_trash()
    {
        var fs = new InMemoryFileSystem();

        Assert.Equal("mem", fs.Id);
        Assert.Equal(ProviderCapabilities.Write | ProviderCapabilities.UnixPermissions |
                     ProviderCapabilities.Symlinks | ProviderCapabilities.Watch, fs.Capabilities);
        Assert.Equal("other", new InMemoryFileSystem("other").Id);
    }

    [Fact]
    public void AddFile_creates_parents_and_helpers_see_it()
    {
        var fs = new InMemoryFileSystem();

        fs.AddFile("/a/b/c.txt", "hello");
        fs.AddFile("/a/d.bin", [1, 2, 3]);

        Assert.Equal(["/a", "/a/b", "/a/b/c.txt", "/a/d.bin"], fs.AllPaths);
        Assert.True(fs.Exists("/a/b"));
        Assert.False(fs.Exists("/a/x"));
        Assert.Equal("hello", fs.ReadAllText("/a/b/c.txt"));
    }

    [Fact]
    public async Task AddFile_uses_given_modified_time()
    {
        var fs = new InMemoryFileSystem();
        var stamp = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero);

        fs.AddFile("/f.txt", "x", stamp);

        Assert.Equal(stamp, (await fs.GetEntryAsync(Mem("/f.txt"), Ct))!.Modified);
    }

    [Fact]
    public async Task FailWriteAfter_throws_after_given_bytes()
    {
        var fs = new InMemoryFileSystem();
        fs.FailWriteAfter("/f.txt", 5, () => new IOException("disk full"));
        await using var stream = await fs.OpenWriteAsync(Mem("/f.txt"), Ct);

        await stream.WriteAsync("abc"u8.ToArray(), Ct);
        var error = await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync("defg"u8.ToArray(), Ct).AsTask());

        Assert.Equal("disk full", error.Message);
        Assert.Equal("abcde", fs.ReadAllText("/f.txt"));
    }

    [Fact]
    public async Task FailOn_throws_for_matching_operation()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a.txt", "a");
        fs.AddFile("/b.txt", "b");
        fs.FailOn((op, path) => op == "OpenRead" && path.Name == "a.txt",
            () => new VfsAccessDeniedException("denied"));

        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => fs.OpenReadAsync(Mem("/a.txt"), Ct));
        (await fs.OpenReadAsync(Mem("/b.txt"), Ct)).Dispose();
        Assert.NotNull(await fs.GetEntryAsync(Mem("/a.txt"), Ct));
    }

    [Fact]
    public async Task FailOn_uses_method_names_without_async()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/d");
        var seen = new List<string>();
        fs.FailOn((op, _) => { seen.Add(op); return false; }, () => new IOException());

        await foreach (var _ in fs.EnumerateAsync(Mem("/d"), Ct)) { }
        await fs.GetEntryAsync(Mem("/d"), Ct);
        await fs.CreateDirectoryAsync(Mem("/d/e"), Ct);
        (await fs.OpenWriteAsync(Mem("/d/f"), Ct)).Dispose();
        (await fs.OpenReadAsync(Mem("/d/f"), Ct)).Dispose();
        await fs.MoveAsync(Mem("/d/f"), Mem("/d/g"), Ct);
        await fs.DeleteAsync(Mem("/d/g"), recursive: false, Ct);

        Assert.Equal(["Enumerate", "GetEntry", "CreateDirectory", "OpenWrite", "OpenRead", "Move", "Move", "Delete"], seen);
    }

    [Fact]
    public async Task Volume_key_follows_longest_registered_prefix()
    {
        var fs = new InMemoryFileSystem();
        fs.SetVolumeKey("/Volumes/usb", "usb");

        Assert.Equal("usb", await fs.GetVolumeKeyAsync(Mem("/Volumes/usb/x"), Ct));
        Assert.Equal("usb", await fs.GetVolumeKeyAsync(Mem("/Volumes/usb"), Ct));
        Assert.Equal("mem", await fs.GetVolumeKeyAsync(Mem("/Volumes/usbx"), Ct));
        Assert.Equal("mem", await fs.GetVolumeKeyAsync(Mem("/a"), Ct));
    }

    [Fact]
    public void RaiseChanged_notifies_watchers_of_that_directory_until_disposed()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/d");
        var calls = 0;
        var watch = fs.Watch(Mem("/d"), () => calls++);

        fs.RaiseChanged("/d");
        fs.RaiseChanged("/other");
        watch!.Dispose();
        fs.RaiseChanged("/d");

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Symlink_to_directory_can_be_enumerated_through()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/dir/f.txt");
        await fs.CreateSymlinkAsync(Mem("/link"), "/dir", Ct);

        var names = new List<string>();
        await foreach (var e in fs.EnumerateAsync(Mem("/link"), Ct)) names.Add(e.Name);

        Assert.Equal(["f.txt"], names);
    }

    [Fact]
    public async Task Rejects_paths_of_another_provider()
    {
        var fs = new InMemoryFileSystem();

        await Assert.ThrowsAsync<ArgumentException>(() => fs.GetEntryAsync(VfsPath.Local("/"), Ct));
    }

    [Fact]
    public async Task Is_safe_for_concurrent_use()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/d");

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            await using var s = await fs.OpenWriteAsync(Mem($"/d/{i}.txt"), Ct);
            await s.WriteAsync(Encoding.UTF8.GetBytes(i.ToString()), Ct);
            await foreach (var _ in fs.EnumerateAsync(Mem("/d"), Ct)) { }
        }, Ct)));

        Assert.Equal(51, fs.AllPaths.Count);
        Assert.Equal("42", fs.ReadAllText("/d/42.txt"));
    }
}
