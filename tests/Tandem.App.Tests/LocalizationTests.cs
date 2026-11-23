using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Tandem.App.Resources;
using Tandem.App.Views.Net;
using Tandem.Core.Operations;

namespace Tandem.App.Tests;

[Collection(LanguageCollection.Name)]
public class LocalizationTests : LanguageTestBase
{
    private static readonly ResourceManager Resources = new("Tandem.App.Resources.Strings", typeof(Loc).Assembly);

    private static Dictionary<string, string> ReadAll(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for '{culture.Name}'");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static Dictionary<string, string> English => ReadAll(CultureInfo.InvariantCulture);
    private static Dictionary<string, string> Russian => ReadAll(CultureInfo.GetCultureInfo("ru"));

    [Fact]
    public void Both_languages_have_identical_key_sets()
    {
        var en = English.Keys.Order().ToList();
        var ru = Russian.Keys.Order().ToList();
        Assert.NotEmpty(en);
        Assert.Equal(en, ru);
    }

    [Fact]
    public void No_empty_values_in_either_language()
    {
        Assert.All(English.Concat(Russian), e => Assert.False(string.IsNullOrWhiteSpace(e.Value), e.Key));
    }

    [Fact]
    public void Texts_follow_the_copy_rules()
    {
        Assert.All(English.Concat(Russian), e =>
        {
            Assert.DoesNotContain("!", e.Value);
            Assert.DoesNotContain("пожалуйста", e.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("please", e.Value, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Missing_key_returns_key()
    {
        Assert.Equal("No_Such_Key", Loc.Get("No_Such_Key"));
    }

    [Fact]
    public void Russian_is_selected_by_code()
    {
        Loc.SetLanguage("ru");
        Assert.Equal("ru", Loc.Language);
        Assert.Equal("МБ", Loc.Get("Unit_MB"));
    }

    [Fact]
    public void English_is_selected_by_code()
    {
        Loc.SetLanguage("en");
        Assert.Equal("en", Loc.Language);
        Assert.Equal("MB", Loc.Get("Unit_MB"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("xx-not-a-culture")]
    [InlineData("")]
    public void Unknown_code_falls_back_to_english(string code)
    {
        Loc.SetLanguage("ru");
        Loc.SetLanguage(code);
        Assert.Equal("en", Loc.Language);
        Assert.Equal("Cancel", Loc.Get("Button_Cancel"));
    }

    [Fact]
    public void Region_suffix_is_accepted()
    {
        Loc.SetLanguage("ru-RU");
        Assert.Equal("ru", Loc.Language);
    }

    [Fact]
    public void System_language_resolves_to_a_supported_one()
    {
        Loc.SetLanguage(null);
        Assert.Contains(Loc.Language, new[] { "en", "ru" });
    }

    [Fact]
    public void Format_fills_in_arguments()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Selected 2 of 10, 1.5 KB", Loc.Format("Panel_Status", 2, 10, "1.5 KB"));
        Loc.SetLanguage("ru");
        Assert.Equal("Выбрано 2 из 10, 1,5 КБ", Loc.Format("Panel_Status", 2, 10, "1,5 КБ"));
    }

    public static TheoryData<Type> CoreEnums => new()
    {
        typeof(RejectionReason), typeof(SkipReason), typeof(OperationKind),
        typeof(OperationState), typeof(ConflictAction), typeof(ErrorAction),
    };

    [Theory]
    [MemberData(nameof(CoreEnums))]
    public void Every_core_enum_member_has_text_in_both_languages(Type enumType)
    {
        var en = English;
        var ru = Russian;
        Assert.All(Enum.GetNames(enumType), name =>
        {
            var key = $"{enumType.Name}_{name}";
            Assert.True(en.ContainsKey(key), $"English is missing {key}");
            Assert.True(ru.ContainsKey(key), $"Russian is missing {key}");
        });
    }

    [Fact]
    public void Enum_returns_text_for_the_member()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Skip all", Loc.Enum(ErrorAction.SkipAll));
        Loc.SetLanguage("ru");
        Assert.Equal("Пропустить все", Loc.Enum(ErrorAction.SkipAll));
    }

    [Fact]
    public void Get_is_safe_from_many_threads()
    {
        Loc.SetLanguage("ru");
        var results = new string[64];
        Parallel.For(0, results.Length, i => results[i] = Loc.Get("Button_Copy"));
        Assert.All(results, r => Assert.Equal("Копировать", r));
    }

    [Fact]
    public void Tr_extension_provides_the_text()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Copy", new TrExtension { Key = "Button_Copy" }.ProvideValue(null));
    }

    [AvaloniaFact]
    public void Tr_extension_works_in_markup()
    {
        Loc.SetLanguage("ru");
        var probe = new LocProbe();
        Assert.Equal("Отменить", probe.FindControl<TextBlock>("Text")!.Text);
    }

    private static string ResourceFile(string name, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Tandem.App", "Resources", name));

    [Fact]
    public void Stage_3_section_markers_are_present_in_both_resource_files()
    {
        foreach (var file in new[] { "Strings.resx", "Strings.ru.resx" }) AssertSectionMarkers(file);
    }

    private static void AssertSectionMarkers(string file)
    {
        string[] markers =
        [
            "<!-- stage3:tools-a begin (Rename_*, Checksum_*, FolderSize_*) -->",
            "<!-- stage3:tools-a end -->",
            "<!-- stage3:tools-b begin (CompareDirs_*, Compare_*, Sync_*) -->",
            "<!-- stage3:tools-b end -->",
            "<!-- stage3:net begin (Net_*, Connection_*, RemoteEdit_*) -->",
            "<!-- stage3:net end -->",
            "<!-- stage3:net-ssh begin (Console_*, Terminal_*) -->",
            "<!-- stage3:net-ssh end -->",
        ];
        var lines = File.ReadAllLines(ResourceFile(file)).Select(l => l.Trim()).ToList();

        var positions = markers.Select(m => lines.IndexOf(m)).ToList();
        Assert.All(positions.Zip(markers), p => Assert.True(p.First >= 0, $"{file} lacks {p.Second}"));
        Assert.Equal(positions.Order(), positions); // in this order
        Assert.All(markers, m => Assert.Single(lines, m));
        Assert.Equal("</root>", lines.Last(l => l.Length > 0));
        Assert.DoesNotContain(lines.Skip(positions[^1] + 1), l => l.StartsWith("<data", StringComparison.Ordinal));
    }

    private static Color ThemeColor(string key, ThemeVariant theme)
    {
        Assert.True(Application.Current!.TryGetResource(key, theme, out var value), $"{key} is missing in {theme}");
        return Assert.IsType<SolidColorBrush>(value).Color;
    }

    /// <summary>The WCAG contrast ratio of two colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) =>
            0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [AvaloniaFact]
    public void Terminal_palette_preserves_contrast_on_the_fixed_black_background()
    {
        var window = new TerminalWindow();
        try
        {
            Color TerminalColor(string name)
            {
                Assert.True(window.TryFindResource("Tandem.Terminal" + name, ThemeVariant.Dark, out var brush));
                return Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
            }

            var background = TerminalColor("Background");
            var foreground = TerminalColor("Foreground");
            Assert.Equal(Colors.Black, background);
            Assert.True(Contrast(foreground, background) >= 4.5);
            Assert.True(Contrast(foreground, TerminalColor("Selection")) >= 4.5);
            Assert.True(Contrast(TerminalColor("Cursor"), background) >= 3);

            var ansi = Enumerable.Range(0, 16).Select(i => TerminalColor("Ansi" + i)).ToArray();
            for (var i = 1; i < ansi.Length; i++)
            {
                Assert.True(Contrast(ansi[i], background) >= 3, $"ANSI {i}: {Contrast(ansi[i], background):0.00}");
            }
            Assert.True(Contrast(ansi[7], ansi[0]) >= 4.5);
            Assert.True(Contrast(ansi[15], ansi[8]) >= 4.5);
        }
        finally { window.Close(); }
    }
}
