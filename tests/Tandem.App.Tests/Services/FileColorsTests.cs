using Tandem.App.Services;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Services;

public class FileColorsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static FileEntry File(string name) =>
        new(VfsPath.Local("/f/" + name), EntryKind.File, 1, T0, T0, null, false);

    private static FileEntry Folder(string name) =>
        new(VfsPath.Local("/f/" + name), EntryKind.Directory, 0, T0, T0, null, false);

    [Fact]
    public void First_matching_colour_rule_wins_and_folders_have_none()
    {
        var colors = new FileColors([new FileColorRule("*.jpg;*.png", "Green"), new FileColorRule("photo*", "Red"),
            new FileColorRule("*", "Gray")]);

        Assert.Equal("Green", colors.ColorNameFor(File("photo.jpg")));
        Assert.Equal("Red", colors.ColorNameFor(File("photo.gif")));
        Assert.Equal("Gray", colors.ColorNameFor(File("notes.txt")));
        Assert.Null(colors.ColorNameFor(Folder("pictures.jpg")));
        var folderLink = new FileEntry(VfsPath.Local("/f/link.jpg"), EntryKind.Symlink, 0, T0, T0, null, false,
            "/somewhere", LinkPointsToDirectory: true);
        Assert.Null(colors.ColorNameFor(folderLink));
        var fileLink = folderLink with { LinkPointsToDirectory = false };
        Assert.Equal("Green", colors.ColorNameFor(fileLink));
    }

    [Fact]
    public void No_rule_gives_no_colour()
    {
        Assert.Null(new FileColors([]).ColorNameFor(File("a.txt")));
        Assert.Null(new FileColors([new FileColorRule("*.md", "Blue")]).ColorNameFor(File("a.txt")));
    }

    [Fact]
    public void Mask_matching_ignores_case_and_normalization()
    {
        var colors = new FileColors([new FileColorRule("*.JPG", "Teal"), new FileColorRule("café*", "Purple")]);

        Assert.Equal("Teal", colors.ColorNameFor(File("holiday.jpg")));
        Assert.Equal("Purple", colors.ColorNameFor(File("Café menu.txt"))); // decomposed é
    }

    [Fact]
    public void Resource_key_is_the_palette_key()
    {
        Assert.Equal("Tandem.FileColor.Red", FileColors.ResourceKey("Red"));
    }
}
