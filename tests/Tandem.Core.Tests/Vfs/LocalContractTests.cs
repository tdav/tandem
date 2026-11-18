using System.Runtime.Versioning;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.Core.Tests.Vfs;

[SupportedOSPlatform("macos")]
public sealed class LocalContractTests : ProviderContractTests, IDisposable
{
    private readonly TempDir _temp = new();

    protected override Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync() =>
        Task.FromResult<(IFileSystemProvider, VfsPath)>(
            (new LocalFileSystemProvider(new UnrestrictedAccessBroker()), _temp.Root));

    public void Dispose() => _temp.Dispose();
}
