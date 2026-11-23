using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.ViewModels.Net;

namespace Tandem.App.Views.Net;

public sealed partial class TerminalWindow : Window
{
    public TerminalWindow()
    {
        InitializeComponent();
        ConsoleView.EnableKeepOnTop(this);
    }

    public TerminalWindow(TerminalViewModel model) : this()
    {
        DataContext = model;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if ((e.Key == Key.W && e.KeyModifiers == KeyModifiers.Meta) ||
                (e.Key == Key.Escape && model.State == TerminalState.Connecting))
            {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) => ConsoleView.FocusTerminal();
        Activated += (_, _) => ConsoleView.FocusTerminal();
    }
}
