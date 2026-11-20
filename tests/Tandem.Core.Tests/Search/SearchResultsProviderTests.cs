using Tandem.Core.Operations;
using Tandem.Core.Search;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Search;

public sealed class SearchResultsProviderTests
{
    private readonly InMemoryFileSystem _mem = new();
    private readonly ProviderRegistry _providers = new();
    private readonly SearchResultsProvider _search;

    public SearchResultsProviderTests()
    {
        _search = new SearchResultsProvider(_providers);
        _providers.Register(_mem);
        _providers.Register(_search);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath Mem(string path) => new("mem", path);

    private static VfsPath Search(string path) => new(SearchResultsProvider.ProviderId, path);

    private async Task<List<FileEntry>> List(VfsPath directory)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in _search.EnumerateAsync(directory, Ct)) entries.Add(entry);
        return entries;
    }

    /// <summary>Files /src/a.txt, /src/sub/b.txt and the folder /src/sub, all found in one set.</summary>
    private VfsPath AddFoundSet()
    {
        _mem.AddFile("/src/a.txt", "alpha");
        _mem.AddFile("/src/sub/b.txt", "beta");
        return _search.Add(Mem("/src"), "Search in /src: *.txt",
            [Mem("/src/a.txt"), Mem("/src/sub"), Mem("/src/sub/b.txt")]);
    }

    [Fact]
    public async Task Lists_real_entries_with_their_own_paths()
    {
        var set = AddFoundSet();

        var entries = await List(set);

        Assert.Equal([Mem("/src/a.txt"), Mem("/src/sub"), Mem("/src/sub/b.txt")], entries.Select(e => e.Path));
        Assert.All(entries, e => Assert.Equal("mem", e.Path.ProviderId));
        Assert.Equal([EntryKind.File, EntryKind.Directory, EntryKind.File], entries.Select(e => e.Kind));
        Assert.Equal(5, entries[0].Size);
        Assert.Equal(await _mem.GetEntryAsync(Mem("/src/a.txt"), Ct), entries[0]);
    }

    [Fact]
    public async Task Vanished_file_drops_out_of_the_next_listing()
    {
        var set = AddFoundSet();
        _mem.FailOn((op, path) => op == "GetEntry" && path.Path == "/src/sub",
            () => new VfsAccessDeniedException("no access"));
        _search.Add(Mem("/"), "other", [new VfsPath("gone", "/x"), Mem("/src/a.txt")]);

        await _mem.DeleteAsync(Mem("/src/a.txt"), recursive: false, Ct);

        // Gone, unreadable: both drop out, and stay out once the folder is readable again.
        Assert.Equal([Mem("/src/sub/b.txt")], (await List(set)).Select(e => e.Path));
        _mem.AddFile("/src/a.txt", "back");
        Assert.Equal([Mem("/src/sub/b.txt")], (await List(set)).Select(e => e.Path));

        // A path whose provider is not registered drops out too; the same path in another set is not affected.
        Assert.Equal([Mem("/src/a.txt")], (await List(Search("/2"))).Select(e => e.Path));
    }

    [Fact]
    public async Task Listing_stops_when_cancelled()
    {
        var set = AddFoundSet();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var listed = new List<VfsPath>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in _search.EnumerateAsync(set, cts.Token))
            {
                listed.Add(entry.Path);
                await cts.CancelAsync();
            }
        });

        Assert.Equal([Mem("/src/a.txt")], listed);
        Assert.Equal(3, (await List(set)).Count); // nothing dropped out
    }

    [Fact]
    public async Task Root_lists_result_sets_and_unknown_set_is_not_found()
    {
        var first = AddFoundSet();
        var second = _search.Add(Mem("/"), "second", []);

        Assert.Equal(Search("/1"), first);
        Assert.Equal(Search("/2"), second);
        var sets = await List(Search("/"));
        Assert.Equal([first, second], sets.Select(e => e.Path));
        Assert.All(sets, e => Assert.Equal(EntryKind.Directory, e.Kind));

        Assert.Equal(EntryKind.Directory, (await _search.GetEntryAsync(Search("/"), Ct))?.Kind);
        Assert.Equal(EntryKind.Directory, (await _search.GetEntryAsync(first, Ct))?.Kind);
        Assert.Equal(first, (await _search.GetEntryAsync(first, Ct))?.Path);
        Assert.Empty(await List(second));

        Assert.Null(await _search.GetEntryAsync(Search("/7"), Ct));
        Assert.Null(await _search.GetEntryAsync(Search("/1/a.txt"), Ct));
        Assert.Null(await _search.GetEntryAsync(Search("/01"), Ct));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => List(Search("/7")));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => List(Search("/1/sub")));
    }

    [Fact]
    public async Task Every_write_is_refused_with_access_denied()
    {
        var set = AddFoundSet();
        var inSet = set.Combine("x");

        Assert.Equal(ProviderCapabilities.None, _search.Capabilities);
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _search.OpenReadAsync(inSet, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _search.OpenWriteAsync(inSet, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _search.CreateDirectoryAsync(inSet, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _search.DeleteAsync(set, recursive: true, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _search.MoveAsync(set, Search("/9"), Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _search.CreateSymlinkAsync(inSet, "/a", Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() =>
            _search.SetModifiedAsync(set, DateTimeOffset.UnixEpoch, Ct));
        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => _search.SetModeAsync(set, UnixFileMode.UserRead, Ct));

        Assert.Equal(3, (await List(set)).Count);
        Assert.True(_mem.Exists("/src/a.txt"));
    }

    [Fact]
    public async Task Same_entry_is_an_equal_path_that_exists_and_volume_is_search()
    {
        var set = AddFoundSet();

        Assert.True(await _search.IsSameEntryAsync(set, Search("/1/"), Ct));
        Assert.True(await _search.IsSameEntryAsync(Search("/"), Search("/"), Ct));
        Assert.False(await _search.IsSameEntryAsync(set, Search("/"), Ct));
        Assert.False(await _search.IsSameEntryAsync(Search("/7"), Search("/7"), Ct));
        Assert.Equal("search", await _search.GetVolumeKeyAsync(set, Ct));
        Assert.Null(_search.Watch(set, () => { }));
    }

    [Fact]
    public void Host_location_is_the_searched_folder_and_display_path_is_the_title()
    {
        var set = AddFoundSet();

        Assert.Equal(new HostLocation(Mem("/src"), null), _search.GetHostLocation(set));
        Assert.Null(_search.GetHostLocation(Search("/")));
        Assert.Null(_search.GetHostLocation(Search("/7")));
        Assert.Equal("Search in /src: *.txt", _search.GetDisplayPath(set));
        Assert.Equal("/", _search.GetDisplayPath(Search("/")));

        // Up and saved tabs go back to where the search ran.
        Assert.Equal(new HostLocation(Mem("/src"), null), _providers.UpOf(set));
        Assert.Equal(Mem("/src"), _providers.PersistentLocationOf(set));
        Assert.Equal("Search in /src: *.txt", _providers.DisplayPath(set));
    }

    [Fact]
    public void Oldest_set_is_dropped_after_twenty()
    {
        for (var i = 1; i <= SearchResultsProvider.MaxResultSets; i++)
            Assert.Equal(Search($"/{i}"), _search.Add(Mem("/"), $"set {i}", []));
        Assert.NotNull(_search.GetHostLocation(Search("/1")));

        var newest = _search.Add(Mem("/src"), "set 21", []);

        Assert.Equal(Search("/21"), newest);
        Assert.Null(_search.GetHostLocation(Search("/1")));
        Assert.Equal("/1", _search.GetDisplayPath(Search("/1")));
        Assert.NotNull(_search.GetHostLocation(Search("/2")));
        Assert.Equal(new HostLocation(Mem("/src"), null), _search.GetHostLocation(newest));
    }

    [Fact]
    public async Task Root_lists_at_most_twenty_sets_oldest_first()
    {
        for (var i = 1; i <= SearchResultsProvider.MaxResultSets + 3; i++) _search.Add(Mem("/"), $"set {i}", []);

        var sets = await List(Search("/"));

        Assert.Equal(Enumerable.Range(4, SearchResultsProvider.MaxResultSets).Select(i => Search($"/{i}")),
            sets.Select(e => e.Path));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => List(Search("/3")));
    }

    [Fact]
    public async Task Sets_can_be_added_from_several_threads()
    {
        const int perThread = 50;
        var added = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            Enumerable.Range(0, perThread).Select(i => _search.Add(Mem("/"), $"set {i}", [])).ToList(), Ct)));

        var all = added.SelectMany(paths => paths).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(SearchResultsProvider.MaxResultSets, (await List(Search("/"))).Count);
        Assert.Equal(Search($"/{4 * perThread}"), (await List(Search("/")))[^1].Path);
    }

    [Fact]
    public async Task Copy_of_listed_entries_copies_the_real_files()
    {
        var set = AddFoundSet();
        _mem.AddDirectory("/dst");
        var files = (await List(set)).Where(e => e.Kind == EntryKind.File).Select(e => e.Path).ToList();

        var result = await new CopyOperation(_providers, files, Mem("/dst"), new CopyOptions(),
            new ScriptedInteraction()).RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal("alpha", _mem.ReadAllText("/dst/a.txt"));
        Assert.Equal("beta", _mem.ReadAllText("/dst/b.txt"));
        Assert.Equal("alpha", _mem.ReadAllText("/src/a.txt"));
    }

    [Fact]
    public async Task Delete_of_listed_entries_deletes_the_real_files_and_listing_shrinks()
    {
        var set = AddFoundSet();
        var files = (await List(set)).Where(e => e.Kind == EntryKind.File).Select(e => e.Path).ToList();

        var result = await new DeleteOperation(_providers, files, toTrash: false, trash: null,
            new ScriptedInteraction()).RunAsync();

        Assert.Equal(OperationState.Completed, result.State);
        Assert.False(_mem.Exists("/src/a.txt"));
        Assert.False(_mem.Exists("/src/sub/b.txt"));
        Assert.Equal([Mem("/src/sub")], (await List(set)).Select(e => e.Path));
    }
}
