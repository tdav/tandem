namespace Tandem.Core.Themes;

/// <summary>A colour token's value in the light and the dark variant of a theme, as "#rrggbb" or "#aarrggbb".
/// A theme that gives one value for a token uses it in both variants.</summary>
public sealed record ThemeColor(string Light, string Dark);

/// <summary>A text style of a theme: the font family token it uses ("ui" or "mono"), size and line height in
/// pixels, and the weight (100–900).</summary>
public sealed record ThemeTextStyle(string Family, double FontSize, double LineHeight, int FontWeight);

/// <summary>
/// A theme: the colours, fonts and sizes the whole UI is drawn with. The file format is the design system's
/// <c>tokens.json</c> (see <see cref="ThemeParser"/>); token names are the design system's ("surface", "ink",
/// "row-height", "radius-button"…).
/// </summary>
/// <param name="Id">The file name without ".json"; <see cref="BuiltInThemes.StoneId"/> for the built-in one.</param>
/// <param name="Name">The name shown to the user (the file's "name", else the id).</param>
/// <param name="SourcePath">The file it was read from; null for a built-in theme.</param>
/// <param name="Colors">Colour tokens by name.</param>
/// <param name="FontFamilies">Font family lists by family token ("ui", "mono"), comma-separated, CSS-like.</param>
/// <param name="TextStyles">Text styles by name ("item", "data", "caption", "code", "dialog-text"…).</param>
/// <param name="Sizes">Size, spacing and radius tokens by name ("row-height", "space-2", "radius-button"…), in
/// pixels.</param>
public sealed record ThemeDefinition(
    string Id,
    string Name,
    string? SourcePath,
    IReadOnlyDictionary<string, ThemeColor> Colors,
    IReadOnlyDictionary<string, string> FontFamilies,
    IReadOnlyDictionary<string, ThemeTextStyle> TextStyles,
    IReadOnlyDictionary<string, double> Sizes)
{
    /// <summary>This theme with every token it lacks taken from <paramref name="fallback"/>, so a theme file may
    /// set only the tokens it changes.</summary>
    public ThemeDefinition WithFallback(ThemeDefinition fallback) => this with
    {
        Colors = Merge(Colors, fallback.Colors),
        FontFamilies = Merge(FontFamilies, fallback.FontFamilies),
        TextStyles = Merge(TextStyles, fallback.TextStyles),
        Sizes = Merge(Sizes, fallback.Sizes),
    };

    private static IReadOnlyDictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> own,
        IReadOnlyDictionary<string, T> fallback)
    {
        var merged = new Dictionary<string, T>(fallback, StringComparer.Ordinal);
        foreach (var (key, value) in own) merged[key] = value;
        return merged;
    }
}
