using Avalonia;
using Tandem.Core.Settings;

namespace Tandem.App.Views;

/// <summary>Checks a saved window placement before it is applied, so the window never opens with a broken size or
/// somewhere no screen shows it.</summary>
public static class WindowPlacement
{
    public const double DefaultWidth = 1200;
    public const double DefaultHeight = 750;
    public const double DefaultSplitRatio = 0.5;

    // The narrowest share a panel may be restored with; anything outside is treated as broken.
    private const double MinSplitRatio = 0.05;

    /// <summary>What to apply. <see cref="Position"/> null: centre the window on the primary screen.</summary>
    public sealed record Placement(PixelPoint? Position, double Width, double Height, bool Maximized, double SplitRatio);

    /// <summary>
    /// The saved placement if it is usable: finite values, a positive size, and some part of the window on one of
    /// <paramref name="screens"/> (screen areas in pixels, as <see cref="Avalonia.Controls.Window.Position"/> is;
    /// <paramref name="scaling"/> turns the saved size into pixels). Otherwise the default size, centred, not
    /// maximized. With no screen information the window is not checked against screens. The split ratio is checked
    /// on its own.
    /// </summary>
    public static Placement Restore(WindowState? saved, IReadOnlyList<PixelRect> screens, double scaling)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var centred = new Placement(null, DefaultWidth, DefaultHeight, false,
            ValidSplitRatio(saved?.SplitRatio ?? DefaultSplitRatio));
        if (saved is null) return centred;

        double[] numbers = [saved.X, saved.Y, saved.Width, saved.Height];
        if (numbers.Any(n => !double.IsFinite(n)) || saved.Width <= 0 || saved.Height <= 0) return centred;
        if (Math.Abs(saved.X) > int.MaxValue / 2.0 || Math.Abs(saved.Y) > int.MaxValue / 2.0) return centred;

        var factor = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        var window = new Rect(saved.X, saved.Y, saved.Width * factor, saved.Height * factor);
        if (screens.Count > 0 && !screens.Any(s => s.ToRect(1).Intersects(window))) return centred;

        return centred with
        {
            Position = new PixelPoint((int)Math.Round(saved.X), (int)Math.Round(saved.Y)),
            Width = saved.Width,
            Height = saved.Height,
            Maximized = saved.Maximized,
        };
    }

    /// <summary>The ratio if it leaves both panels a usable share of the width; otherwise one half.</summary>
    public static double ValidSplitRatio(double ratio) =>
        double.IsFinite(ratio) && ratio >= MinSplitRatio && ratio <= 1 - MinSplitRatio ? ratio : DefaultSplitRatio;
}
