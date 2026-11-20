using Avalonia.Controls;
using Tandem.App.ViewModels;
using Tandem.Core.Settings;

namespace Tandem.App.Tests.ViewModels;

public class ListLayoutTests
{
    [Fact]
    public void Layout_maps_columns_to_cells_in_order_and_hides_with_zero_width()
    {
        var layout = new ListLayoutViewModel();
        var changed = new List<string?>();
        layout.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        layout.Apply([
            new ColumnSetting(ColumnId.Size, true, 90),
            new ColumnSetting(ColumnId.Mode, false, 70),
            new ColumnSetting(ColumnId.Extension, true, 50),
            new ColumnSetting(ColumnId.Modified, true, 130),
        ], fontSize: 16, rowHeight: 26);

        Assert.Equal(1, layout.SizeCell);
        Assert.Equal(2, layout.ModeCell);
        Assert.Equal(3, layout.ExtensionCell);
        Assert.Equal(4, layout.ModifiedCell);
        Assert.Equal(new GridLength(90), layout.Cell1Width);
        Assert.Equal(new GridLength(0), layout.Cell2Width);
        Assert.Equal(new GridLength(50), layout.Cell3Width);
        Assert.Equal(new GridLength(130), layout.Cell4Width);
        Assert.True(layout.SizeVisible);
        Assert.False(layout.ModeVisible);
        Assert.Equal(16, layout.FontSize);
        Assert.Equal(26, layout.RowHeight);
        Assert.Equal([ColumnId.Size, ColumnId.Mode, ColumnId.Extension, ColumnId.Modified],
            layout.Columns.Select(c => c.Column));
        Assert.Contains(nameof(ListLayoutViewModel.SizeCell), changed);
        Assert.Contains(nameof(ListLayoutViewModel.Cell2Width), changed);
        Assert.Contains(nameof(ListLayoutViewModel.RowHeight), changed);
    }

    [Fact]
    public void New_layout_shows_the_default_columns_and_sizes()
    {
        var layout = new ListLayoutViewModel();

        Assert.Equal(AppSettings.DefaultColumns, layout.Columns);
        Assert.Equal((1, 2, 3, 4), (layout.ExtensionCell, layout.SizeCell, layout.ModifiedCell, layout.ModeCell));
        Assert.Equal(new GridLength(56), layout.Cell1Width);
        Assert.Equal(AppSettings.DefaultFontSize, layout.FontSize);
        Assert.Equal(AppSettings.DefaultRowHeight, layout.RowHeight);
    }

    [Fact]
    public void Applying_the_same_layout_again_raises_nothing()
    {
        var layout = new ListLayoutViewModel();
        var changes = 0;
        layout.PropertyChanged += (_, _) => changes++;

        layout.Apply(AppSettings.DefaultColumns.ToArray(), AppSettings.DefaultFontSize, AppSettings.DefaultRowHeight);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Layout_without_every_column_is_refused()
    {
        var layout = new ListLayoutViewModel();
        Assert.Throws<ArgumentException>(() => layout.Apply([new ColumnSetting(ColumnId.Size, true, 80)], 14, 22));
    }
}
