using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Tandem.App.Views;

/// <summary>
/// The file list's <see cref="ListBox"/>, with every selection behaviour of its own switched off: the panel
/// (<see cref="ViewModels.PanelViewModel.CursorIndex"/>) is the only owner of the highlighted row. It never moves or
/// extends the selection for keys (arrows, Home/End, PageUp/PageDown, Cmd+A, with any modifiers), typed text
/// (type-to-select), focus moving onto a row, or pointer input; mouse clicks are handled by
/// <see cref="FileListView"/> through the panel.
/// </summary>
public sealed class PanelListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Navigation keys are swallowed so the window's directional focus navigation does not move the focus
        // to another row either; the panel's keys were handled before this control saw them.
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        // No type-to-select: typed text is the panel's quick search (handled by the window).
    }

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs eventArgs) => false;
}
