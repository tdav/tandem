using System.Collections;
using System.Globalization;
using System.Resources;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tandem.App.Resources;
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
}
