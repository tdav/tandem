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
        // The provider's flag (dot-name, UF_HIDDEN, the Windows Hidden attribute) is enough without a dot.
        Assert.True(Row("/h/Thumbs", kind, hidden: true).IsHidden);
        Assert.False(Row("/h/docs", kind, hidden: false).IsHidden);
    }

    [Theory]
    [InlineData(EntryKind.Directory, "/h/.git")]
    [InlineData(EntryKind.Directory, "/h/.vs")]
    [InlineData(EntryKind.Directory, "/h/.claude")]
    [InlineData(EntryKind.File, "/h/.gitignore")]
    public void Dot_name_row_is_hidden_without_the_hidden_attribute(EntryKind kind, string path) =>
        // The Windows provider reports only the Hidden attribute; a dot-name still gets the hidden icon.
        Assert.True(Row(path, kind, hidden: false).IsHidden);

    [Fact]
    public void A_dot_inside_the_name_does_not_hide_the_row() =>
        Assert.False(Row("/h/archive.tar.gz", EntryKind.File, hidden: false).IsHidden);

    [Fact]
    public void Parent_link_is_not_hidden() => Assert.False(EntryRowViewModel.CreateParentLink().IsHidden);
}
