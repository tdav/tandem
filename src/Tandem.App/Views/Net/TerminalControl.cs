using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Tandem.App.ViewModels.Net;
using Tandem.Net.Terminal;

namespace Tandem.App.Views.Net;

public sealed class TerminalControl : Control
{
    private static readonly FontFamily TerminalFont = new("Menlo, monospace");
    private TerminalViewModel? _model;
    private (int Column, int Line)? _anchor;
    private bool _dragging;
    private double _cellWidth = 8;
    private double _cellHeight = 18;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        SizeChanged += (_, _) => ResizeToBounds();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    internal void Attach(TerminalViewModel model)
    {
        _model = model;
        var letter = Format("M", CellStyle.None, Brushes.Transparent);
        _cellWidth = Math.Ceiling(letter.Width);
        _cellHeight = Math.Ceiling(letter.Height);
        model.ScreenChanged += OnScreenChanged;
    }

    internal void Detach()
    {
        if (_model is not null) _model.ScreenChanged -= OnScreenChanged;
        _model = null;
    }

    internal (int Columns, int Rows) GridSize => (Math.Clamp((int)(Bounds.Width / _cellWidth), 2, 500),
        Math.Clamp((int)(Bounds.Height / _cellHeight), 2, 300));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var foreground = Resource("Tandem.TerminalForeground");
        var background = Resource("Tandem.TerminalBackground");
        context.FillRectangle(background, new Rect(Bounds.Size));
        if (_model is not { } model) return;

        int columns, rows, top;
        (int Column, int Row) cursor;
        bool showCursor;
        TerminalCell[] cells;
        lock (model.ScreenLock)
        {
            var screen = model.Screen;
            (columns, rows) = (screen.Columns, screen.Rows);
            top = -Math.Min(model.ScrollOffset, screen.ScrollbackCount);
            cursor = screen.Cursor;
            showCursor = top == 0 && screen.CursorVisible && IsFocused && model.State == TerminalState.Connected;
            cells = new TerminalCell[columns * rows];
            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                    cells[row * columns + column] = screen.CellAt(column, top + row);
        }

        var selection = model.Selection;
        var selectedBrush = selection is not null ? Resource("Tandem.TerminalSelection") : null;
        var palette = new Dictionary<TerminalColor, IBrush>();
        var glyphs = new Dictionary<(string Text, CellStyle Style, IBrush Brush), FormattedText>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var cell = cells[row * columns + column];
                if (cell.Width == 0) continue;
                var fg = ColorBrush(cell.Foreground, foreground);
                var bg = ColorBrush(cell.Background, background);
                if (cell.Style.HasFlag(CellStyle.Inverse)) (fg, bg) = (bg, fg);
                var point = new Point(column * _cellWidth, row * _cellHeight);
                var bounds = new Rect(point, new Size(Math.Max(1, cell.Width) * _cellWidth, _cellHeight));
                if (!ReferenceEquals(bg, background)) context.FillRectangle(bg, bounds);
                if (selection is { } range &&
                    (Contains(range, (column, top + row)) || cell.Width == 2 && Contains(range, (column + 1, top + row))))
                    context.FillRectangle(selectedBrush!, bounds);
                if (!string.IsNullOrWhiteSpace(cell.Text))
                {
                    var key = (cell.Text, cell.Style & (CellStyle.Bold | CellStyle.Italic), fg);
                    if (!glyphs.TryGetValue(key, out var glyph))
                        glyphs.Add(key, glyph = Format(key.Text, key.Item2, fg));
                    if (cell.Style.HasFlag(CellStyle.Dim))
                    {
                        using var dimmed = context.PushOpacity(0.65);
                        context.DrawText(glyph, point);
                    }
                    else context.DrawText(glyph, point);
                }
                if (cell.Style.HasFlag(CellStyle.Underline)) DrawRule(bounds.Bottom - 2);
                if (cell.Style.HasFlag(CellStyle.Strikethrough)) DrawRule(bounds.Top + _cellHeight / 2);

                void DrawRule(double y) =>
                    context.DrawLine(new Pen(fg), new Point(bounds.Left, y), new Point(bounds.Right, y));
            }
        }
        if (showCursor)
            context.DrawRectangle(new Pen(Resource("Tandem.TerminalCursor")), new Rect(
                cursor.Column * _cellWidth + 0.5, cursor.Row * _cellHeight + 0.5, _cellWidth - 1, _cellHeight - 1));

        IBrush ColorBrush(TerminalColor color, IBrush fallback)
        {
            if (color.Kind == TerminalColorKind.Default) return fallback;
            if (palette.TryGetValue(color, out var brush)) return brush;
            brush = color.Kind == TerminalColorKind.Rgb ? new SolidColorBrush(Color.FromUInt32(0xff000000u | (uint)color.Value))
                : color.Value < 16 ? Resource($"Tandem.TerminalAnsi{color.Value}") : IndexedBrush(color.Value);
            palette.Add(color, brush);
            return brush;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _model is not { } model) return;
        var keys = e.KeyModifiers;
        if (keys == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            model.ScrollOffset += (e.Key == Key.PageUp ? 1 : -1) * Math.Max(1, GridSize.Rows - 1);
            return;
        }

        var modifiers = (keys.HasFlag(KeyModifiers.Shift) ? TerminalModifiers.Shift : 0)
            | (keys.HasFlag(KeyModifiers.Alt) ? TerminalModifiers.Alt : 0)
            | (keys.HasFlag(KeyModifiers.Control) ? TerminalModifiers.Control : 0);
        if (MapKey(e.Key) is { } key)
        {
            e.Handled = true;
            model.SendKey(key, modifiers);
        }
        else if ((modifiers & (TerminalModifiers.Control | TerminalModifiers.Alt)) != 0)
        {
            var symbol = e.KeySymbol;
            if (modifiers.HasFlag(TerminalModifiers.Control) && e.Key is >= Key.A and <= Key.Z)
                symbol = ((char)('A' + (e.Key - Key.A))).ToString();
            if (string.IsNullOrEmpty(symbol)) return;
            e.Handled = true;
            model.SendText(symbol, modifiers);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || _model is null) return;
        e.Handled = true;
        _model.SendText(e.Text, TerminalModifiers.None);
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_model is not { } model) return;
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            await model.SecondaryClickAsync();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        var cell = CellAt(point.Position);
        e.Handled = true;
        if (e.ClickCount >= 3) { model.SelectLineAt(cell.Line); return; }
        if (e.ClickCount == 2) { model.SelectWordAt(cell.Column, cell.Line); return; }
        _anchor = cell;
        _dragging = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_anchor is not { } anchor || _model is not { } model) return;
        var cell = CellAt(e.GetPosition(this));
        if (!_dragging && cell == anchor) return;
        _dragging = true;
        model.SelectRange(anchor, cell);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_anchor is null) return;
        _anchor = null;
        if (!_dragging) _model?.ClearSelection();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _anchor = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_model is not { } model || e.Delta.Y == 0) return;
        e.Handled = true;
        model.ScrollOffset += Math.Sign(e.Delta.Y) * 3;
    }

    private void ResizeToBounds()
    {
        if (_model is not { } model) return;
        var grid = GridSize;
        model.Resize(grid.Columns, grid.Rows);
        InvalidateVisual();
    }

    private void OnScreenChanged(object? sender, EventArgs e) => InvalidateVisual();

    private (int Column, int Line) CellAt(Point point)
    {
        var grid = GridSize;
        var offset = _model?.ScrollOffset ?? 0;
        return (Math.Clamp((int)(point.X / _cellWidth), 0, grid.Columns - 1),
            Math.Clamp((int)(point.Y / _cellHeight), 0, grid.Rows - 1) - offset);
    }

    private FormattedText Format(string text, CellStyle style, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(TerminalFont,
            style.HasFlag(CellStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
            style.HasFlag(CellStyle.Bold) ? FontWeight.Bold : FontWeight.Normal), _model?.FontSize ?? 14, brush);

    private IBrush Resource(string name) =>
        this.TryFindResource(name, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    private static IBrush IndexedBrush(int number)
    {
        if (number >= 232)
        {
            var grey = (byte)(8 + (number - 232) * 10);
            return new SolidColorBrush(Color.FromRgb(grey, grey, grey));
        }
        number -= 16;
        return new SolidColorBrush(Color.FromRgb(Level(number / 36), Level(number / 6 % 6), Level(number % 6)));

        static byte Level(int index) => (byte)(index == 0 ? 0 : 55 + index * 40);
    }

    private static bool Contains(((int Column, int Line) From, (int Column, int Line) To) range,
        (int Column, int Line) cell)
    {
        var a = (range.From.Line, range.From.Column);
        var b = (range.To.Line, range.To.Column);
        var point = (cell.Line, cell.Column);
        if (a.CompareTo(b) > 0) (a, b) = (b, a);
        return point.CompareTo(a) >= 0 && point.CompareTo(b) <= 0;
    }

    private static TerminalKey? MapKey(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter, Key.Tab => TerminalKey.Tab, Key.Escape => TerminalKey.Escape,
        Key.Back => TerminalKey.Backspace, Key.Delete => TerminalKey.Delete, Key.Insert => TerminalKey.Insert,
        Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down, Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right, Key.Home => TerminalKey.Home, Key.End => TerminalKey.End,
        Key.PageUp => TerminalKey.PageUp, Key.PageDown => TerminalKey.PageDown,
        >= Key.F1 and <= Key.F12 => (TerminalKey)((int)TerminalKey.F1 + (key - Key.F1)),
        _ => null,
    };
}
