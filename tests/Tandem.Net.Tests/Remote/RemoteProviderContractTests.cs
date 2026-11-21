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

    protected abstract RemoteFeatures Features { get; }

    protected override async Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync()
    {
        var server = new FakeRemoteServer { Features = Features };
        server.AddDirectory("/work");
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
}

/// <summary>A server that cannot set modified times (as FTP without <c>MFMT</c>).</summary>
public sealed class RemoteProviderTimelessContractTests : RemoteContractTestsBase
{
    protected override RemoteFeatures Features => RemoteFeatures.Symlinks | RemoteFeatures.Modes;

    protected override bool StoresModifiedTime => false;
}
