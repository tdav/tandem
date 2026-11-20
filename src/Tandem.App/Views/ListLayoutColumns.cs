using Avalonia;
using Avalonia.Controls;
using Tandem.App.ViewModels;
using Tandem.Core.Settings;

namespace Tandem.App.Views;

/// <summary>
/// Lays out a grid of the file list (the column headers or a row) by a <see cref="ListLayoutViewModel"/>: set
/// <c>Layout</c> on the grid, whose columns 0 to 4 are "Name" and the four cells, and <c>Cell</c> on each child that
/// shows a column. The grid's cell widths, and each such child's grid column and visibility, follow the layout while
/// the grid is shown. Columns after the fifth are left alone.
/// </summary>
public static class ListLayoutColumns
{
    public static readonly AttachedProperty<ListLayoutViewModel?> LayoutProperty =
        AvaloniaProperty.RegisterAttached<Grid, ListLayoutViewModel?>("Layout", typeof(ListLayoutColumns));

    public static readonly AttachedProperty<ColumnId?> CellProperty =
        AvaloniaProperty.RegisterAttached<Control, ColumnId?>("Cell", typeof(ListLayoutColumns));

    private static readonly AttachedProperty<Follower?> FollowerProperty =
        AvaloniaProperty.RegisterAttached<Grid, Follower?>("Follower", typeof(ListLayoutColumns));

    static ListLayoutColumns()
    {
        LayoutProperty.Changed.AddClassHandler<Grid>((grid, _) => Follow(grid));
    }

    public static ListLayoutViewModel? GetLayout(Grid grid) => grid.GetValue(LayoutProperty);
    public static void SetLayout(Grid grid, ListLayoutViewModel? value) => grid.SetValue(LayoutProperty, value);

    public static ColumnId? GetCell(Control control) => control.GetValue(CellProperty);
    public static void SetCell(Control control, ColumnId? value) => control.SetValue(CellProperty, value);

    private static void Follow(Grid grid)
    {
        grid.GetValue(FollowerProperty)?.Stop();
        var follower = GetLayout(grid) is { } layout ? new Follower(grid, layout) : null;
        grid.SetValue(FollowerProperty, follower);
    }

    /// <summary>Applies the layout to the grid now and on every column change while the grid is in the visual tree (so a
    /// grid that is gone does not stay subscribed to the layout).</summary>
    private sealed class Follower
    {
        private readonly Grid _grid;
        private readonly ListLayoutViewModel _layout;
        private bool _listening;

        public Follower(Grid grid, ListLayoutViewModel layout)
        {
            _grid = grid;
            _layout = layout;
            grid.AttachedToVisualTree += OnAttached;
            grid.DetachedFromVisualTree += OnDetached;
            if (TopLevel.GetTopLevel(grid) is not null) Listen(true);
            Apply();
        }

        public void Stop()
        {
            _grid.AttachedToVisualTree -= OnAttached;
            _grid.DetachedFromVisualTree -= OnDetached;
            Listen(false);
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Listen(true);
            Apply();
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Listen(false);

        private void Listen(bool listen)
        {
            if (listen == _listening) return;
            _listening = listen;
            if (listen) _layout.ColumnsChanged += OnColumnsChanged;
            else _layout.ColumnsChanged -= OnColumnsChanged;
        }

        // One notification per layout change, sent when the whole new layout is in place.
        private void OnColumnsChanged(object? sender, EventArgs e) => Apply();

        private void Apply()
        {
            var columns = _grid.ColumnDefinitions;
            if (columns.Count < 5) return;
            columns[1].Width = _layout.Cell1Width;
            columns[2].Width = _layout.Cell2Width;
            columns[3].Width = _layout.Cell3Width;
            columns[4].Width = _layout.Cell4Width;
            foreach (var child in _grid.Children)
            {
                if (GetCell(child) is not { } cell) continue;
                var (index, visible) = cell switch
                {
                    ColumnId.Extension => (_layout.ExtensionCell, _layout.ExtensionVisible),
                    ColumnId.Size => (_layout.SizeCell, _layout.SizeVisible),
                    ColumnId.Modified => (_layout.ModifiedCell, _layout.ModifiedVisible),
                    _ => (_layout.ModeCell, _layout.ModeVisible),
                };
                Grid.SetColumn(child, index);
                child.IsVisible = visible;
            }
        }
    }
}
