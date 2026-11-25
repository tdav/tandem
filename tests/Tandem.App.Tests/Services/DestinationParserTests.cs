using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Services;

[Collection(LanguageCollection.Name)]
public class DestinationParserTests : LanguageTestBase
{
    private const string Home = "/Users/me";

    // Directories that exist; "/dst/file.txt" is a file.
    private static readonly HashSet<string> Directories =
        ["/", "/src", "/src/sub", "/dst", "/dst/docs", "/Users", Home, Home + "/x"];

    private static readonly HashSet<string> Files = ["/dst/file.txt"];

    private static readonly VfsPath Active = VfsPath.Local("/src");
    private static readonly VfsPath Passive = VfsPath.Local("/dst");

    private static Task<DestinationKind> Probe(VfsPath path, CancellationToken ct) => Task.FromResult(
        Directories.Contains(path.Path) ? DestinationKind.Directory
        : Files.Contains(path.Path) ? DestinationKind.Other
        : DestinationKind.Missing);

    private static Task<DestinationParseResult> Parse(string text, int count = 1, string? single = "a.txt",
        VfsPath? passive = null, VfsPath? active = null) =>
        DestinationParser.ParseAsync(text, active ?? Active, passive ?? Passive, count, count == 1 ? single : null,
            Home, Probe, TestContext.Current.CancellationToken);

    // text, items, expected directory, expected new name (null = keep), expected "create directory"
    [Theory]
    [InlineData("/dst", 1, "/dst", null, false)]
    [InlineData("/dst/", 1, "/dst", null, false)]
    [InlineData("/dst", 3, "/dst", null, false)]
    [InlineData("/dst/b.txt", 1, "/dst", "b.txt", false)]
    [InlineData("/dst/a.txt", 1, "/dst", null, false)] // the default text for one item: no rename
    [InlineData("/dst/docs", 1, "/dst/docs", null, false)] // an existing folder takes the item
    [InlineData("/dst/docs", 3, "/dst/docs", null, false)]
    [InlineData("/dst/new/", 1, "/dst/new", null, true)] // trailing slash: a folder, created
    [InlineData("/dst/new/", 3, "/dst/new", null, true)]
    [InlineData("/dst/file.txt", 1, "/dst", "file.txt", false)] // an existing file: the engine asks about it
    [InlineData("sub", 1, "/src/sub", null, false)] // relative to the active panel's folder
    [InlineData("b.txt", 1, "/src", "b.txt", false)]
    [InlineData("./sub/", 3, "/src/sub", null, false)]
    [InlineData("../dst", 3, "/dst", null, false)]
    [InlineData("/dst/docs/../b.txt", 1, "/dst", "b.txt", false)]
    [InlineData("~", 3, Home, null, false)]
    [InlineData("~/x", 3, Home + "/x", null, false)]
    [InlineData("~/b.txt", 1, Home, "b.txt", false)]
    [InlineData("/dst/b.txt  ", 1, "/dst", "b.txt  ", false)] // trailing spaces are part of the name
    [InlineData("/", 3, "/", null, false)]
    [InlineData("//dst//", 3, "/dst", null, false)]
    public async Task Valid_destinations(string text, int count, string directory, string? newName, bool create)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix home paths; LocalPathTypedTests covers Windows.");
        var result = await Parse(text, count);

        Assert.Null(result.Error);
        Assert.NotNull(result.Target);
        Assert.Equal(VfsPath.Local(directory), result.Target.Directory);
        Assert.Equal(newName, result.Target.NewName);
        Assert.Equal(create, result.Target.CreateDirectory);
    }

    [Fact]
    public async Task Same_name_as_the_single_item_means_its_folder_even_if_such_a_folder_exists()
    {
        // Copying the folder "docs" to "/dst/docs" merges into the existing /dst/docs, never nests it.
        var result = await Parse("/dst/docs", single: "docs");

        Assert.Equal(VfsPath.Local("/dst"), result.Target!.Directory);
        Assert.Null(result.Target.NewName);
    }

    [Theory]
    [InlineData("", "Destination_Empty")]
    [InlineData("   ", "Destination_Empty")]
    [InlineData("/..", "Destination_Invalid")]
    [InlineData("../../..", "Destination_Invalid")]
    [InlineData("/nowhere/b.txt", "Destination_FolderMissing")]
    [InlineData("/dst/file.txt/", "Destination_NotAFolder")]
    public async Task Invalid_destinations(string text, string errorKey)
    {
        var result = await Parse(text);

        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
        Assert.StartsWith(Loc.Get(errorKey).Split('{')[0], result.Error);
    }

    [Fact]
    public async Task New_name_with_many_items_is_an_error()
    {
        var result = await Parse("/dst/b.txt", count: 3);

        Assert.Null(result.Target);
        Assert.Equal(Loc.Format("Destination_NewNameManyItems", "b.txt"), result.Error);
    }

    [Fact]
    public async Task Missing_folder_without_slash_with_many_items_is_an_error_not_a_new_folder()
    {
        var result = await Parse("/dst/new", count: 2);

        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Text_refers_to_the_passive_panels_provider()
    {
        var passive = new VfsPath("mem", "/dst");
        var result = await DestinationParser.ParseAsync("/dst", Active, passive, 1, "a.txt", Home, Probe, TestContext.Current.CancellationToken);

        Assert.Equal("mem", result.Target!.Directory.ProviderId);
    }

    [Fact]
    public async Task Tilde_is_not_expanded_outside_the_local_provider()
    {
        var passive = new VfsPath("mem", "/dst");
        var active = new VfsPath("mem", "/src");
        var result = await DestinationParser.ParseAsync("~/x/", active, passive, 2, null, Home,
            (p, _) => Task.FromResult(DestinationKind.Missing), TestContext.Current.CancellationToken);

        // "~" is an ordinary name there: relative to the active folder.
        Assert.Equal(new VfsPath("mem", "/src/~/x"), result.Target!.Directory);
    }

    [Fact]
    public async Task Relative_text_with_panels_on_different_providers_is_an_error()
    {
        var passive = new VfsPath("mem", "/dst");
        var result = await DestinationParser.ParseAsync("sub", Active, passive, 1, "a.txt", Home, Probe, TestContext.Current.CancellationToken);

        Assert.Null(result.Target);
        Assert.Equal(Loc.Get("Destination_RelativeOtherSource"), result.Error);
    }

    [Fact]
    public async Task A_failing_probe_is_reported_as_an_error_not_thrown()
    {
        var result = await DestinationParser.ParseAsync("/dst", Active, Passive, 1, "a.txt", Home,
            (_, _) => throw new VfsAccessDeniedException("no"), TestContext.Current.CancellationToken);

        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
    }
}
