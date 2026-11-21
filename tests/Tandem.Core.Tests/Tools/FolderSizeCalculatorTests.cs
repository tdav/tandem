using Tandem.Core.Tools;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Tools;

public sealed class FolderSizeCalculatorTests
{
    [Fact]
    public async Task Sums_readable_tree_without_following_links()
    {
        var ct = TestContext.Current.CancellationToken;
        var fs = new InMemoryFileSystem();
        fs.AddFile("/tree/a", "abcd");
        fs.AddFile("/tree/sub/b", "abc");
        fs.AddFile("/tree/unreadable/c", "should not count");
        await fs.CreateSymlinkAsync(new("mem", "/tree/loop"), "/tree", ct);
        var link = await fs.GetEntryAsync(new("mem", "/tree/loop"), ct);
        fs.FailOn((operation, path) => operation == "Enumerate" && path.Name == "unreadable",
            () => new UnauthorizedAccessException());
        var result = await FolderSizeCalculator.ComputeAsync(fs, new("mem", "/tree"), null, ct);
        Assert.Equal(new FolderSize(7 + link!.Size, 3, 2, 1), result);
    }
}
