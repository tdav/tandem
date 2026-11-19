using Avalonia;
using Tandem.App.Views;
using Tandem.Core.Settings;

namespace Tandem.App.Tests.Views;

public class WindowPlacementTests
{
    private static readonly PixelRect[] OneScreen = [new(0, 0, 1920, 1080)];

    private static void AssertCentredDefault(WindowPlacement.Placement placement)
    {
        Assert.Null(placement.Position);
        Assert.Equal(WindowPlacement.DefaultWidth, placement.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, placement.Height);
        Assert.False(placement.Maximized);
    }

    [Fact]
    public void No_saved_placement_centres_with_the_default_size()
    {
        var placement = WindowPlacement.Restore(null, OneScreen, scaling: 1);

        AssertCentredDefault(placement);
        Assert.Equal(0.5, placement.SplitRatio);
    }

    [Fact]
    public void A_valid_placement_is_kept()
    {
        var placement = WindowPlacement.Restore(new WindowState(100, 80, 900, 600, true, 0.4), OneScreen, scaling: 2);

        Assert.Equal(new PixelPoint(100, 80), placement.Position);
        Assert.Equal(900, placement.Width);
        Assert.Equal(600, placement.Height);
        Assert.True(placement.Maximized);
        Assert.Equal(0.4, placement.SplitRatio);
    }

    [Theory]
    [InlineData(double.NaN, 0, 900, 600)]
    [InlineData(0, double.PositiveInfinity, 900, 600)]
    [InlineData(0, 0, 0, 600)]
    [InlineData(0, 0, 900, -1)]
    [InlineData(0, 0, double.NaN, 600)]
    [InlineData(3000, 0, 900, 600)] // right of the only screen
    [InlineData(-2000, 0, 900, 600)] // left of it
    [InlineData(0, 1100, 900, 600)] // below it
    public void Invalid_or_off_screen_placement_is_centred_with_the_default_size(double x, double y, double width,
        double height)
    {
        var placement = WindowPlacement.Restore(new WindowState(x, y, width, height, true, 0.4), OneScreen, scaling: 1);

        AssertCentredDefault(placement);
        Assert.Equal(0.4, placement.SplitRatio); // the split is checked on its own
    }

    [Fact]
    public void A_window_on_a_second_screen_is_kept()
    {
        PixelRect[] screens = [new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440)];

        var placement = WindowPlacement.Restore(new WindowState(2500, 100, 900, 600, false, 0.5), screens, scaling: 1);

        Assert.Equal(new PixelPoint(2500, 100), placement.Position);
    }

    [Fact]
    public void Without_screen_information_finite_values_are_kept()
    {
        var placement = WindowPlacement.Restore(new WindowState(10, 10, 900, 600, false, 0.5), [], scaling: 1);

        Assert.Equal(new PixelPoint(10, 10), placement.Position);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-0.2)]
    [InlineData(1)]
    [InlineData(0.01)]
    [InlineData(0.99)]
    public void Unusable_split_ratio_is_reset_to_half(double ratio)
    {
        Assert.Equal(0.5, WindowPlacement.ValidSplitRatio(ratio));
    }
}
