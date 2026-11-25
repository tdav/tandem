using System.Text.Json;
using Tandem.Core.Themes;
using Tandem.Core.Tests.Vfs;

namespace Tandem.Core.Tests.Themes;

public sealed class BundledThemesTests : IDisposable
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    private string Dir => temp.Combine("Themes");

    public static TheoryData<string> FileNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in BundledThemes.FileNames) data.Add(name);
        return data;
    }

    [Fact]
    public void FileNames_NotEmpty_AllJson()
    {
        Assert.NotEmpty(BundledThemes.FileNames);
        Assert.All(BundledThemes.FileNames, n => Assert.EndsWith(".json", n, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void Theme_ParsesWithName_AndKeepsEveryToken(string fileName)
    {
        var json = BundledThemes.Read(fileName);

        var theme = ThemeParser.Parse(json, Path.GetFileNameWithoutExtension(fileName));

        Assert.False(string.IsNullOrWhiteSpace(theme.Name));
        using var document = JsonDocument.Parse(json, Options);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(name.GetString()));
        Assert.Equal(RawCount(root, "color"), theme.Colors.Count);
        Assert.Equal(RawStyleCount(root), theme.TextStyles.Count);
        Assert.Equal(RawCount(root, "spacing") + RawCount(root, "size") + RawCount(root, "radius"), theme.Sizes.Count);
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void Theme_ColorTokens_AreStoneTokens(string fileName)
    {
        var theme = ThemeParser.Parse(BundledThemes.Read(fileName), "t");

        var unknown = theme.Colors.Keys.Where(k => !BuiltInThemes.Stone.Colors.ContainsKey(k)).ToList();

        Assert.Empty(unknown);
    }

    // A token a theme leaves out comes from Stone's variant on screen: a dark theme would get Stone's light
    // buttons in the app's light mode. So each bundled theme sets them all.
    [Theory]
    [MemberData(nameof(FileNames))]
    public void Theme_SetsEveryStoneColorToken(string fileName)
    {
        var theme = ThemeParser.Parse(BundledThemes.Read(fileName), "t");

        var missing = BuiltInThemes.Stone.Colors.Keys.Where(k => !theme.Colors.ContainsKey(k)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void FileNames_AreTheNotepadPlusPlusThemes()
    {
        Assert.Equal(
            ["npp-bespin.json", "npp-black-board.json", "npp-choco.json",
             "npp-dark-mode-default.json", "npp-deep-black.json", "npp-hot-fudge-sundae.json", "npp-mono-industrial.json",
             "npp-monokai.json", "npp-mossy-lawn.json", "npp-navajo.json", "npp-obsidian.json",
             "npp-plastic-code-wrap.json", "npp-ruby-blue.json", "npp-solarized.json", "npp-twilight.json",
             "npp-vibrant-ink.json", "npp-vim-dark-blue.json", "npp-zenburn.json"],
            BundledThemes.FileNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Install_EmptyFolder_CopiesAll_ThenNothing()
    {
        Directory.CreateDirectory(Dir);

        var first = BundledThemes.Install(Dir);
        var second = BundledThemes.Install(Dir);

        Assert.Equal(BundledThemes.FileNames, first);
        Assert.Empty(second);
        foreach (var fileName in BundledThemes.FileNames)
            Assert.Equal(BundledThemes.Read(fileName), File.ReadAllText(Path.Combine(Dir, fileName)));
    }

    [Fact]
    public void Install_WritesUtf8WithoutBom()
    {
        BundledThemes.Install(Dir);

        var bytes = File.ReadAllBytes(Path.Combine(Dir, BundledThemes.FileNames[0]));

        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }

    [Fact]
    public void Install_KeepsUserFile_CopiesTheRest()
    {
        var mine = BundledThemes.FileNames[0];
        Directory.CreateDirectory(Dir);
        const string userText = """{ "name": "Mine" }""";
        File.WriteAllText(Path.Combine(Dir, mine), userText);

        var copied = BundledThemes.Install(Dir);

        Assert.Equal(userText, File.ReadAllText(Path.Combine(Dir, mine)));
        Assert.DoesNotContain(mine, copied);
        Assert.Equal(BundledThemes.FileNames.Skip(1), copied);
    }

    [Fact]
    public void Install_MissingFolder_CreatesIt()
    {
        var dir = Path.Combine(Dir, "nested");

        var copied = BundledThemes.Install(dir);

        Assert.True(Directory.Exists(dir));
        Assert.Equal(BundledThemes.FileNames.Count, copied.Count);
    }

    [Fact]
    public void Install_ThenCatalog_ListsEveryBundledTheme_WithoutError()
    {
        BundledThemes.Install(Dir);

        var list = new ThemeCatalog(Dir).List();

        foreach (var fileName in BundledThemes.FileNames)
        {
            var info = Assert.Single(list, t => t.Id == Path.GetFileNameWithoutExtension(fileName));
            Assert.Null(info.Error);
        }
    }

    private static int RawCount(JsonElement root, string section) =>
        root.TryGetProperty(section, out var element) && element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array
            ? tokens.GetArrayLength()
            : 0;

    private static int RawStyleCount(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Object ||
            !type.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return 0;
        return groups.EnumerateArray()
            .Where(g => g.TryGetProperty("styles", out var s) && s.ValueKind == JsonValueKind.Array)
            .Sum(g => g.GetProperty("styles").GetArrayLength());
    }
}
