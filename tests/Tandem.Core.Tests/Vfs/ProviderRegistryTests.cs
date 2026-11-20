using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Vfs;

public class ProviderRegistryTests
{
    [Fact]
    public void Get_and_For_find_registered_provider()
    {
        var registry = new ProviderRegistry();
        var mem = new InMemoryFileSystem();
        var other = new InMemoryFileSystem("other");

        registry.Register(mem);
        registry.Register(other);

        Assert.Same(mem, registry.Get("mem"));
        Assert.Same(other, registry.For(new VfsPath("other", "/a")));
    }

    [Fact]
    public void Get_unknown_throws() =>
        Assert.Throws<KeyNotFoundException>(() => new ProviderRegistry().Get("nope"));

    [Fact]
    public void Register_duplicate_id_throws()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());

        Assert.Throws<InvalidOperationException>(() => registry.Register(new InMemoryFileSystem()));
    }

    [Fact]
    public void TryGet_reports_missing_provider()
    {
        var registry = new ProviderRegistry();
        var mem = new InMemoryFileSystem();
        registry.Register(mem);

        Assert.True(registry.TryGet("mem", out var found));
        Assert.Same(mem, found);
        Assert.False(registry.TryGet("nope", out var missing));
        Assert.Null(missing);
    }

    // ---- Hosted providers ----------------------------------------------------------------------------

    [Fact]
    public void Plain_provider_displays_its_own_path_and_has_no_host()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        var path = new VfsPath("mem", "/a/b");

        Assert.Equal("/a/b", registry.DisplayPath(path));
        Assert.Null(registry.HostLocationOf(path));
        Assert.Null(registry.HostLocationOf(new VfsPath("mem", "/")));
        Assert.Equal(path, registry.PersistentLocationOf(path));
    }

    [Fact]
    public void Host_location_of_a_hosted_root_is_where_up_leads()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        registry.Register(HostedFileSystem.InFile("zip", registry, new VfsPath("mem", "/a/b.zip")));

        Assert.Equal(new HostLocation(new VfsPath("mem", "/a"), "b.zip"),
            registry.HostLocationOf(new VfsPath("zip", "/")));
    }

    [Fact]
    public void Host_location_of_a_path_below_a_hosted_root_is_the_roots()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        registry.Register(HostedFileSystem.InFile("zip", registry, new VfsPath("mem", "/a/b.zip")));

        Assert.Equal(new HostLocation(new VfsPath("mem", "/a"), "b.zip"),
            registry.HostLocationOf(new VfsPath("zip", "/docs")));
        Assert.Equal(new VfsPath("mem", "/a"), registry.PersistentLocationOf(new VfsPath("zip", "/docs/x")));
    }

    [Fact]
    public void Host_location_comes_from_the_nearest_ancestor_that_has_one()
    {
        // Search results: each result set is a folder "/n" of the provider, leading up to where the search ran.
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        var results = new HostedFileSystem("find");
        results.SetHostLocation("/1", new HostLocation(new VfsPath("mem", "/src"), null));
        results.SetHostLocation("/2", new HostLocation(new VfsPath("mem", "/docs"), null));
        registry.Register(results);

        Assert.Equal(new HostLocation(new VfsPath("mem", "/src"), null),
            registry.HostLocationOf(new VfsPath("find", "/1")));
        Assert.Equal(new HostLocation(new VfsPath("mem", "/docs"), null),
            registry.HostLocationOf(new VfsPath("find", "/2/sub")));
        Assert.Null(registry.HostLocationOf(new VfsPath("find", "/")));
        Assert.Equal(new VfsPath("mem", "/docs"), registry.PersistentLocationOf(new VfsPath("find", "/2/sub")));
        Assert.Equal(new VfsPath("find", "/"), registry.PersistentLocationOf(new VfsPath("find", "/")));
    }

    [Fact]
    public void Hosted_provider_without_host_locations_has_none_and_persists_its_own_path()
    {
        var registry = new ProviderRegistry();
        registry.Register(new HostedFileSystem("net") { Display = path => "net:" + path.Path });
        var path = new VfsPath("net", "/share/dir");

        Assert.Null(registry.HostLocationOf(path));
        Assert.Equal(path, registry.PersistentLocationOf(path));
        Assert.Equal("net:/share/dir", registry.DisplayPath(path));
    }

    [Fact]
    public void Persistent_location_of_hosted_path_is_host_directory_recursively()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        registry.Register(HostedFileSystem.InFile("outer", registry, new VfsPath("mem", "/a/b.zip")));
        registry.Register(HostedFileSystem.InFile("inner", registry, new VfsPath("outer", "/docs/c.tar")));

        Assert.Equal(new VfsPath("mem", "/a"), registry.PersistentLocationOf(new VfsPath("inner", "/x/y")));
        Assert.Equal(new HostLocation(new VfsPath("outer", "/docs"), "c.tar"),
            registry.HostLocationOf(new VfsPath("inner", "/x")));
        Assert.Equal("/a/b.zip/docs/c.tar/x/y", registry.DisplayPath(new VfsPath("inner", "/x/y")));
        Assert.Equal("/a/b.zip", registry.DisplayPath(new VfsPath("outer", "/")));
    }

    [Fact]
    public void Persistent_location_refuses_hosts_in_a_cycle()
    {
        var registry = new ProviderRegistry();
        var a = new HostedFileSystem("a");
        var b = new HostedFileSystem("b");
        a.SetHostLocation("/", new HostLocation(new VfsPath("b", "/"), null));
        b.SetHostLocation("/", new HostLocation(new VfsPath("a", "/"), null));
        registry.Register(a);
        registry.Register(b);

        Assert.Throws<InvalidOperationException>(() => registry.PersistentLocationOf(new VfsPath("a", "/x")));
    }

    [Fact]
    public void Hosted_batching_provider_reports_its_hosts_provider_as_BatchHost()
    {
        var registry = new ProviderRegistry();
        var journal = new BatchJournal();
        var host = new BatchingProvider("outer", journal);
        registry.Register(host);
        var inner = new BatchingProvider("inner", journal);
        inner.HostedIn(registry, new VfsPath("outer", "/docs/c.zip"));

        registry.Register(inner);

        var registered = Assert.IsAssignableFrom<IBatchProvider>(registry.Get("inner"));
        Assert.Same(registry.Get("outer"), registered.BatchHost);
        Assert.Equal(new HostLocation(new VfsPath("outer", "/docs"), "c.zip"),
            registry.HostLocationOf(new VfsPath("inner", "/")));
    }

    [Fact]
    public void Register_refuses_a_hosted_batching_provider_whose_BatchHost_is_not_its_hosts_provider()
    {
        var registry = new ProviderRegistry();
        var journal = new BatchJournal();
        registry.Register(new BatchingProvider("outer", journal));
        var other = new BatchingProvider("other", journal);
        registry.Register(other);
        var inner = new BatchingProvider("inner", journal);
        inner.HostedIn(registry, new VfsPath("outer", "/c.zip"));
        inner.BatchHost = other;

        Assert.Throws<InvalidOperationException>(() => registry.Register(inner));
        Assert.False(registry.TryGet("inner", out _));
    }

    [Fact]
    public void Register_refuses_a_hosted_batching_provider_with_no_BatchHost_in_a_batching_host()
    {
        var registry = new ProviderRegistry();
        var journal = new BatchJournal();
        registry.Register(new BatchingProvider("outer", journal));
        var inner = new BatchingProvider("inner", journal);
        inner.HostedIn(registry, new VfsPath("outer", "/c.zip"));
        inner.BatchHost = null;

        Assert.Throws<InvalidOperationException>(() => registry.Register(inner));
    }

    [Fact]
    public void Hosted_batching_provider_in_a_plain_host_has_no_BatchHost()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        var inner = new BatchingProvider("inner", new BatchJournal());
        inner.HostedIn(registry, new VfsPath("mem", "/a/c.zip"));

        registry.Register(inner);

        Assert.Null(((IBatchProvider)registry.Get("inner")).BatchHost);
    }

    // ---- Up ------------------------------------------------------------------------------------------

    [Fact]
    public void Up_from_a_hosted_root_leads_to_the_host_folder_with_the_container_focused()
    {
        var registry = ArchiveAndSearch();

        Assert.Equal(new HostLocation(new VfsPath("mem", "/a"), "b.zip"), registry.UpOf(new VfsPath("zip", "/")));
    }

    [Fact]
    public void Up_inside_a_hosted_provider_goes_to_the_parent()
    {
        var registry = ArchiveAndSearch();

        Assert.Equal(new HostLocation(new VfsPath("zip", "/"), "docs"), registry.UpOf(new VfsPath("zip", "/docs")));
        Assert.Equal(new HostLocation(new VfsPath("find", "/1"), "sub"), registry.UpOf(new VfsPath("find", "/1/sub")));
    }

    [Fact]
    public void Up_from_a_search_result_set_leads_to_the_searched_folder()
    {
        var registry = ArchiveAndSearch();

        Assert.Equal(new HostLocation(new VfsPath("mem", "/src"), null), registry.UpOf(new VfsPath("find", "/1")));
    }

    [Fact]
    public void Up_in_a_plain_provider_goes_to_the_parent_and_ends_at_the_root()
    {
        var registry = ArchiveAndSearch();

        Assert.Equal(new HostLocation(new VfsPath("mem", "/"), "a"), registry.UpOf(new VfsPath("mem", "/a")));
        Assert.Null(registry.UpOf(new VfsPath("mem", "/")));
    }

    private static ProviderRegistry ArchiveAndSearch()
    {
        var registry = new ProviderRegistry();
        registry.Register(new InMemoryFileSystem());
        registry.Register(HostedFileSystem.InFile("zip", registry, new VfsPath("mem", "/a/b.zip")));
        var results = new HostedFileSystem("find");
        results.SetHostLocation("/1", new HostLocation(new VfsPath("mem", "/src"), null));
        registry.Register(results);
        return registry;
    }

    // ---- Unregistered hosts, hosts in hosts ------------------------------------------------------------

    [Fact]
    public void Host_location_needs_the_paths_provider_and_persistent_location_its_hosts()
    {
        var registry = new ProviderRegistry();
        registry.Register(HostedFileSystem.InFile("zip", registry, new VfsPath("gone", "/a/b.zip")));

        Assert.Throws<KeyNotFoundException>(() => registry.HostLocationOf(new VfsPath("nope", "/")));
        Assert.Equal(new HostLocation(new VfsPath("gone", "/a"), "b.zip"),
            registry.HostLocationOf(new VfsPath("zip", "/docs")));
        Assert.Throws<KeyNotFoundException>(() => registry.PersistentLocationOf(new VfsPath("zip", "/docs")));
    }

    [Fact]
    public void Hosted_batching_provider_in_a_hosted_host_that_does_not_batch_has_no_BatchHost()
    {
        var registry = new ProviderRegistry();
        var journal = new BatchJournal();
        registry.Register(new BatchingProvider("arc", journal));
        registry.Register(HostedFileSystem.InFile("outer", registry, new VfsPath("arc", "/a.iso")));
        var inner = new BatchingProvider("inner", journal);
        inner.HostedIn(registry, new VfsPath("outer", "/c.zip"));

        registry.Register(inner);

        Assert.Null(((IBatchProvider)registry.Get("inner")).BatchHost);
        var wrong = new BatchingProvider("wrong", journal);
        wrong.HostedIn(registry, new VfsPath("outer", "/d.zip"));
        wrong.BatchHost = (IBatchProvider)registry.Get("arc");
        Assert.Throws<InvalidOperationException>(() => registry.Register(wrong));
    }

    // ---- Mounters ------------------------------------------------------------------------------------

    [Fact]
    public void Has_mounters_once_one_is_added()
    {
        var registry = new ProviderRegistry();
        Assert.False(registry.HasMounters);

        registry.AddMounter(new FuncMounter((_, _) => null));

        Assert.True(registry.HasMounters);
    }

    [Fact]
    public async Task First_mounter_that_answers_wins_and_none_gives_null()
    {
        var registry = new ProviderRegistry();
        var asked = new List<string>();
        registry.AddMounter(new FuncMounter((file, byContent) =>
        {
            asked.Add($"first {file.Name} {byContent}");
            return null;
        }));
        registry.AddMounter(new FuncMounter((file, _) =>
        {
            asked.Add($"second {file.Name}");
            return file.Extension == "zip" ? new VfsPath("zip", "/") : null;
        }));
        registry.AddMounter(new FuncMounter((file, _) =>
        {
            asked.Add($"third {file.Name}");
            return new VfsPath("never", "/");
        }));

        var mounted = await registry.TryMountAsync(File("/a/b.zip"), byContent: true, Ct);

        Assert.Equal(new VfsPath("zip", "/"), mounted);
        Assert.Equal(["first b.zip True", "second b.zip"], asked);

        var none = new ProviderRegistry();
        none.AddMounter(new FuncMounter((_, _) => null));
        Assert.Null(await none.TryMountAsync(File("/a/b.txt"), byContent: false, Ct));
        Assert.Null(await new ProviderRegistry().TryMountAsync(File("/a/b.txt"), byContent: false, Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FileEntry File(string path) =>
        new(new VfsPath("mem", path), EntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null,
            false);

    private sealed class FuncMounter(Func<FileEntry, bool, VfsPath?> mount) : IFileMounter
    {
        public Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct) =>
            Task.FromResult(mount(file, byContent));
    }
}
