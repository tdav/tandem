using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.Input;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>
/// The command line under the panels. In the field: Enter runs the text, Up/Down walk the history, Esc clears the
/// text (or, when it is empty, asks for the focus to go back to the panel), Ctrl+C kills a running command. The field
/// is a text editor, so the panels' plain keys do not fire while it has the focus (<see cref="KeyRouting"/>).
/// </summary>
public partial class CommandLineView : UserControl
{
    public CommandLineView()
    {
        InitializeComponent();
        CommandLineBox.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Esc in an empty field: the focus should go back to the active panel.</summary>
    public event EventHandler? ReturnFocusRequested;

    /// <summary>Running the text failed unexpectedly; the app reports it.</summary>
    public event EventHandler<Exception>? Failed;

    private CommandLineViewModel? Model => DataContext as CommandLineViewModel;

    /// <summary>Puts the keyboard focus in the field; false when it cannot take it (hidden or disabled).</summary>
    public bool FocusField() => CommandLineBox.Focus();

    /// <summary>Ctrl+C (whatever the layout) while a command runs: kills it. True when the key was used. Cmd+C
    /// stays the copy of the selection.</summary>
    internal static bool TryKill(CommandLineViewModel? model, KeyEventArgs e)
    {
        if (model is not { IsRunning: true }) return false;
        var chord = KeyRouting.ChordFor(e.Key, e.PhysicalKey, e.KeyModifiers);
        if (chord.Key != Key.C || chord.Modifiers != KeyModifiers.Control) return false;
        model.Kill();
        return true;
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        if (TryKill(model, e))
        {
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = ExecuteAsync(model);
                break;
            case Key.Up:
                e.Handled = true;
                model.HistoryPrevious();
                CommandLineBox.CaretIndex = model.Text.Length;
                break;
            case Key.Down:
                e.Handled = true;
                model.HistoryNext();
                CommandLineBox.CaretIndex = model.Text.Length;
                break;
            case Key.Escape:
                e.Handled = true;
                if (model.Text.Length > 0) model.Text = "";
                else ReturnFocusRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private async Task ExecuteAsync(CommandLineViewModel model)
    {
        try
        {
            await model.ExecuteAsync();
        }
        catch (Exception failure)
        {
            Failed?.Invoke(this, failure);
        }
    }
}
