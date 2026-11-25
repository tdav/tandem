using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Tandem.App.Views;

/// <summary>
/// The items panel of the brief view: rows of <see cref="ItemHeight"/> filling a column top to bottom, as many as fit
/// in the height, then the next column (column-major, like a newspaper). Columns are of equal width: as many of at
/// least <see cref="MinColumnWidth"/> as fit in the visible width share it. The panel is as wide as its columns, so the
/// list scrolls sideways when they do not fit; it never scrolls up and down.
/// </summary>
/// <remarks>
/// Virtualizing: only the rows of the columns in the viewport have containers, recycled as columns scroll by, so a
/// folder of 100,000 entries costs a few screens of rows. The row with the keyboard focus, and the one last scrolled
/// into view, keep their containers until they are in view again or lose the focus. Items are expected to be replaced
/// as a whole (one Reset): any change of the items gives every container back.
/// </remarks>
public sealed class BriefColumnsPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<BriefColumnsPanel, double>(nameof(ItemHeight), 20);

    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<BriefColumnsPanel, double>(nameof(MinColumnWidth), 200);

    private readonly Dictionary<int, Control> _realized = [];
    private readonly Dictionary<Control, object?> _recycleKeys = [];
    private readonly Dictionary<object, Stack<Control>> _recyclePool = [];
    private Rect _viewport;
    private int _scrollToIndex = -1;

    static BriefColumnsPanel()
    {
        AffectsMeasure<BriefColumnsPanel>(ItemHeightProperty, MinColumnWidthProperty);
    }

    /// <summary>The height of every row; the list's row height.</summary>
    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>The narrowest a column may be; the visible width is shared by as many columns as allow it.</summary>
    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>The rows of one column, as last laid out.</summary>
    public int RowsPerColumn { get; private set; } = 1;

    /// <summary>The width of every column, as last laid out.</summary>
    public double ColumnWidth { get; private set; }

    /// <summary>The columns that fit in the visible width.</summary>
    public int VisibleColumns { get; private set; } = 1;

    /// <summary>Raised during layout when <see cref="RowsPerColumn"/>, <see cref="ColumnWidth"/> or
    /// <see cref="VisibleColumns"/> changed.</summary>
    public event EventHandler? GeometryChanged;

    /// <summary>The rectangle of the item at <paramref name="index"/>, by the last layout.</summary>
    public Rect ItemRect(int index) =>
        new(index / RowsPerColumn * ColumnWidth, index % RowsPerColumn * ItemHeightOrDefault, ColumnWidth,
            ItemHeightOrDefault);

    private double ItemHeightOrDefault => ItemHeight is > 0 and < double.PositiveInfinity ? ItemHeight : 20;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        this.FindAncestorOfType<FileListView>()?.OnBriefPanelAttached(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (e.EffectiveViewport == _viewport) return;
        _viewport = e.EffectiveViewport;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        var count = items.Count;
        var rowHeight = ItemHeightOrDefault;

        // The list never scrolls up and down, so the height given is the visible one; the width is unbounded (the
        // list scrolls sideways), so the visible width is the viewport's, or the list's before there is one.
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height
            : _viewport.Height > 0 ? _viewport.Height : rowHeight;
        var width = _viewport.Width > 0 ? _viewport.Width
            : double.IsFinite(availableSize.Width) ? availableSize.Width
            : ItemsControl?.Bounds.Width is > 0 and var listWidth ? listWidth : MinColumnWidth;
        var minWidth = MinColumnWidth > 1 ? MinColumnWidth : 1;
        var rows = Math.Max(1, (int)Math.Floor(height / rowHeight));
        var visibleColumns = Math.Max(1, (int)Math.Floor(width / minWidth));
        var columnWidth = width / visibleColumns;
        var geometryChanged = rows != RowsPerColumn || columnWidth != ColumnWidth || visibleColumns != VisibleColumns;
        RowsPerColumn = rows;
        ColumnWidth = columnWidth;
        VisibleColumns = visibleColumns;

        // The rows of the columns in view, the column cut by the right edge included.
        var firstColumn = Math.Max(0, (int)Math.Floor(Math.Max(0, _viewport.X) / columnWidth));
        var lastColumn = Math.Max(firstColumn, (int)Math.Ceiling((Math.Max(0, _viewport.X) + width) / columnWidth) - 1);
        var first = Math.Min(count, firstColumn * rows);
        var last = (int)Math.Min(count - 1L, (lastColumn + 1L) * rows - 1);
        if (_scrollToIndex >= first && _scrollToIndex <= last) _scrollToIndex = -1;

        foreach (var (index, container) in _realized.ToArray())
        {
            if (index >= first && index <= last) continue;
            if (index == _scrollToIndex || (index < count && container.IsKeyboardFocusWithin)) continue;
            _realized.Remove(index);
            Recycle(container);
        }
        for (var index = first; index <= last; index++)
            if (!_realized.ContainsKey(index)) Realize(index);

        var itemSize = new Size(columnWidth, rowHeight);
        foreach (var container in _realized.Values) container.Measure(itemSize);

        if (geometryChanged) GeometryChanged?.Invoke(this, EventArgs.Empty);
        var columns = count == 0 ? 0 : (count + rows - 1) / rows;
        return new Size(columns * columnWidth, Math.Min(count, rows) * rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (index, container) in _realized) container.Arrange(ItemRect(index));
        return finalSize;
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        RecycleAll();
        InvalidateMeasure();
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        RecycleAll();
        _recyclePool.Clear();
        InvalidateMeasure();
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count || !IsEffectivelyVisible) return null;
        if (!_realized.TryGetValue(index, out var container))
        {
            container = Realize(index);
            if (container is null) return null;
            _scrollToIndex = index;
        }
        // Laid out at its place now, so the scroll viewer can bring that rectangle into view; the next measure (for
        // the new viewport) keeps it until it is in view.
        var rect = ItemRect(index);
        container.Measure(rect.Size);
        container.Arrange(rect);
        container.BringIntoView();
        return container;
    }

    protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
            if (ReferenceEquals(realized, container)) return index;
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values.ToArray();

    // The panel's keys move the cursor (MainViewModel); the list never moves the focus between rows by itself.
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) => null;

    private Control? Realize(int index)
    {
        if (ItemContainerGenerator is not { } generator) return null;
        var item = Items[index];
        Control container;
        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            if (recycleKey is not null && _recyclePool.TryGetValue(recycleKey, out var pool) && pool.TryPop(out var reused))
            {
                container = reused;
                container.SetCurrentValue(IsVisibleProperty, true);
                generator.PrepareItemContainer(container, item, index);
            }
            else
            {
                container = generator.CreateContainer(item, index, recycleKey);
                generator.PrepareItemContainer(container, item, index);
                AddInternalChild(container);
            }
            _recycleKeys[container] = recycleKey;
        }
        else
        {
            // The item is its own container.
            container = (Control)item!;
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            _recycleKeys[container] = null;
        }
        _realized[index] = container;
        generator.ItemContainerPrepared(container, item, index);
        return container;
    }

    private void Recycle(Control container)
    {
        _recycleKeys.Remove(container, out var recycleKey);
        if (ItemContainerGenerator is { } generator) generator.ClearItemContainer(container);
        if (recycleKey is null)
        {
            RemoveInternalChild(container);
            return;
        }
        container.SetCurrentValue(IsVisibleProperty, false);
        if (!_recyclePool.TryGetValue(recycleKey, out var pool)) _recyclePool[recycleKey] = pool = new Stack<Control>();
        pool.Push(container);
    }

    private void RecycleAll()
    {
        _scrollToIndex = -1;
        foreach (var container in _realized.Values.ToArray()) Recycle(container);
        _realized.Clear();
    }
}
