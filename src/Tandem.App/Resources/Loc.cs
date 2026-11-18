using System.Globalization;
using System.Resources;

namespace Tandem.App.Resources;

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
    public static string Get(string key) => Strings.GetString(key, _culture) ?? key;

    /// <summary>The text for <paramref name="key"/> with its placeholders filled in from <paramref name="args"/>.</summary>
    public static string Format(string key, params object[] args)
    {
        var culture = _culture;
        return string.Format(culture, Strings.GetString(key, culture) ?? key, args);
    }

    /// <summary>The text for an enum member, stored under the key <c>&lt;EnumName&gt;_&lt;Member&gt;</c>.</summary>
    public static string Enum<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Get($"{typeof(TEnum).Name}_{value}");

    private static CultureInfo Resolve(string? code)
    {
        var language = code is null
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : code.Split('-', '_')[0];
        return CultureInfo.GetCultureInfo(
            string.Equals(language, Russian, StringComparison.OrdinalIgnoreCase) ? Russian : English);
    }
}
