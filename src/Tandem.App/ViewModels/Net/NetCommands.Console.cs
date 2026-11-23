using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.Core.Settings;
using Tandem.Net;
using Tandem.Net.Ssh;

namespace Tandem.App.ViewModels.Net;

public sealed partial class NetCommands
{
    internal Func<double> ConsoleFontSize { get; set; } = () => AppSettings.DefaultFontSize;

    public bool HasOpenConsole => _services.ToolWindows.LastOpen<TerminalViewModel>() is not null;

    partial void RegisterConsole()
    {
        On(CommandIds.NetConsole, ShowConsoleAsync, () => ConsoleProfile() is not null || _services.Consoles?.Profiles.Count > 0);
        On(CommandIds.NetShowConsole, ShowOpenConsoleAsync, () => !_disposed && !_closing && HasOpenConsole);
        _services.ToolWindows.OpenWindowsChanged += OnToolWindowsChanged;
        OnDispose(() => _services.ToolWindows.OpenWindowsChanged -= OnToolWindowsChanged);
        if (_services.Net is not { } net) return;
        void Changed(object? sender, EventArgs e)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) UpdateAvailability();
            else Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAvailability);
        }

        net.Changed += Changed;
        OnDispose(() => net.Changed -= Changed);
    }

    private void OnToolWindowsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasOpenConsole));
        UpdateAvailability();
    }

    private Task ShowOpenConsoleAsync()
    {
        if (!_closing && !_disposed && _services.ToolWindows.LastOpen<TerminalViewModel>() is { } terminal)
            _services.ToolWindows.Show(terminal);
        return Task.CompletedTask;
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

        OpenConsole(profile);
    }

    private void OpenConsole(ConnectionProfile profile)
    {
        if (_disposed || _closing || _services.Consoles is not { } consoles) return;
        TerminalViewModel? windowModel = null;
        windowModel = new TerminalViewModel(profile.Name, () => consoles.Open(profile.Id), new WindowTerminalClipboard(),
            question => _services.ToolWindows.ShowDialogAsync(question, ownerModel: windowModel), _reportFailure)
        { FontSize = ConsoleFontSize() };
        _services.ToolWindows.Show(windowModel);
    }
}
