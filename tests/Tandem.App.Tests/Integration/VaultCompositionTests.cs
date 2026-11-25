using System.Runtime.Versioning;
using Tandem.App.Services;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Net;
using Tandem.Net.Connections;

namespace Tandem.App.Tests.Integration;

[SupportedOSPlatform("macos")]
public sealed class VaultCompositionTests
{
    // Generated independently with Python hashlib and Swift CryptoKit and fixed salt/nonce for this test vector.
    private const string PortableFile =
        "VEFORFZMVDEAAQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobJHqLLqu62OsIbvbit58TNue/p74p7CtZc7RRlvk5aVIUsbIppuvT" +
        "w4mMISwWyWlrAABtN8wEB1X139LtdaKPScm1LIYlmbd+tuUL0A2sx5i4WSsRapR6nL8mWL6NPHwqMleZhkQ5Cqbt1gii/tMHYw4e" +
        "PIeUX1BNY3UAD1iBcdClLuQKIuQkKHj8vrQAl8JnCwWsvN4rLRejVoZoHgDXYrSQdWEV8zjUeCuszQ1XVjwVbBVcomNqEwFB6WJD" +
        "uUBlHxeBKJBvNgFFA6VPiJNy8ARxp/122KCeNzJmcF4/FRqtGPDPEBW3K+VbcnyMQDI77Nj5Q7pCbpri9rFSMigT6Q6zx91zZAgO" +
        "yYSpPZ24sckzLBasWkUYNe/vaF6j1gLOWPgPGXzDxqzlSNh1x60q0UHO0PN00QjLENwQE0It6YuPElFtL48siXKOTPWyNpzhz7xt" +
        "pFF4zMjDTbaErW+je0agM1/ziU7f9iwMWafEJRrMMjRI0gWw+GNpk97fzOHwpnsqqSXwTpxcqf7e6DwkaN8=";

    [Fact]
    public async Task Private_key_reader_rejects_oversized_files()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix home paths.");
        var providers = new ProviderRegistry();
        var local = new InMemoryFileSystem(VfsPath.LocalProviderId);
        local.AddFile("/test-home/key", new byte[1_048_577]);
        providers.Register(local);
        var read = Composition.CreateKeyReader(providers, "/test-home");

        await Assert.ThrowsAsync<IOException>(() => read("~/key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Platform_connections_unlock_the_documented_portable_format()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS folders and Keychain");
        var home = Directory.CreateTempSubdirectory("tandem-vault-format-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var configuration = Path.Combine(home.FullName, "Library", "Application Support", "Tandem");
            Directory.CreateDirectory(configuration);
            var fileName = Path.Combine(configuration, "credentials.tandemvault");
            await File.WriteAllBytesAsync(fileName, Convert.FromBase64String(PortableFile), ct);
            using var app = Composition.CreatePlatformServices(new ErrorLog(Path.Combine(home.FullName, "test.log")),
                home.FullName);
            await using var connections = Assert.IsType<NetConnections>(app.Net);
            connections.Load();

            Assert.True(connections.VaultConfigured);
            Assert.False(connections.VaultUnlocked);
            Assert.Empty(connections.Profiles);
            await connections.UnlockVaultAsync("Мастер-пароль for test only", ct);

            var profile = Assert.Single(connections.Profiles);
            Assert.Equal("Проверка", profile.Name);
            Assert.Equal("2001:db8::8", profile.Host);
            Assert.Equal(2220, profile.Port);
            Assert.Equal("demo-user", profile.User);
            Assert.Equal(NetAuth.PrivateKey, profile.Auth);
            Assert.True(await connections.HasStoredPrivateKeyAsync(profile.Id, ct));
        }
        finally { home.Delete(recursive: true); }
    }
}
