using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tandem.App.Commands;
using Tandem.App.Input;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Viewing;

namespace Tandem.App.Views.Viewing;

/// <summary>
/// Shows a <see cref="ViewerViewModel"/> (its <see cref="StyledElement.DataContext"/>): the page as selectable
/// monospace text or the picture, a scroll bar over the whole file (dragging it jumps to that offset), and a bar with
/// the mode, encoding and line wrapping. Used by the viewer window and the quick-view panel.
/// </summary>
/// <remarks>
/// Keys while it has the focus (fixed, not commands): ↑/↓ one row; PageUp/PageDown and Space one page; Home/End the
/// start and the end; Cmd+1/2/3 text, hex, picture; Cmd+E the next encoding; Cmd+C copies the selected text. The mouse
/// wheel scrolls three rows a notch. The number of rows of a page follows the view's height.
/// </remarks>
public partial class ViewerView : UserControl
{
    private const int WheelRows = 3;

    private static readonly KeyChord Up = new(Key.Up, KeyModifiers.None);
    private static readonly KeyChord Down = new(Key.Down, KeyModifiers.None);
    private static readonly KeyChord PageUp = new(Key.PageUp, KeyModifiers.None);
    private static readonly KeyChord PageDown = new(Key.PageDown, KeyModifiers.None);
    private static readonly KeyChord Space = new(Key.Space, KeyModifiers.None);
    private static readonly KeyChord Home = new(Key.Home, KeyModifiers.None);
    private static readonly KeyChord End = new(Key.End, KeyModifiers.None);
    private static readonly KeyChord TextChord = KeyChord.Parse("Cmd+1");
    private static readonly KeyChord HexChord = KeyChord.Parse("Cmd+2");
    private static readonly KeyChord ImageChord = KeyChord.Parse("Cmd+3");
    private static readonly KeyChord NextEncodingChord = KeyChord.Parse("Cmd+E");
    private static readonly KeyChord CopyChord = KeyChord.Parse("Cmd+C");

    private ViewerViewModel? _viewer;
    private bool _showingOffset;

    public ViewerView()
    {
        InitializeComponent();
        ModeBox.ItemTemplate = new FuncDataTemplate<ViewerMode>((mode, _) => new TextBlock { Text = Loc.Enum(mode) });
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        TextArea.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        TextArea.SizeChanged += (_, _) => UpdatePageLines();
        OffsetBar.ValueChanged += OnOffsetBarChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewer is not null) _viewer.PropertyChanged -= OnViewerChanged;
        _viewer = DataContext as ViewerViewModel;
        if (_viewer is not null) _viewer.PropertyChanged += OnViewerChanged;
        ShowAll();
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewerViewModel.Lines):
                ShowLines();
                break;
            case nameof(ViewerViewModel.Offset) or nameof(ViewerViewModel.PageEnd):
                ShowOffset();
                break;
            case nameof(ViewerViewModel.Mode) or nameof(ViewerViewModel.WrapLines):
                ShowMode();
                break;
        }
    }

    private void ShowAll()
    {
        ShowMode();
        ShowLines();
        ShowOffset();
        UpdatePageLines();
    }

    private void ShowLines() => PageText.Text = _viewer is null ? null : string.Join('\n', _viewer.Lines);

    private void ShowMode()
    {
        var picture = _viewer?.Mode == ViewerMode.Image;
        var wrap = _viewer?.WrapLines == true && _viewer.Mode == ViewerMode.Text;
        TextArea.IsVisible = !picture;
        OffsetBar.IsVisible = !picture;
        Picture.IsVisible = picture;
        EncodingBox.IsEnabled = _viewer?.Mode == ViewerMode.Text;
        PageText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TextArea.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    /// <summary>The scroll bar follows the page: its range is the file's bytes, the thumb the page.</summary>
    private void ShowOffset()
    {
        if (_viewer is not { } viewer) return;
        _showingOffset = true;
        try
        {
            var page = Math.Max(1, viewer.PageEnd - viewer.Offset);
            OffsetBar.Maximum = viewer.Length;
            OffsetBar.ViewportSize = page;
            OffsetBar.LargeChange = page;
            OffsetBar.SmallChange = Math.Max(1.0, page / (double)Math.Max(1, viewer.Lines.Count));
            OffsetBar.Value = viewer.Offset;
        }
        finally
        {
            _showingOffset = false;
        }
    }

    private void OnOffsetBarChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_showingOffset || _viewer is not { } viewer) return;
        Run(viewer.ScrollToOffsetAsync((long)e.NewValue));
    }

    private void UpdatePageLines()
    {
        if (_viewer is null || TextArea.Bounds.Height <= 0) return;
        var rows = (int)((TextArea.Bounds.Height - PageText.Margin.Top - PageText.Margin.Bottom) / PageText.LineHeight);
        _viewer.PageLines = Math.Max(1, rows);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_viewer is not { } viewer || e.Delta.Y == 0) return;
        e.Handled = true;
        Run(viewer.ScrollLinesAsync(-Math.Sign(e.Delta.Y) * WheelRows));
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewer is not { } viewer) return;
        var chord = KeyRouting.ChordFor(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (chord == Up) Run(viewer.ScrollLinesAsync(-1));
        else if (chord == Down) Run(viewer.ScrollLinesAsync(1));
        else if (chord == PageUp) Run(viewer.ScrollLinesAsync(-viewer.PageLines));
        else if (chord == PageDown || chord == Space) Run(viewer.ScrollLinesAsync(viewer.PageLines));
        else if (chord == Home) Run(viewer.GoToStartAsync());
        else if (chord == End) Run(viewer.GoToEndAsync());
        else if (chord == TextChord) viewer.Mode = ViewerMode.Text;
        else if (chord == HexChord) viewer.Mode = ViewerMode.Hex;
        else if (chord == ImageChord) viewer.Mode = ViewerMode.Image;
        else if (chord == NextEncodingChord) NextEncoding(viewer);
        else if (chord == CopyChord) PageText.Copy();
        else return;
        e.Handled = true;
    }

    private static void NextEncoding(ViewerViewModel viewer)
    {
        var encodings = viewer.Encodings;
        var index = Math.Max(0, encodings.ToList().IndexOf(viewer.Encoding));
        viewer.Encoding = encodings[(index + 1) % encodings.Count];
    }

    /// <summary>Awaits a page change started by input; what it throws unexpectedly goes to the UI thread's
    /// unhandled-exception handler (the app logs and shows it).</summary>
    private static async void Run(Task change) => await change;
}
