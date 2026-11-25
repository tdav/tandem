using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tandem.App.Services;
using Tandem.Core.Themes;

namespace Tandem.App.Tests.Views;

// The theme is process-wide: these tests run in the language collection and put Stone back when they end.
[Collection(LanguageCollection.Name)]
public class ThemeServiceTests
{
    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    private static ThemeDefinition WithColors(params (string Token, string Light, string Dark)[] colors)
    {
        var stone = BuiltInThemes.Stone;
        var changed = new Dictionary<string, ThemeColor>(stone.Colors, StringComparer.Ordinal);
        foreach (var (token, light, dark) in colors) changed[token] = new ThemeColor(light, dark);
        return stone with { Id = "test", Name = "Test", Colors = changed };
    }

    private static object Resource(string key, ThemeVariant? variant = null)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"{key} is missing in {variant}");
        return value!;
    }

    private static Color BrushColor(string key, ThemeVariant variant) =>
        Assert.IsType<SolidColorBrush>(Resource(key, variant)).Color;

    [AvaloniaFact]
    public void Applying_a_theme_rewrites_the_tokens_the_views_keys_and_the_control_brushes_in_both_variants()
    {
        try
        {
            ThemeService.Apply(WithColors(("surface", "#102030", "#405060"), ("bar", "#112233", "#445566")));

            Assert.Equal("test", ThemeService.Current.Id);
            foreach (var (variant, surface, bar) in new[]
                     {
                         (ThemeVariant.Light, Color.Parse("#102030"), Color.Parse("#112233")),
                         (ThemeVariant.Dark, Color.Parse("#405060"), Color.Parse("#445566")),
                     })
            {
                Assert.Equal(surface, Assert.IsType<Color>(Resource("Tandem.Color.Surface", variant)));
                Assert.Equal(surface, BrushColor("Tandem.Surface", variant));
                Assert.Equal(surface, BrushColor("Tandem.TabActiveBackground", variant));
                Assert.Equal(surface, BrushColor("SystemControlBackgroundChromeMediumLowBrush", variant));
                Assert.Equal(bar, BrushColor("Tandem.Bar", variant));
                Assert.Equal(bar, BrushColor("Tandem.BarBackground", variant));
                Assert.Equal(bar, BrushColor("ButtonBackground", variant));
                Assert.IsType<DrawingImage>(Resource("Tandem.FolderIcon", variant));
            }
        }
        finally
        {
            ThemeService.Apply(BuiltInThemes.Stone);
        }

        Assert.Equal(Color.Parse("#bbb8b0"), BrushColor("Tandem.Surface", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#a3a098"), BrushColor("ButtonBackground", ThemeVariant.Light));
    }

    [AvaloniaFact]
    public void A_control_already_on_screen_follows_the_applied_theme()
    {
        var border = new Border { Width = 10, Height = 10 };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Tandem.BarBackground"));
        var window = new Window { Content = border, RequestedThemeVariant = ThemeVariant.Light };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse("#a3a098"), Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);

            ThemeService.Apply(WithColors(("bar", "#112233", "#445566")));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse("#112233"), Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);

            window.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse("#445566"), Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(BuiltInThemes.Stone);
        }
    }

    [AvaloniaFact]
    public void The_fluent_palette_follows_the_theme()
    {
        try
        {
            ThemeService.Apply(WithColors(("focus", "#aa0000", "#00aa00"), ("surface", "#102030", "#405060"),
                ("ink", "#010203", "#fafbfc")));

            Assert.Equal(Color.Parse("#aa0000"), Assert.IsType<Color>(Resource("SystemAccentColor", ThemeVariant.Light)));
            Assert.Equal(Color.Parse("#00aa00"), Assert.IsType<Color>(Resource("SystemAccentColor", ThemeVariant.Dark)));
            Assert.Equal(Color.Parse("#102030"), Assert.IsType<Color>(Resource("SystemRegionColor", ThemeVariant.Light)));
            Assert.Equal(Color.Parse("#fafbfc"), Assert.IsType<Color>(Resource("SystemBaseHighColor", ThemeVariant.Dark)));
            // A brush Fluent derives from the palette.
            Assert.Equal(Color.Parse("#aa0000"),
                Assert.IsAssignableFrom<ISolidColorBrush>(Resource("SystemControlHighlightAccentBrush", ThemeVariant.Light)).Color);
            // Window background and main text, which Fluent does not rebuild from a changed palette: written directly.
            Assert.Equal(Color.Parse("#102030"), BrushColor("SystemRegionBrush", ThemeVariant.Light));
            Assert.Equal(Color.Parse("#fafbfc"), BrushColor("SystemControlForegroundBaseHighBrush", ThemeVariant.Dark));
            Assert.Equal(Color.Parse("#010203"), BrushColor("SystemControlHighlightAltBaseHighBrush", ThemeVariant.Light));
        }
        finally
        {
            ThemeService.Apply(BuiltInThemes.Stone);
        }

        Assert.Equal(Color.Parse("#7c3f21"), Assert.IsType<Color>(Resource("SystemAccentColor", ThemeVariant.Light)));
    }

    [AvaloniaFact]
    public void Fonts_text_styles_and_sizes_are_resources()
    {
        var ui = Assert.IsType<FontFamily>(Resource("Tandem.Font.Ui"));
        Assert.Equal("Segoe UI Variable Text", ui.FamilyNames[0]);
        Assert.Contains("Segoe UI", ui.FamilyNames);
        Assert.DoesNotContain("system-ui", ui.FamilyNames);
        var mono = Assert.IsType<FontFamily>(Resource("Tandem.Font.Mono"));
        Assert.Equal(["Cascadia Mono", "Consolas", "Menlo", "Monaco", "Courier New"], mono.FamilyNames);
        Assert.Same(ui, Resource("ContentControlThemeFontFamily"));
        Assert.Equal(14.0, Resource("ControlContentThemeFontSize"));

        Assert.Equal(14.0, Resource("Tandem.FontSize.Data"));
        Assert.Equal(22.0, Resource("Tandem.LineHeight.Data"));
        Assert.Equal(FontWeight.SemiBold, Resource("Tandem.FontWeight.Item"));
        Assert.Equal(15.0, Resource("Tandem.FontSize.DialogText"));

        Assert.Equal(22.0, Resource("Tandem.Size.RowHeight"));
        Assert.Equal(26.0, Resource("Tandem.Size.FkeyHeight"));
        Assert.Equal(8.0, Resource("Tandem.Size.Space2"));
        Assert.Equal(3.0, Resource("Tandem.Size.RadiusButton"));
        Assert.Equal(new CornerRadius(3), Resource("Tandem.CornerRadius.RadiusButton"));
        Assert.Equal(new CornerRadius(4), Resource("ControlCornerRadius"));
        Assert.Equal(new CornerRadius(8), Resource("OverlayCornerRadius"));
    }

    [AvaloniaFact]
    public void Windows_use_the_theme_interface_font()
    {
        var window = new Window();
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Resource("Tandem.Font.Ui"), window.FontFamily);
        }
        finally
        {
            window.Close();
        }
    }
}
