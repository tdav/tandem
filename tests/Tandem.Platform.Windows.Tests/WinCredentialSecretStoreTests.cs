using System.ComponentModel;
using System.Runtime.Versioning;
using Tandem.Core.Platform;

namespace Tandem.Platform.Windows.Tests;

/// <summary>These tests use the real Credential Manager, each under a key of its own that it deletes at the
/// end.</summary>
[SupportedOSPlatform("windows")]
public sealed class WinCredentialSecretStoreTests
{
    private const int ErrorNoSuchLogonSession = 1312;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task Set_get_replace_and_delete_round_trip() => RoundTripAsync("first", "second");

    [Fact]
    public Task A_unicode_secret_survives() => RoundTripAsync("пароль 🔑", "яблоко");

    [Fact]
    public async Task A_missing_secret_reads_as_null_and_deletes_quietly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var store = new WinCredentialSecretStore();
        var key = UniqueKey();

        Assert.Null(await store.GetAsync(key, Ct));
        await store.DeleteAsync(key, Ct);
    }

    [Fact]
    public async Task An_empty_key_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var store = new WinCredentialSecretStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("", "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("", Ct));
    }

    [Fact]
    public void The_target_name_is_prefixed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        Assert.Equal("Tandem/vault-key", WinCredentialSecretStore.TargetFor("vault-key"));
    }

    private static async Task RoundTripAsync(string first, string second)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var store = new WinCredentialSecretStore();
        var key = UniqueKey();
        try
        {
            try
            {
                await store.SetAsync(key, first, Ct);
            }
            catch (SecretStoreException e) when (e.InnerException is Win32Exception { NativeErrorCode: ErrorNoSuchLogonSession })
            {
                Assert.Skip("This session has no Credential Manager (no logon session).");
            }
            Assert.Equal(first, await store.GetAsync(key, Ct));

            await store.SetAsync(key, second, Ct);
            Assert.Equal(second, await store.GetAsync(key, Ct));

            await store.DeleteAsync(key, Ct);
            Assert.Null(await store.GetAsync(key, Ct));
        }
        finally
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
    }

    private static string UniqueKey() => $"tests/{Guid.NewGuid():N}";
}
