using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.Commands;
using Tandem.App.Input;
using Tandem.App.ViewModels.Viewing;

namespace Tandem.App.Views.Viewing;

/// <summary>
/// The window of one <see cref="ViewerViewModel"/> (F3): its <see cref="ViewerView"/>, which has the focus. Esc, F3
/// and Cmd+W close it (fixed keys, not commands). Once closed, the viewer is disposed: the file is closed and a
/// temporary copy of it released.
/// </summary>
public partial class ViewerWindow : Window
{
    private static readonly KeyChord[] CloseChords =
        [new(Key.Escape, KeyModifiers.None), new(Key.F3, KeyModifiers.None), new KeyChord(Key.W, PlatformKeys.Command)];

    /// <summary>For the designer only.</summary>
    public ViewerWindow()
    {
        InitializeComponent();
        Viewer = null!;
    }

    public ViewerWindow(ViewerViewModel viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        InitializeComponent();
        Viewer = viewer;
        DataContext = viewer;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => View.Focus();
        Closed += (_, _) => Run(viewer.DisposeAsync().AsTask());
    }

    /// <summary>The file shown.</summary>
    public ViewerViewModel Viewer { get; }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var chord = KeyRouting.ChordFor(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (!CloseChords.Contains(chord)) return;
        e.Handled = true;
        Close();
    }

    /// <summary>What <paramref name="task"/> throws unexpectedly goes to the UI thread's unhandled-exception handler
    /// (the app logs and shows it).</summary>
    private static async void Run(Task task) => await task;
}
