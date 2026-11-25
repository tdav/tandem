namespace Tandem.Core.Themes;

/// <summary>The themes that ship with the app: "Stone", the Tandem design system (Themes/stone.json, embedded).</summary>
public static class BuiltInThemes
{
    public const string StoneId = "stone";

    private static readonly Lazy<ThemeDefinition> StoneTheme = new(() =>
    {
        using var stream = typeof(BuiltInThemes).Assembly.GetManifestResourceStream("Tandem.Core.Themes.stone.json")
                           ?? throw new InvalidOperationException("The built-in theme stone.json is not embedded.");
        using var reader = new StreamReader(stream);
        return ThemeParser.Parse(reader.ReadToEnd(), StoneId);
    });

    /// <summary>The design system's own theme; every other theme falls back to it for tokens it does not set.</summary>
    public static ThemeDefinition Stone => StoneTheme.Value;
}
