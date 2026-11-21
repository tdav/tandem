namespace Tandem.Core.Tests.Platform;

public sealed class FakeSecretStoreTests
{
    [Fact]
    public async Task Cancelled_call_is_recorded_before_it_is_cancelled()
    {
        var store = new FakeSecretStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync("k1", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetAsync("k2", "s", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync("k3", cts.Token));

        Assert.Equal([("Get", "k1"), ("Set", "k2"), ("Delete", "k3")], store.Calls);
        Assert.Empty(store.Secrets);
    }
}
