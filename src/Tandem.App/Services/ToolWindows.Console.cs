using Avalonia.Controls;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Net;
using Tandem.App.Views.Dialogs;
using Tandem.App.Views.Net;
using Tandem.Net;

namespace Tandem.App.Services;

public sealed partial class ToolWindows
{
    partial void AddConsole()
    {
        Register<TerminalViewModel>(model => new TerminalWindow(model));
        Register<ConsolePickViewModel>(model => new ConsolePickWindow(model));
    }

    private sealed class ConsolePickWindow : DialogWindow
    {
        public ConsolePickWindow(ConsolePickViewModel model) : base(Loc.Get("Console_Choose"))
        {
            var select = new ComboBox
            {
                ItemsSource = model.Profiles,
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ConnectionProfile>((profile, _) =>
                    new TextBlock { Text = profile?.Name }),
                SelectedIndex = 0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            var connect = MakeButton(Loc.Get("Console_Open"), isDefault: true);
            connect.Click += (_, _) => model.Choose(select.SelectedItem as ConnectionProfile);
            SetContent(select, [select], connect, CancelButton());
        }
    }
}
