using System.Runtime.Versioning;
using Tandem.Core.Platform;

namespace Tandem.Platform.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStoreTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(-25300, false)]
    [InlineData(-128, true)]
    [InlineData(-25293, true)]
    [InlineData(-25308, true)]
    public void Status_codes_map_to_errors(int status, bool isError)
    {
        var error = MacKeychainSecretStore.ErrorFor(status);
        if (isError)
        {
            Assert.IsType<SecretStoreException>(error);
            Assert.Contains(status.ToString(), error.Message);
        }
        else Assert.Null(error);
    }

    [Fact]
    public async Task Empty_key_is_refused()
    {
        var store = new MacKeychainSecretStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("", TestContext.Current.CancellationToken));
    }

    [Fact]
    public Task Set_get_replace_and_delete_round_trip_in_the_real_keychain() =>
        RoundTripAsync("first", "second");

    [Fact]
    public Task Unicode_secret_survives() => RoundTripAsync("пароль 🔑", "яблоко");

    [Fact]
    public async Task Missing_secret_reads_as_null_and_deletes_quietly()
    {
        var store = CreateTestStore();
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await store.GetAsync("password", ct));
        await store.DeleteAsync("password", ct);
    }

    private static MacKeychainSecretStore CreateTestStore()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("TANDEM_TEST_KEYCHAIN") == "1",
            "Set TANDEM_TEST_KEYCHAIN=1 to opt in to testing a temporary entry in the macOS Keychain.");
        return new MacKeychainSecretStore("Tandem.Tests." + Guid.NewGuid().ToString("N"));
    }

    private static async Task RoundTripAsync(string initial, string replacement)
    {
        var store = CreateTestStore();
        var ct = TestContext.Current.CancellationToken;
        const string key = "password";
        try
        {
            await store.SetAsync(key, initial, ct);
            Assert.Equal(initial, await store.GetAsync(key, ct));
            await store.SetAsync(key, replacement, ct);
            Assert.Equal(replacement, await store.GetAsync(key, ct));
            await store.DeleteAsync(key, ct);
            Assert.Null(await store.GetAsync(key, ct));
        }
        finally
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
    }
}
