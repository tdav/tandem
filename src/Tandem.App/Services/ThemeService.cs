using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Tandem.Core.Themes;

namespace Tandem.App.Services;

/// <summary>
/// Puts a <see cref="ThemeDefinition"/> on screen: its colour tokens into the app's light and dark theme
/// dictionaries (as <c>Tandem.Color.*</c> colours, <c>Tandem.*</c> brushes, the views' own keys and Fluent's control
/// brushes), the Fluent palette, its font families, text styles and sizes. Which variant shows (light, dark, the
/// system's) stays <see cref="App.ApplyTheme"/>'s choice. Views refer to the resources with DynamicResource, so a
/// theme applied while windows are open changes them at once.
/// </summary>
public static class ThemeService
{
    /// <summary>The theme on screen; the built-in one until another is applied.</summary>
    public static ThemeDefinition Current { get; private set; } = BuiltInThemes.Stone;

    /// <summary>Raised after a theme has been applied.</summary>
    public static event EventHandler? Applied;

    /// <summary>Applies <paramref name="theme"/> to <see cref="Avalonia.Application.Current"/>.</summary>
    public static void Apply(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (Application.Current is { } app)
        {
            // A theme file may set only some tokens; the rest are the built-in theme's.
            var complete = theme.WithFallback(BuiltInThemes.Stone);
            var resources = app.Resources;
            WriteColors(resources, ThemeVariant.Light, complete, dark: false);
            WriteColors(resources, ThemeVariant.Dark, complete, dark: true);
            WritePalette(app, ThemeVariant.Light, complete, dark: false);
            WritePalette(app, ThemeVariant.Dark, complete, dark: true);
            WriteFonts(resources, complete);
            WriteSizes(resources, complete);
        }

        Current = theme;
        Applied?.Invoke(null, EventArgs.Empty);
    }

    private static void WriteColors(IResourceDictionary resources, ThemeVariant variant, ThemeDefinition theme,
        bool dark)
    {
        if (!resources.ThemeDictionaries.TryGetValue(variant, out var provider) ||
            provider is not IResourceDictionary target)
        {
            var created = new ResourceDictionary();
            resources.ThemeDictionaries[variant] = created;
            target = created;
        }

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var (token, value) in theme.Colors)
            colors[token] = Color.Parse(dark ? value.Dark : value.Light);

        // Each entry gets a new object, so every DynamicResource on the key is told it changed.
        foreach (var (token, color) in colors)
        {
            var name = ThemeResourceMap.Pascal(token);
            target["Tandem.Color." + name] = color;
            target["Tandem." + name] = new SolidColorBrush(color);
        }

        foreach (var (key, token) in ThemeResourceMap.Semantic.Concat(ThemeResourceMap.FluentBrushes))
            if (colors.TryGetValue(token, out var color))
                target[key] = new SolidColorBrush(color);

        foreach (var (key, token) in ThemeResourceMap.FluentColors)
            if (colors.TryGetValue(token, out var color))
                target[key] = color;

        var transparent = new SolidColorBrush(dark ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(0, 255, 255, 255));
        var box = Geometry.Parse(ThemeResourceMap.IconBox);
        foreach (var (key, token, geometry) in ThemeResourceMap.Icons)
        {
            if (!colors.TryGetValue(token, out var color)) continue;
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing { Brush = transparent, Geometry = box });
            group.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(color), Geometry = Geometry.Parse(geometry) });
            target[key] = new DrawingImage(group);
        }
    }

    /// <summary>Fluent's own palette: its accent is the input colour (focus, not the navigation accent), the window
    /// background the surface, the main text the ink.</summary>
    private static void WritePalette(Application app, ThemeVariant variant, ThemeDefinition theme, bool dark)
    {
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent) return;
        if (!fluent.Palettes.TryGetValue(variant, out var palette))
        {
            palette = new ColorPaletteResources();
            fluent.Palettes[variant] = palette;
        }

        Color? Token(string name) => theme.Colors.TryGetValue(name, out var value)
            ? Color.Parse(dark ? value.Dark : value.Light)
            : null;

        if (Token("focus") is { } accent) palette.Accent = accent;
        if (Token("surface") is { } region) palette.RegionColor = region;
        if (Token("ink") is { } baseHigh) palette.BaseHigh = baseHigh;
    }

    private static void WriteFonts(IResourceDictionary resources, ThemeDefinition theme)
    {
        var ui = FontFamilyOf(theme, "ui");
        var mono = FontFamilyOf(theme, "mono");
        resources["Tandem.Font.Ui"] = ui;
        resources["Tandem.Font.Mono"] = mono;
        resources["ContentControlThemeFontFamily"] = ui;
        if (theme.TextStyles.TryGetValue("data", out var data))
            resources["ControlContentThemeFontSize"] = data.FontSize;

        foreach (var (name, style) in theme.TextStyles)
        {
            var pascal = ThemeResourceMap.Pascal(name);
            resources["Tandem.FontSize." + pascal] = style.FontSize;
            resources["Tandem.LineHeight." + pascal] = style.LineHeight;
            resources["Tandem.FontWeight." + pascal] = (FontWeight)style.FontWeight;
        }
    }

    private static void WriteSizes(IResourceDictionary resources, ThemeDefinition theme)
    {
        foreach (var (name, value) in theme.Sizes)
        {
            var pascal = ThemeResourceMap.Pascal(name);
            resources["Tandem.Size." + pascal] = value;
            if (name.StartsWith("radius-", StringComparison.Ordinal))
                resources["Tandem.CornerRadius." + pascal] = new CornerRadius(value);
        }

        if (theme.Sizes.TryGetValue("radius-control", out var control))
            resources["ControlCornerRadius"] = new CornerRadius(control);
        if (theme.Sizes.TryGetValue("radius-window", out var window))
            resources["OverlayCornerRadius"] = new CornerRadius(window);
    }

    /// <summary>The font of a family token: its CSS-like list without quotes, in order, with the generic names
    /// replaced by concrete families (<see cref="ThemeResourceMap.GenericFamilies"/>).</summary>
    internal static FontFamily FontFamilyOf(ThemeDefinition theme, string familyToken)
    {
        theme.FontFamilies.TryGetValue(familyToken, out var list);
        return new FontFamily(FontFamilyList(list));
    }

    /// <summary>A CSS-like family list as Avalonia's comma-separated one; the platform's default font when nothing
    /// remains.</summary>
    internal static string FontFamilyList(string? cssList)
    {
        var names = new List<string>();
        foreach (var raw in (cssList ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim('"', '\'').Trim();
            if (name.Length == 0) continue;
            var expanded = ThemeResourceMap.GenericFamilies.TryGetValue(name, out var concrete) ? concrete : [name];
            foreach (var family in expanded)
                if (!names.Contains(family, StringComparer.OrdinalIgnoreCase))
                    names.Add(family);
        }

        return names.Count == 0 ? FontFamily.DefaultFontFamilyName : string.Join(", ", names);
    }
}
