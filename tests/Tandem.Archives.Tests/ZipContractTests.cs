using System.Runtime.Versioning;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using SysZip = System.IO.Compression;

namespace Tandem.Archives.Tests;

/// <summary>A zip holds to the contract every provider shares; the facts about links and permissions are skipped
/// (a zip takes neither), and times are kept to the zip's two seconds.</summary>
[SupportedOSPlatform("macos")]
public sealed class ZipContractTests : ProviderContractTests, IDisposable
{
    private readonly CountingHostProvider _host = new();
    private readonly ProviderRegistry _providers = new();
    private readonly TempDir _tempDir = new();
    private readonly TempFileStore _temp;
    private ArchiveProvider? _archive;

    public ZipContractTests()
    {
        _temp = new TempFileStore(_tempDir.Combine("store"));
        _providers.Register(_host);
    }

    protected override TimeSpan TimeResolution => TimeSpan.FromSeconds(2);

    public void Dispose()
    {
        _archive?.Dispose();
        _temp.Dispose();
        _tempDir.Dispose();
    }

    protected override async Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync()
    {
        using var buffer = new MemoryStream();
        using (var zip = new SysZip.ZipArchive(buffer, SysZip.ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("work/");
        _host.Files.AddFile("/arc/contract.zip", buffer.ToArray());
        _archive = await ArchiveProvider.OpenAsync("arc", _providers, new VfsPath("mem", "/arc/contract.zip"), _temp,
            null, prompt: null, Ct);
        _providers.Register(_archive);
        return (_archive, new VfsPath("arc", "/work"));
    }
}
