using System.Text;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Net.Remote;

namespace Tandem.Net.Tests.Remote;

internal static class RemoteFixture
{
    public static readonly VfsPath OriginDirectory = new("mem", "/home/user/projects");

    public static ConnectionProfile Profile(string host = "host.test") =>
        new(Guid.NewGuid(), "test", NetProtocol.Sftp, host, 22, "user", NetAuth.Password);

    public static RemoteProvider Provider(FakeRemoteServer server, string id = "srv", ConnectionProfile? profile = null) =>
        new(id, profile ?? Profile(), server.NewSession, new HostLocation(OriginDirectory, null));
}

public sealed class RemoteProviderTests : IAsyncDisposable
{
    private readonly FakeRemoteServer _server = new();
    private readonly RemoteProvider _provider;

    public RemoteProviderTests() => _provider = RemoteFixture.Provider(_server);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private VfsPath P(string path) => new(_provider.Id, path);

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private async Task<List<string>> NamesAsync(string directory)
    {
        var names = new List<string>();
        await foreach (var entry in _provider.EnumerateAsync(P(directory), Ct)) names.Add(entry.Name);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private async Task<string> ReadTextAsync(string path)
    {
        await using var stream = await _provider.OpenReadAsync(P(path), Ct);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Idle_connection_closed_by_the_server_is_replaced_silently(bool multiplexed)
    {
        if (multiplexed) _server.Features |= RemoteFeatures.Multiplexed;
        _server.AddFile("/a.txt", "a");
        await _provider.ConnectAsync(Ct);

        _server.DropNow();

        Assert.Equal(["a.txt"], await NamesAsync("/"));
        Assert.Equal(2, _server.ConnectCount);
        Assert.Equal(1, _server.OpenNow);
    }

    [Fact]
    public async Task Several_idle_connections_closed_by_the_server_cost_one_retry()
    {
        _server.AddFile("/a.txt", "a");
        await using (await _provider.OpenReadAsync(P("/a.txt"), Ct))
        await using (await _provider.OpenReadAsync(P("/a.txt"), Ct))
        {
        }
        Assert.Equal(2, _server.OpenNow); // both free now

        _server.DropNow();

        Assert.Equal(["a.txt"], await NamesAsync("/"));
        Assert.Equal(3, _server.ConnectCount);
    }

    [Fact]
    public async Task Offline_server_is_connection_lost_and_works_again_when_it_returns()
    {
        _server.AddFile("/a.txt", "a");
        await _provider.ConnectAsync(Ct);
        _server.Offline = true;
        _server.DropNow();

        await Assert.ThrowsAsync<VfsConnectionLostException>(() => NamesAsync("/"));
        await Assert.ThrowsAsync<VfsConnectionLostException>(() => _provider.GetEntryAsync(P("/a.txt"), Ct));

        _server.Offline = false;
        Assert.Equal(["a.txt"], await NamesAsync("/"));
        Assert.Equal(2, _server.ConnectCount);
    }

    [Fact]
    public async Task Offline_server_fails_the_first_connect_too()
    {
        _server.Offline = true;

        await Assert.ThrowsAsync<VfsConnectionLostException>(() => _provider.ConnectAsync(Ct));
        Assert.False(_provider.HasOpenConnections);

        _server.Offline = false;
        await _provider.ConnectAsync(Ct);
        Assert.True(_provider.HasOpenConnections);
    }

    [Fact]
    public async Task Drop_in_the_middle_of_a_read_or_write_is_connection_lost()
    {
        var content = new string('x', 1000);
        _server.AddFile("/big.txt", content);

        _server.DropAfterBytes = 100;
        await Assert.ThrowsAsync<VfsConnectionLostException>(() => ReadTextAsync("/big.txt"));
        Assert.Equal(content, await ReadTextAsync("/big.txt"));

        _server.DropAfterBytes = 100;
        await Assert.ThrowsAsync<VfsConnectionLostException>(async () =>
        {
            await using var write = await _provider.OpenWriteAsync(P("/up.txt"), Ct);
            await write.WriteAsync(Encoding.UTF8.GetBytes(content), Ct);
        });
        Assert.Equal(100, (await _provider.GetEntryAsync(P("/up.txt"), Ct))!.Size); // the server keeps what came

        Assert.Equal(3, _server.ConnectCount);
        Assert.Equal(1, _server.OpenNow);
    }

    [Fact]
    public async Task Error_at_closing_a_written_stream_is_not_swallowed()
    {
        _server.FailCloseWrite = true;
        var write = await _provider.OpenWriteAsync(P("/f.txt"), Ct);
        await write.WriteAsync("data"u8.ToArray(), Ct);

        await Assert.ThrowsAnyAsync<IOException>(() => write.DisposeAsync().AsTask());

        _server.FailCloseWrite = true;
        var syncWrite = await _provider.OpenWriteAsync(P("/g.txt"), Ct);
        syncWrite.Write("data"u8);
        Assert.ThrowsAny<IOException>(syncWrite.Dispose);

        _server.FailCloseWrite = false;
        Assert.Equal(["f.txt", "g.txt"], await NamesAsync("/")); // the connection is free again
        Assert.Equal(1, _server.ConnectCount);
    }

    [Fact]
    public async Task Move_onto_an_existing_name_throws_even_if_the_server_would_replace_it()
    {
        _server.RenameOverwrites = true;
        _server.AddFile("/a.txt", "a");
        _server.AddFile("/b.txt", "b");

        await Assert.ThrowsAsync<VfsAlreadyExistsException>(() => _provider.MoveAsync(P("/a.txt"), P("/b.txt"), Ct));

        Assert.Equal("a", _server.ReadAllText("/a.txt"));
        Assert.Equal("b", _server.ReadAllText("/b.txt"));
        Assert.DoesNotContain(_server.Log, line => line.Contains("Rename", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_does_not_rename_when_the_existence_check_fails()
    {
        _server.RenameOverwrites = true;
        _server.AddFile("/a.txt", "a");
        _server.AddFile("/b.txt", "b");
        _server.FailOn("Stat", "/b.txt", () => new VfsNotFoundException("no access, reported as not found"));

        await Assert.ThrowsAsync<VfsNotFoundException>(() => _provider.MoveAsync(P("/a.txt"), P("/b.txt"), Ct));

        Assert.Equal("b", _server.ReadAllText("/b.txt"));
        Assert.DoesNotContain(_server.Log, line => line.Contains("Rename", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_renames_onto_a_free_name_and_onto_itself_does_nothing()
    {
        _server.AddFile("/a.txt", "a");

        await _provider.MoveAsync(P("/a.txt"), P("/a.txt"), Ct);
        await _provider.MoveAsync(P("/a.txt"), P("/c.txt"), Ct);

        Assert.Equal("a", _server.ReadAllText("/c.txt"));
        Assert.False(_server.Exists("/a.txt"));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => _provider.MoveAsync(P("/a.txt"), P("/a.txt"), Ct));
        Assert.Single(_server.Log, line => line.Contains("Rename", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recursive_delete_removes_the_tree_and_does_not_enter_links()
    {
        _server.AddFile("/outside/keep.txt", "keep");
        _server.AddFile("/d/f.txt", "f");
        _server.AddFile("/d/sub/g.txt", "g");
        _server.AddDirectory("/d/sub/empty");
        _server.AddSymlink("/d/link", "/outside");
        _server.AddSymlink("/d/sub/relative", "../../outside");

        await _provider.DeleteAsync(P("/d"), recursive: true, Ct);

        Assert.Equal(["/outside", "/outside/keep.txt"], _server.AllPaths);
        Assert.Contains("#1 DeleteFile /d/link", _server.Log);
        Assert.Contains("#1 DeleteFile /d/sub/relative", _server.Log);
        Assert.DoesNotContain(_server.Log, line => line.Contains("List /d/link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_of_a_missing_entry_is_not_found_and_of_the_root_is_refused()
    {
        await Assert.ThrowsAsync<VfsNotFoundException>(() => _provider.DeleteAsync(P("/missing"), true, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _provider.DeleteAsync(P("/"), true, Ct));
    }

    [Fact]
    public async Task CreateDirectory_makes_missing_parents()
    {
        _server.AddDirectory("/a");
        _server.AddFile("/file.txt", "x");

        await _provider.CreateDirectoryAsync(P("/a/b/c/d"), Ct);
        await _provider.CreateDirectoryAsync(P("/"), Ct);

        Assert.Equal(["/a", "/a/b", "/a/b/c", "/a/b/c/d", "/file.txt"], _server.AllPaths);
        await Assert.ThrowsAsync<VfsAlreadyExistsException>(() => _provider.CreateDirectoryAsync(P("/file.txt/x"), Ct));
        await Assert.ThrowsAsync<VfsAlreadyExistsException>(() => _provider.CreateDirectoryAsync(P("/file.txt"), Ct));
    }

    [Fact]
    public async Task Two_open_streams_use_two_connections()
    {
        _server.AddFile("/a.txt", "a");
        _server.AddFile("/b.txt", "b");

        await using (var a = await _provider.OpenReadAsync(P("/a.txt"), Ct))
        await using (var b = await _provider.OpenReadAsync(P("/b.txt"), Ct))
        {
            Assert.Equal(["a.txt", "b.txt"], await NamesAsync("/")); // a third call while both are open
            Assert.Equal((byte)'b', (byte)b.ReadByte());
            Assert.Equal((byte)'a', (byte)a.ReadByte());
        }

        Assert.Equal(3, _server.MaxOpenAtOnce);
        Assert.Equal(3, _server.ConnectCount);
        Assert.Equal(["a.txt", "b.txt"], await NamesAsync("/")); // reuses a free one
        await using (var a = await _provider.OpenReadAsync(P("/a.txt"), Ct))
        await using (var b = await _provider.OpenReadAsync(P("/b.txt"), Ct)) // two free ones, not one twice
        {
            Assert.Equal((byte)'a', (byte)a.ReadByte());
            Assert.Equal((byte)'b', (byte)b.ReadByte());
        }
        Assert.Equal(3, _server.ConnectCount);
    }

    [Fact]
    public async Task A_fifth_stream_waits_for_a_free_connection_and_can_be_cancelled()
    {
        for (var i = 0; i < 5; i++) _server.AddFile($"/{i}.txt", i.ToString());
        var open = new List<Stream>();
        for (var i = 0; i < RemoteProvider.MaxSessions; i++) open.Add(await _provider.OpenReadAsync(P($"/{i}.txt"), Ct));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var fifth = _provider.OpenReadAsync(P("/4.txt"), cts.Token);
        await Task.Delay(100, Ct);
        Assert.False(fifth.IsCompleted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fifth);

        var waiting = _provider.OpenReadAsync(P("/4.txt"), Ct);
        await Task.Delay(50, Ct);
        Assert.False(waiting.IsCompleted);
        await open[0].DisposeAsync();
        await using (var stream = await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct))
            Assert.Equal((byte)'4', (byte)stream.ReadByte());

        foreach (var stream in open.Skip(1)) await stream.DisposeAsync();
        Assert.Equal(RemoteProvider.MaxSessions, _server.MaxOpenAtOnce);
        Assert.Equal(RemoteProvider.MaxSessions, _server.ConnectCount);
    }

    [Fact]
    public async Task Multiplexed_server_gets_one_connection()
    {
        _server.Features |= RemoteFeatures.Multiplexed;
        for (var i = 0; i < 6; i++) _server.AddFile($"/{i}.txt", i.ToString());

        var streams = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => _provider.OpenReadAsync(P($"/{i}.txt"), Ct)));
        Assert.Equal(6, (await NamesAsync("/")).Count);
        foreach (var stream in streams) await stream.DisposeAsync();

        Assert.Equal(1, _server.ConnectCount);
        Assert.Equal(1, _server.MaxOpenAtOnce);
    }

    [Fact]
    public async Task Volume_key_opens_no_connection()
    {
        var key = await _provider.GetVolumeKeyAsync(P("/a/b"), Ct);

        Assert.Equal(_provider.Profile.VolumeKey, key);
        Assert.Equal(0, _server.ConnectCount);
    }

    [Fact]
    public void Up_from_the_root_leads_to_the_origin_and_persistent_location_is_the_origin()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        registry.Register(_provider);
        var origin = new HostLocation(RemoteFixture.OriginDirectory, null);

        Assert.Equal(origin, registry.UpOf(_provider.Root));
        Assert.Equal(origin, registry.HostLocationOf(P("/var/www")));
        Assert.Equal(new HostLocation(P("/var"), "www"), registry.UpOf(P("/var/www")));
        Assert.Equal(RemoteFixture.OriginDirectory, registry.PersistentLocationOf(P("/var/www")));
        Assert.Equal(new VfsPath("srv", "/"), _provider.Root);
        Assert.Equal(0, _server.ConnectCount);
    }

    [Fact]
    public void Origin_focus_is_not_kept()
    {
        var provider = new RemoteProvider("other", RemoteFixture.Profile(), _server.NewSession,
            new HostLocation(RemoteFixture.OriginDirectory, "something"));

        Assert.Equal(new HostLocation(RemoteFixture.OriginDirectory, null), provider.GetHostLocation(provider.Root));
        Assert.Null(provider.GetHostLocation(new VfsPath("other", "/a")));
    }

    [Fact]
    public void Display_path_is_address_plus_path_without_a_password()
    {
        var profile = RemoteFixture.Profile() with { Port = 2222 };
        var provider = new RemoteProvider("p", profile, _server.NewSession, new HostLocation(RemoteFixture.OriginDirectory, null));

        Assert.Equal("sftp://user@host.test:2222/var/www", provider.GetDisplayPath(new VfsPath("p", "/var/www")));
        Assert.Equal("sftp://user@host.test:2222/", provider.GetDisplayPath(provider.Root));
        Assert.Same(profile, provider.Profile);
    }

    [Theory]
    [InlineData(RemoteFeatures.None, ProviderCapabilities.None)]
    [InlineData(RemoteFeatures.Symlinks, ProviderCapabilities.Symlinks)]
    [InlineData(RemoteFeatures.Modes | RemoteFeatures.SetModified, ProviderCapabilities.UnixPermissions)]
    [InlineData(RemoteFeatures.Symlinks | RemoteFeatures.Modes | RemoteFeatures.Multiplexed,
        ProviderCapabilities.Symlinks | ProviderCapabilities.UnixPermissions)]
    public async Task Capabilities_follow_the_server_features(RemoteFeatures features, ProviderCapabilities extra)
    {
        _server.Features = features;
        const ProviderCapabilities always = ProviderCapabilities.Write | ProviderCapabilities.LocalCopyEdit;
        Assert.Equal(always, _provider.Capabilities); // not connected yet: nothing is known

        await _provider.ConnectAsync(Ct);

        Assert.Equal(always | extra, _provider.Capabilities);
        Assert.Null(_provider.Watch(_provider.Root, () => { }));
    }

    [Fact]
    public async Task CloseConnections_then_a_call_reconnects()
    {
        _server.AddFile("/a.txt", "a");
        await _provider.ConnectAsync(Ct);
        Assert.True(_provider.HasOpenConnections);

        await _provider.CloseConnectionsAsync();

        Assert.False(_provider.HasOpenConnections);
        Assert.Equal(0, _server.OpenNow);
        Assert.Equal(["a.txt"], await NamesAsync("/"));
        Assert.Equal(2, _server.ConnectCount);

        var busy = await _provider.OpenReadAsync(P("/a.txt"), Ct);
        await _provider.CloseConnectionsAsync();
        Assert.True(_provider.HasOpenConnections); // the busy one closes when it is free
        Assert.Equal(1, _server.OpenNow);
        Assert.Equal((byte)'a', (byte)busy.ReadByte());
        await busy.DisposeAsync();
        Assert.False(_provider.HasOpenConnections);
        Assert.Equal(0, _server.OpenNow);
    }

    [Fact]
    public async Task Entries_carry_the_server_details()
    {
        var stamp = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero);
        _server.AddFile("/dir/.hidden", "12345", stamp);
        _server.AddSymlink("/dir/link", ".");

        var file = (await _provider.GetEntryAsync(P("/dir/.hidden"), Ct))!;
        Assert.Equal(new FileEntry(P("/dir/.hidden"), EntryKind.File, 5, stamp, stamp, file.Mode, IsHidden: true), file);
        Assert.NotNull(file.Mode);
        var link = (await _provider.GetEntryAsync(P("/dir/link"), Ct))!;
        Assert.Equal(EntryKind.Symlink, link.Kind);
        Assert.Equal(".", link.LinkTarget);
        Assert.True(link.LinkPointsToDirectory);
        Assert.False(link.IsHidden);
        Assert.Equal(EntryKind.Directory, (await _provider.GetEntryAsync(_provider.Root, Ct))!.Kind);
    }

    [Fact]
    public async Task Paths_of_another_provider_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _provider.GetEntryAsync(new VfsPath("other", "/a"), Ct));
    }

    [Fact]
    public async Task A_cancelled_call_leaves_the_pool_usable()
    {
        _server.AddFile("/a.txt", "a");
        await _provider.ConnectAsync(Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.GetEntryAsync(P("/a.txt"), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.OpenReadAsync(P("/a.txt"), cts.Token));

        Assert.Equal("a", await ReadTextAsync("/a.txt"));
    }

    [Fact]
    public async Task After_dispose_calls_are_refused_and_connections_closed()
    {
        await _provider.ConnectAsync(Ct);

        await _provider.DisposeAsync();

        Assert.Equal(0, _server.OpenNow);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _provider.GetEntryAsync(P("/"), Ct));
    }
}
