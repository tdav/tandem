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
        KeepOnTop.IsCheckedChanged += (_, _) => Topmost = KeepOnTop.IsChecked == true;
        KeepOnTop.Click += (_, _) => Terminal.Focus();
    }

    public TerminalWindow(TerminalViewModel model) : this()
    {
        DataContext = model;
        Terminal.Attach(model);
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                e.Handled = true;
                if (e.KeyModifiers != KeyModifiers.Meta) return;
                switch (e.Key)
                {
                    case Key.W: Close(); break;
                    case Key.C: await model.CopySelectionAsync(); break;
                    case Key.V: await model.PasteAsync(); break;
                }
            }
            else if (e.Key == Key.Escape && model.State == TerminalState.Connecting)
            {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            model.AttachClipboard(this);
            Terminal.Focus();
            var grid = Terminal.GridSize;
            await model.StartAsync(grid.Columns, grid.Rows);
        };
        Activated += (_, _) => Terminal.Focus();
        Closed += (_, _) => Terminal.Detach();
    }
}
