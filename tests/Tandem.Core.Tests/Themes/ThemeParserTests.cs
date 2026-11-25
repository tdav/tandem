using Tandem.Core.Themes;

namespace Tandem.Core.Tests.Themes;

public sealed class ThemeParserTests
{
    private static ThemeDefinition Parse(string json) => ThemeParser.Parse(json, "test");

    [Fact]
    public void Stone_Parses_WithDesignSystemTokens()
    {
        var stone = BuiltInThemes.Stone;

        Assert.Equal(19, stone.Colors.Count);
        Assert.Equal(new ThemeColor("#bbb8b0", "#2a2927"), stone.Colors["surface"]);
        Assert.Equal(new ThemeTextStyle("ui", 14, 22, 600), stone.TextStyles["item"]);
        Assert.Equal(22, stone.Sizes["row-height"]);
        Assert.Equal(8, stone.Sizes["radius-window"]);
        Assert.True(stone.FontFamilies.ContainsKey("ui"));
        Assert.True(stone.FontFamilies.ContainsKey("mono"));
        Assert.Null(stone.SourcePath);
        Assert.Equal(BuiltInThemes.StoneId, stone.Id);
    }

    [Fact]
    public void Color_SingleString_AppliesToBothVariants()
    {
        var theme = Parse("""{ "color": { "tokens": [ { "name": "on-accent", "value": "#ffffff" } ] } }""");

        Assert.Equal(new ThemeColor("#ffffff", "#ffffff"), theme.Colors["on-accent"]);
    }

    [Fact]
    public void Color_OnlyLight_UsedForBoth()
    {
        var theme = Parse("""{ "color": { "tokens": [ { "name": "ink", "value": { "light": "#112233" } } ] } }""");

        Assert.Equal(new ThemeColor("#112233", "#112233"), theme.Colors["ink"]);
    }

    [Fact]
    public void Color_OnlyDark_UsedForBoth()
    {
        var theme = Parse("""{ "color": { "tokens": [ { "name": "ink", "value": { "dark": "#445566" } } ] } }""");

        Assert.Equal(new ThemeColor("#445566", "#445566"), theme.Colors["ink"]);
    }

    [Fact]
    public void Color_ShortForm_IsExpandedAndLowerCased()
    {
        var theme = Parse("""{ "color": { "tokens": [ { "name": "bar", "value": { "light": "#ABC", "dark": "#0f0" } } ] } }""");

        Assert.Equal(new ThemeColor("#aabbcc", "#00ff00"), theme.Colors["bar"]);
    }

    [Fact]
    public void Sizes_AcceptPxAndPlainNumbers()
    {
        var theme = Parse("""
            {
              "spacing": { "tokens": [ { "name": "space-1", "value": "4px" } ] },
              "size":    { "tokens": [ { "name": "row-height", "value": 22 } ] },
              "radius":  { "tokens": [ { "name": "radius-button", "value": "3" } ] }
            }
            """);

        Assert.Equal(4, theme.Sizes["space-1"]);
        Assert.Equal(22, theme.Sizes["row-height"]);
        Assert.Equal(3, theme.Sizes["radius-button"]);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAllowed()
    {
        var theme = Parse("""
            {
              // a comment
              "name": "Commented",
              "size": { "tokens": [ { "name": "row-height", "value": "20px", }, ], },
            }
            """);

        Assert.Equal("Commented", theme.Name);
        Assert.Equal(20, theme.Sizes["row-height"]);
    }

    [Fact]
    public void Style_InheritsFamilyFromGroup_AndOwnFamilyWins()
    {
        var theme = Parse("""
            {
              "type": {
                "families": { "ui": "Sans", "mono": "Mono" },
                "groups": [
                  { "family": "mono", "styles": [
                      { "name": "a", "fontSize": "12px", "lineHeight": "16px", "fontWeight": 500 },
                      { "name": "b", "family": "ui", "fontSize": 10 } ] }
                ]
              }
            }
            """);

        Assert.Equal(new ThemeTextStyle("mono", 12, 16, 500), theme.TextStyles["a"]);
        Assert.Equal(new ThemeTextStyle("ui", 10, 0, 400), theme.TextStyles["b"]);
        Assert.Equal("Sans", theme.FontFamilies["ui"]);
    }

    [Fact]
    public void MissingSections_AreFine()
    {
        var theme = Parse("{}");

        Assert.Empty(theme.Colors);
        Assert.Empty(theme.FontFamilies);
        Assert.Empty(theme.TextStyles);
        Assert.Empty(theme.Sizes);
    }

    [Fact]
    public void Name_FallsBackToId()
    {
        Assert.Equal("test", Parse("{}").Name);
        Assert.Equal("test", Parse("""{ "name": "   " }""").Name);
        Assert.Equal("Mine", Parse("""{ "name": " Mine " }""").Name);
    }

    [Fact]
    public void SourcePath_IsKept()
    {
        Assert.Equal("x.json", ThemeParser.Parse("{}", "x", "x.json").SourcePath);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("""{ "color": { "tokens": [ { "value": "#fff" } ] } }""")]
    [InlineData("""{ "color": { "tokens": [ { "name": "x", "value": "red" } ] } }""")]
    [InlineData("""{ "color": { "tokens": [ { "name": "x", "value": "#12" } ] } }""")]
    [InlineData("""{ "color": { "tokens": [ { "name": "x", "value": { } } ] } }""")]
    [InlineData("""{ "color": { "tokens": [ { "name": "x" } ] } }""")]
    [InlineData("""{ "size": { "tokens": [ { "name": "x", "value": "wide" } ] } }""")]
    [InlineData("""{ "size": { "tokens": [ { "name": "x" } ] } }""")]
    [InlineData("""{ "type": { "groups": [ { "styles": [ { "fontSize": "12px" } ] } ] } }""")]
    [InlineData("""{ "type": { "groups": [ { "styles": [ { "name": "x", "fontSize": "big" } ] } ] } }""")]
    public void Invalid_ThrowsThemeFormatException(string json)
    {
        Assert.Throws<ThemeFormatException>(() => Parse(json));
    }

    [Theory]
    [InlineData("#abc", "#aabbcc")]
    [InlineData("  #ABCDEF ", "#abcdef")]
    [InlineData("#80ffffff", "#80ffffff")]
    [InlineData("abc", null)]
    [InlineData("#abcd", null)]
    [InlineData("#ggg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeColor_Works(string? input, string? expected)
    {
        Assert.Equal(expected, ThemeParser.NormalizeColor(input));
    }

    [Fact]
    public void WithFallback_OwnTokensWin_MissingComeFromFallback()
    {
        var own = Parse("""
            {
              "color": { "tokens": [ { "name": "surface", "value": "#010203" } ] },
              "size": { "tokens": [ { "name": "row-height", "value": "30px" } ] }
            }
            """);

        var merged = own.WithFallback(BuiltInThemes.Stone);

        Assert.Equal(new ThemeColor("#010203", "#010203"), merged.Colors["surface"]);
        Assert.Equal(BuiltInThemes.Stone.Colors["ink"], merged.Colors["ink"]);
        Assert.Equal(30, merged.Sizes["row-height"]);
        Assert.Equal(BuiltInThemes.Stone.Sizes["radius-window"], merged.Sizes["radius-window"]);
        Assert.Equal(BuiltInThemes.Stone.TextStyles["item"], merged.TextStyles["item"]);
        Assert.Equal(BuiltInThemes.Stone.FontFamilies["mono"], merged.FontFamilies["mono"]);
        Assert.Equal("test", merged.Id);
        Assert.Equal(BuiltInThemes.Stone.Colors.Count, merged.Colors.Count);
    }
}
