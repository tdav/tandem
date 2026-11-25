using Tandem.App.ViewModels;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

public class EntryRowViewModelTests
{
    private static EntryRowViewModel Row(string path, EntryKind kind, bool hidden) =>
        new(new FileEntry(new VfsPath("mem", path), kind, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            Mode: null, IsHidden: hidden));

    [Theory]
    [InlineData(EntryKind.Directory)]
    [InlineData(EntryKind.File)]
    public void Row_is_hidden_when_the_provider_says_so(EntryKind kind)
    {
        Assert.True(Row("/h/.cache", kind, hidden: true).IsHidden);
        // The provider decides (dot-name, UF_HIDDEN, the Windows Hidden attribute), not the row's name.
        Assert.True(Row("/h/Thumbs", kind, hidden: true).IsHidden);
        Assert.False(Row("/h/.dot", kind, hidden: false).IsHidden);
        Assert.False(Row("/h/docs", kind, hidden: false).IsHidden);
    }

    [Fact]
    public void Parent_link_is_not_hidden() => Assert.False(EntryRowViewModel.CreateParentLink().IsHidden);
}
