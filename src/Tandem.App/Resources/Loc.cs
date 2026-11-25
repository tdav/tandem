using System.Globalization;
using System.Resources;

namespace Tandem.App.Resources;

/// <summary>The form a noun takes after a number (<see cref="Loc.PluralFormOf"/>).</summary>
public enum PluralForm { One, Few, Many }

/// <summary>
/// The interface's text, from <c>Strings.resx</c> (English, the fallback) and <c>Strings.ru.resx</c> (Russian).
/// The language is chosen once at startup, before the window is created; switching it later does not update
/// text already shown. Safe to call from any thread.
/// </summary>
public static class Loc
{
    private const string English = "en";
    private const string Russian = "ru";

    private static readonly ResourceManager Strings = new("Tandem.App.Resources.Strings", typeof(Loc).Assembly);

    private static volatile CultureInfo _culture = Resolve(null);

    /// <summary>The current language: "en" or "ru".</summary>
    public static string Language => _culture.Name;

    /// <summary>The culture of the current language, for formatting numbers the way the text expects.</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>Chooses the language: "ru", "en" (a region suffix such as "ru-RU" is accepted), or null for the
    /// system language. Anything that is not a supported language selects English.</summary>
    public static void SetLanguage(string? code) => _culture = Resolve(code);

    /// <summary>The text for <paramref name="key"/> in the current language; the key itself when there is none.</summary>
    /// <remarks>On Windows a "{key}_Windows" text, where there is one, wins (Explorer instead of Finder).</remarks>
    public static string Get(string key) =>
        (OperatingSystem.IsWindows() ? Strings.GetString(key + "_Windows", _culture) : null)
        ?? Strings.GetString(key, _culture) ?? key;

    /// <summary>The text for <paramref name="key"/> with its placeholders filled in from <paramref name="args"/>.</summary>
    public static string Format(string key, params object[] args)
    {
        var culture = _culture;
        return string.Format(culture, Get(key), args);
    }

    /// <summary>The text for an enum member, stored under the key <c>&lt;EnumName&gt;_&lt;Member&gt;</c>.</summary>
    public static string Enum<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Get($"{typeof(TEnum).Name}_{value}");

    /// <summary>Which plural form a count takes in the current language. Russian: 1, 21, 101 → One;
    /// 2–4, 22–24 → Few; 0, 5–20, 25–30, 111–114 → Many. English: 1 → One, anything else → Many.</summary>
    public static PluralForm PluralFormOf(long n)
    {
        n = Math.Abs(n);
        if (_culture.Name != Russian) return n == 1 ? PluralForm.One : PluralForm.Many;
        var (lastDigit, lastTwo) = (n % 10, n % 100);
        if (lastDigit == 1 && lastTwo != 11) return PluralForm.One;
        if (lastDigit is >= 2 and <= 4 && lastTwo is < 12 or > 14) return PluralForm.Few;
        return PluralForm.Many;
    }

    /// <summary>A number with its noun, e.g. "3 объекта": the text under <c>{keyBase}_One</c>, <c>_Few</c> or
    /// <c>_Many</c> (see <see cref="PluralFormOf"/>), with <paramref name="n"/> as its <c>{0}</c>. Both languages
    /// have all three keys; English uses the same text for Few and Many.</summary>
    public static string Count(string keyBase, long n) => Format($"{keyBase}_{PluralFormOf(n)}", n);

    private static CultureInfo Resolve(string? code)
    {
        var language = code is null
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : code.Split('-', '_')[0];
        return CultureInfo.GetCultureInfo(
            string.Equals(language, Russian, StringComparison.OrdinalIgnoreCase) ? Russian : English);
    }
}
