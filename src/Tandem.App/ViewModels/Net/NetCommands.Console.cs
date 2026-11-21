using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.Core.Settings;
using Tandem.Net;
using Tandem.Net.Ssh;

namespace Tandem.App.ViewModels.Net;

public sealed partial class NetCommands
{
    internal Func<double> ConsoleFontSize { get; set; } = () => AppSettings.DefaultFontSize;

    partial void RegisterConsole()
    {
        On(CommandIds.NetConsole, ShowConsoleAsync, () => ConsoleProfile() is not null || _services.Consoles?.Profiles.Count > 0);
        if (_services.Net is not { } net) return;
        void Changed(object? sender, EventArgs e)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) UpdateAvailability();
            else Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAvailability);
        }

        net.Changed += Changed;
        OnDispose(() => net.Changed -= Changed);
    }

    private ConnectionProfile? ConsoleProfile() =>
        Active is { } panel && _services.Net?.ProfileOf(panel.CurrentPath.ProviderId) is { } profile &&
        SshConsoles.Supports(profile) ? profile : null;

    private async Task ShowConsoleAsync()
    {
        if (_services.Consoles is not { } consoles) return;
        var profile = ConsoleProfile();
        if (profile is null)
        {
            var profiles = consoles.Profiles;
            if (profiles.Count == 0) return;
            if (profiles.Count == 1) profile = profiles[0];
            else
            {
                var selected = await _services.ToolWindows.ShowDialogAsync(new ConsolePickViewModel(profiles));
                profile = consoles.Profiles.FirstOrDefault(p => p.Id == selected);
                if (profile is null) return;
            }
        }

        var id = profile.Id;
        TerminalViewModel? windowModel = null;
        windowModel = new TerminalViewModel(profile.Name, () => consoles.Open(id), new WindowTerminalClipboard(),
            question => _services.ToolWindows.ShowDialogAsync(question, ownerModel: windowModel), _reportFailure)
        { FontSize = ConsoleFontSize() };
        _services.ToolWindows.Show(windowModel);
    }
}
