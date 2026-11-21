using Tandem.Core.Vfs;

namespace Tandem.Core.Tests.Vfs;

public sealed class DroppingProviderTests
{
    [Fact]
    public async Task Reads_lists_only_paths_that_opened()
    {
        var remote = new DroppingProvider();
        remote.Inner.AddFile("/a.txt");
        var ct = TestContext.Current.CancellationToken;

        await using (await remote.OpenReadAsync(new VfsPath("drop", "/a.txt"), ct)) { }
        await Assert.ThrowsAsync<VfsNotFoundException>(() => remote.OpenReadAsync(new VfsPath("drop", "/missing"), ct));

        Assert.Equal([new VfsPath("drop", "/a.txt")], remote.Reads);
    }
}
