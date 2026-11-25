using Tandem.Core.Themes;
using Tandem.Core.Tests.Vfs;

namespace Tandem.Core.Tests.Themes;

public sealed class ThemeCatalogTests : IDisposable
{
    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    private string Dir => temp.Combine("Themes");

    private ThemeCatalog Catalog() => new(Dir);

    private void Write(string fileName, string content)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, fileName), content);
    }

    [Fact]
    public void List_MissingFolder_OnlyBuiltIn()
    {
        var list = Catalog().List();

        var only = Assert.Single(list);
        Assert.Equal(BuiltInThemes.StoneId, only.Id);
        Assert.Null(only.Path);
        Assert.Null(only.Error);
    }

    [Fact]
    public void List_FilesFollowBuiltIn_OrderedByName()
    {
        Write("b.json", """{ "name": "Zeta" }""");
        Write("a.json", """{ "name": "Alpha" }""");
        Write("c.txt", "ignored");

        var list = Catalog().List();

        Assert.Equal(["stone", "a", "b"], list.Select(t => t.Id));
        Assert.Equal("Alpha", list[1].Name);
        Assert.Equal(Path.Combine(Dir, "a.json"), list[1].Path);
        Assert.All(list, t => Assert.Null(t.Error));
    }

    [Fact]
    public void List_BrokenFile_IsListedWithError()
    {
        Write("broken.json", "{ nope");

        var list = Catalog().List();

        var broken = Assert.Single(list, t => t.Id == "broken");
        Assert.Equal("broken", broken.Name);
        Assert.False(string.IsNullOrEmpty(broken.Error));
        Assert.Equal(BuiltInThemes.StoneId, list[0].Id);
    }

    [Fact]
    public void Load_BrokenFile_FallsBackToBuiltInWithError()
    {
        Write("broken.json", "[]");

        var theme = Catalog().Load("broken", out var error);

        Assert.Same(BuiltInThemes.Stone, theme);
        Assert.NotNull(error);
        Assert.Contains("broken.json", error);
    }

    [Fact]
    public void Load_Null_BuiltInWithoutError()
    {
        var theme = Catalog().Load(null, out var error);

        Assert.Same(BuiltInThemes.Stone, theme);
        Assert.Null(error);
    }

    [Fact]
    public void Load_Unknown_BuiltInWithError()
    {
        var theme = Catalog().Load("unknown", out var error);

        Assert.Same(BuiltInThemes.Stone, theme);
        Assert.NotNull(error);
        Assert.Contains("unknown", error);
    }

    [Fact]
    public void Load_StoneId_WithoutFile_NoError()
    {
        var theme = Catalog().Load("Stone", out var error);

        Assert.Same(BuiltInThemes.Stone, theme);
        Assert.Null(error);
    }

    [Fact]
    public void FileNamedStone_ReplacesBuiltInEntry()
    {
        Write("stone.json", """{ "name": "My Stone" }""");

        var list = Catalog().List();

        var stone = Assert.Single(list);
        Assert.Equal("stone", stone.Id);
        Assert.Equal("My Stone", stone.Name);
        Assert.NotNull(stone.Path);
        Assert.Equal("My Stone", Catalog().Load("stone", out _).Name);
    }

    [Fact]
    public void BrokenStoneFile_KeepsBuiltInEntry_AndShowsError()
    {
        Write("stone.json", "{ nope");

        var list = Catalog().List();

        Assert.Equal(2, list.Count);
        Assert.Null(list[0].Path);
        Assert.NotNull(list[1].Error);
    }

    [Fact]
    public void Load_IdIsCaseInsensitive()
    {
        Write("Dusk.json", """{ "name": "Dusk" }""");

        var theme = Catalog().Load("dUsK", out var error);

        Assert.Null(error);
        Assert.Equal("Dusk", theme.Name);
        Assert.Equal("Dusk", theme.Id);
    }

    [Fact]
    public void Load_MergesFallbackTokens()
    {
        Write("mine.json", """
            { "name": "Mine", "color": { "tokens": [ { "name": "surface", "value": "#010203" } ] } }
            """);

        var theme = Catalog().Load("mine", out var error);

        Assert.Null(error);
        Assert.Equal(new ThemeColor("#010203", "#010203"), theme.Colors["surface"]);
        Assert.Equal(BuiltInThemes.Stone.Colors.Count, theme.Colors.Count);
        Assert.Equal(BuiltInThemes.Stone.Sizes["row-height"], theme.Sizes["row-height"]);
        Assert.Equal(BuiltInThemes.Stone.TextStyles["item"], theme.TextStyles["item"]);
    }

    [Fact]
    public void DirectoryIn_AppendsThemes()
    {
        Assert.Equal(Path.Combine("state", "Themes"), ThemeCatalog.DirectoryIn("state"));
    }
}
