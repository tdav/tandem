using System.Runtime.Versioning;
using Tandem.Core.Tests.Vfs;
using Tandem.Net.Connections;

namespace Tandem.Net.Tests.Connections;

[SupportedOSPlatform("macos")]
public sealed class TrustStoreTests
{
    [Fact]
    public void Remembers_keys_by_host_port_and_algorithm_and_certificates_by_host_and_port()
    {
        using var directory = new TempDir();
        var trust = new TrustStore(directory.Path);
        var ed = new HostKeyInfo("ssh-ed25519", "SHA256:ed");
        var rsa = new HostKeyInfo("ssh-rsa", "SHA256:rsa");
        trust.TrustHostKey("example.test:22", ed);
        trust.TrustHostKey("example.test:22", rsa);
        trust.TrustCertificate("example.test:21", "SHA256:cert");

        var reloaded = new TrustStore(directory.Path);
        Assert.Equal(ed, reloaded.HostKeyOf("EXAMPLE.test:22", ed.Algorithm));
        Assert.Equal(rsa, reloaded.HostKeyOf("example.test:22", rsa.Algorithm));
        Assert.Null(reloaded.HostKeyOf("example.test:2222", ed.Algorithm));
        Assert.Equal("SHA256:cert", reloaded.CertificateOf("example.test:21"));
        Assert.Null(reloaded.CertificateOf("example.test:990"));
    }

    [Fact]
    public void Corrupt_file_trusts_nothing()
    {
        using var directory = new TempDir();
        File.WriteAllText(directory.Combine(TrustStore.FileName), "{\"Version\":1,\"Keys\":[null],\"Certificates\":[]}");
        var store = new TrustStore(directory.Path);
        Assert.Null(store.HostKeyOf("example.test:22", "ssh-ed25519"));
        Assert.Null(store.CertificateOf("example.test:21"));
        Assert.True(File.Exists(directory.Combine(TrustStore.FileName + ".bad")));
    }
}
