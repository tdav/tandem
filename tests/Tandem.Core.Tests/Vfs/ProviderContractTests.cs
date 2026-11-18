using System.Text;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Vfs;

/// <summary>
/// Behaviour every <see cref="IFileSystemProvider"/> must share. Derived classes supply a provider and an
/// empty, writable directory inside it; the tests arrange and assert only through the interface.
/// </summary>
public abstract class ProviderContractTests
{
    /// <summary>Returns a provider and an existing, empty directory in it that the test may modify.</summary>
    protected abstract Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enumerate_lists_direct_children_only()
    {
        var (fs, root) = await CreateAsync();
        await WriteAsync(fs, root.Combine("a.txt"), "a");
        await fs.CreateDirectoryAsync(root.Combine("sub"), Ct);
        await WriteAsync(fs, root.Combine("sub").Combine("b.txt"), "b");

        var entries = await ListAsync(fs, root);

        Assert.Equal(2, entries.Count);
        var file = Assert.Single(entries, e => e.Name == "a.txt");
        Assert.Equal(EntryKind.File, file.Kind);
        Assert.Equal(root.Combine("a.txt"), file.Path);
        var dir = Assert.Single(entries, e => e.Name == "sub");
        Assert.Equal(EntryKind.Directory, dir.Kind);
    }

    [Fact]
    public async Task Write_then_read_roundtrips_bytes()
    {
        var (fs, root) = await CreateAsync();
        var path = root.Combine("data.bin");
        var bytes = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();

        await using (var write = await fs.OpenWriteAsync(path, Ct))
            await write.WriteAsync(bytes, Ct);

        Assert.Equal(bytes, await ReadAsync(fs, path));
        Assert.Equal(bytes.Length, (await fs.GetEntryAsync(path, Ct))!.Size);
    }

    [Fact]
    public async Task OpenWrite_truncates_existing_file()
    {
        var (fs, root) = await CreateAsync();
        var path = root.Combine("f.txt");
        await WriteAsync(fs, path, "a long first version");

        await WriteAsync(fs, path, "short");

        Assert.Equal("short", Encoding.UTF8.GetString(await ReadAsync(fs, path)));
    }

    [Fact]
    public async Task GetEntry_returns_null_for_missing()
    {
        var (fs, root) = await CreateAsync();

        Assert.Null(await fs.GetEntryAsync(root.Combine("missing"), Ct));
    }

    [Fact]
    public async Task Enumerate_missing_throws_NotFound()
    {
        var (fs, root) = await CreateAsync();

        await Assert.ThrowsAsync<VfsNotFoundException>(() => ListAsync(fs, root.Combine("missing")));
    }

    [Fact]
    public async Task Move_onto_existing_throws_AlreadyExists()
    {
        var (fs, root) = await CreateAsync();
        await WriteAsync(fs, root.Combine("a.txt"), "a");
        await WriteAsync(fs, root.Combine("b.txt"), "b");

        await Assert.ThrowsAsync<VfsAlreadyExistsException>(
            () => fs.MoveAsync(root.Combine("a.txt"), root.Combine("b.txt"), Ct));

        Assert.Equal("a", Encoding.UTF8.GetString(await ReadAsync(fs, root.Combine("a.txt"))));
        Assert.Equal("b", Encoding.UTF8.GetString(await ReadAsync(fs, root.Combine("b.txt"))));
    }

    [Fact]
    public async Task Move_renames_directory_with_contents()
    {
        var (fs, root) = await CreateAsync();
        await fs.CreateDirectoryAsync(root.Combine("old"), Ct);
        await WriteAsync(fs, root.Combine("old").Combine("f.txt"), "x");

        await fs.MoveAsync(root.Combine("old"), root.Combine("new"), Ct);

        Assert.Null(await fs.GetEntryAsync(root.Combine("old"), Ct));
        Assert.Equal("x", Encoding.UTF8.GetString(await ReadAsync(fs, root.Combine("new").Combine("f.txt"))));
    }

    [Fact]
    public async Task Delete_nonempty_without_recursive_throws()
    {
        var (fs, root) = await CreateAsync();
        var dir = root.Combine("dir");
        await fs.CreateDirectoryAsync(dir, Ct);
        await WriteAsync(fs, dir.Combine("f.txt"), "x");

        await Assert.ThrowsAnyAsync<IOException>(() => fs.DeleteAsync(dir, recursive: false, Ct));

        Assert.NotNull(await fs.GetEntryAsync(dir.Combine("f.txt"), Ct));
    }

    [Fact]
    public async Task Delete_recursive_removes_tree()
    {
        var (fs, root) = await CreateAsync();
        var dir = root.Combine("dir");
        await fs.CreateDirectoryAsync(dir.Combine("inner"), Ct);
        await WriteAsync(fs, dir.Combine("inner").Combine("f.txt"), "x");
        await WriteAsync(fs, dir.Combine("g.txt"), "y");

        await fs.DeleteAsync(dir, recursive: true, Ct);

        Assert.Null(await fs.GetEntryAsync(dir, Ct));
        Assert.Empty(await ListAsync(fs, root));
    }

    [Fact]
    public async Task CreateDirectory_twice_is_ok()
    {
        var (fs, root) = await CreateAsync();
        var dir = root.Combine("dir");

        await fs.CreateDirectoryAsync(dir, Ct);
        await fs.CreateDirectoryAsync(dir, Ct);

        Assert.Equal(EntryKind.Directory, (await fs.GetEntryAsync(dir, Ct))!.Kind);
    }

    [Fact]
    public async Task Symlink_is_reported_as_symlink_and_not_traversed()
    {
        var (fs, root) = await CreateAsync();
        await fs.CreateDirectoryAsync(root.Combine("dir"), Ct);
        await WriteAsync(fs, root.Combine("dir").Combine("inside.txt"), "x");

        await fs.CreateSymlinkAsync(root.Combine("link"), "dir", Ct);

        var entries = await ListAsync(fs, root);
        Assert.Equal(2, entries.Count);
        var link = Assert.Single(entries, e => e.Name == "link");
        Assert.Equal(EntryKind.Symlink, link.Kind);
        Assert.Equal("dir", link.LinkTarget);
        Assert.True(link.LinkPointsToDirectory);

        var direct = await fs.GetEntryAsync(root.Combine("link"), Ct);
        Assert.Equal(EntryKind.Symlink, direct!.Kind);
        Assert.Equal("dir", direct.LinkTarget);
    }

    [Fact]
    public async Task SetModified_is_visible_in_GetEntry()
    {
        var (fs, root) = await CreateAsync();
        var path = root.Combine("f.txt");
        await WriteAsync(fs, path, "x");
        var stamp = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await fs.SetModifiedAsync(path, stamp, Ct);

        Assert.Equal(stamp, (await fs.GetEntryAsync(path, Ct))!.Modified);
    }

    [Fact]
    public async Task SetMode_is_visible_in_GetEntry()
    {
        var (fs, root) = await CreateAsync();
        var path = root.Combine("f.txt");
        await WriteAsync(fs, path, "x");
        const UnixFileMode ownerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await fs.SetModeAsync(path, ownerReadWrite, Ct);

        Assert.Equal(ownerReadWrite, (await fs.GetEntryAsync(path, Ct))!.Mode);
    }

    [Fact]
    public async Task Paths_under_same_root_share_volume_key()
    {
        var (fs, root) = await CreateAsync();
        await fs.CreateDirectoryAsync(root.Combine("sub"), Ct);

        var rootKey = await fs.GetVolumeKeyAsync(root, Ct);
        var nestedKey = await fs.GetVolumeKeyAsync(root.Combine("sub").Combine("missing.txt"), Ct);

        Assert.False(string.IsNullOrEmpty(rootKey));
        Assert.Equal(rootKey, nestedKey);
    }

    [Fact]
    public async Task CreateDirectory_on_existing_file_throws()
    {
        var (fs, root) = await CreateAsync();
        var path = root.Combine("f.txt");
        await WriteAsync(fs, path, "x");

        await Assert.ThrowsAnyAsync<IOException>(() => fs.CreateDirectoryAsync(path, Ct));

        Assert.Equal(EntryKind.File, (await fs.GetEntryAsync(path, Ct))!.Kind);
    }

    [Fact]
    public async Task OpenWrite_with_missing_parent_throws_NotFound()
    {
        var (fs, root) = await CreateAsync();

        await Assert.ThrowsAsync<VfsNotFoundException>(
            () => fs.OpenWriteAsync(root.Combine("missing").Combine("f.txt"), Ct));
    }

    [Fact]
    public async Task Cancelled_token_produces_canceled_task()
    {
        var (fs, root) = await CreateAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = fs.CreateDirectoryAsync(root.Combine("dir"), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
        Assert.Null(await fs.GetEntryAsync(root.Combine("dir"), Ct));
    }

    protected static async Task WriteAsync(IFileSystemProvider fs, VfsPath path, string text)
    {
        await using var stream = await fs.OpenWriteAsync(path, Ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), Ct);
    }

    protected static async Task<byte[]> ReadAsync(IFileSystemProvider fs, VfsPath path)
    {
        await using var stream = await fs.OpenReadAsync(path, Ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    protected static async Task<List<FileEntry>> ListAsync(IFileSystemProvider fs, VfsPath directory)
    {
        var list = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(directory, Ct))
            list.Add(entry);
        return list;
    }
}
