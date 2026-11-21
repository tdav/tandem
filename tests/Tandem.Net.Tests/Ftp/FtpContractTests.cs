using System.Runtime.Versioning;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Net.Ftp;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Sftp;

namespace Tandem.Net.Tests.Ftp;

[SupportedOSPlatform("macos")]
public class FtpContractTests : ProviderContractTests, IAsyncDisposable
{
    private readonly TempDir _directory = new();
    private TestFtpServer? _server;
    private RemoteProvider? _provider;

    protected override TimeSpan TimeResolution => TimeSpan.FromSeconds(1);
    protected virtual bool Mfmt => true;
    protected override bool StoresModifiedTime => Mfmt;

    protected override async Task<(IFileSystemProvider fs, VfsPath root)> CreateAsync()
    {
        _server = new TestFtpServer(_directory.Path) { Mfmt = Mfmt };
        var profile = new ConnectionProfile(Guid.NewGuid(), "FTP", NetProtocol.Ftp, "127.0.0.1", _server.Port,
            "user", NetAuth.Password);
        var factory = new FtpSessionFactory(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        _provider = new RemoteProvider("ftp-contract", profile,
            () => factory.Create(profile, new SftpTests.Credentials("test-password"), new SftpTests.Trust()),
            new HostLocation(VfsPath.Local("/"), null));
        await _provider.ConnectAsync(Ct);
        return (_provider, _provider.Root);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_provider is not null) await _provider.DisposeAsync();
        }
        finally
        {
            if (_server is not null) await _server.DisposeAsync();
            _directory.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

[SupportedOSPlatform("macos")]
public sealed class FtpWithoutMfmtContractTests : FtpContractTests
{
    protected override bool Mfmt => false;
}
