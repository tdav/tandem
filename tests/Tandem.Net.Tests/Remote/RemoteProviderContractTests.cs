using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Net.Remote;

namespace Tandem.Net.Tests.Remote;

/// <summary>The shared provider contract over <see cref="RemoteProvider"/> and a <see cref="FakeRemoteServer"/> with
/// the given features. Each test gets its own server and provider, connected before the test starts (so that the
/// capabilities are known).</summary>
public abstract class RemoteContractTestsBase : ProviderContractTests, IAsyncDisposable
{
    private readonly List<RemoteProvider> _providers = [];

    /// <summary>The server of the last <see cref="CreateAsync"/>.</summary>
    protected FakeRemoteServer Server { get; private set; } = null!;

    protected abstract RemoteFeatures Features { get; }

    protected override async Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync()
    {
        var server = new FakeRemoteServer { Features = Features };
        server.AddDirectory("/work");
        Server = server;
        var provider = RemoteFixture.Provider(server);
        _providers.Add(provider);
        await provider.ConnectAsync(Ct);
        return (provider, new VfsPath(provider.Id, "/work"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A server with every feature (as SFTP with links).</summary>
public sealed class RemoteProviderContractTests : RemoteContractTestsBase
{
    protected override RemoteFeatures Features =>
        RemoteFeatures.Symlinks | RemoteFeatures.Modes | RemoteFeatures.SetModified | RemoteFeatures.Multiplexed;
}

/// <summary>One call per connection, no links and no modes; times can be set.</summary>
public sealed class RemoteProviderPlainContractTests : RemoteContractTestsBase
{
    protected override RemoteFeatures Features => RemoteFeatures.SetModified;

    /// <summary>Without <see cref="RemoteFeatures.Symlinks"/> a link the server lists is still a link (only its target
    /// is unknown): a recursive delete removes the link itself and leaves what it points to.</summary>
    [Fact]
    public async Task A_link_is_a_link_without_the_feature_and_recursive_delete_does_not_enter_it()
    {
        var (fs, root) = await CreateAsync();
        Server.AddFile("/outside/keep.txt", "keep");
        Server.AddFile("/work/tree/f.txt", "f");
        Server.AddSymlink("/work/tree/link", "/outside");

        var link = Assert.Single(await ListAsync(fs, root.Combine("tree")), e => e.Name == "link");
        Assert.Equal(EntryKind.Symlink, link.Kind);
        Assert.Null(link.LinkTarget);
        Assert.Equal(EntryKind.Symlink, (await fs.GetEntryAsync(root.Combine("tree").Combine("link"), Ct))!.Kind);

        await fs.DeleteAsync(root.Combine("tree"), recursive: true, Ct);

        Assert.False(Server.Exists("/work/tree"));
        Assert.Equal("keep", Server.ReadAllText("/outside/keep.txt"));
        Assert.DoesNotContain(Server.Log, line => line.Contains("List /work/tree/link", StringComparison.Ordinal));
    }
}

/// <summary>A server that cannot set modified times (as FTP without <c>MFMT</c>).</summary>
public sealed class RemoteProviderTimelessContractTests : RemoteContractTestsBase
{
    protected override RemoteFeatures Features => RemoteFeatures.Symlinks | RemoteFeatures.Modes;

    protected override bool StoresModifiedTime => false;
}
