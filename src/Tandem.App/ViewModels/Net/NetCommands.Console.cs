using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.Core.Settings;
using Tandem.Net;
using Tandem.Net.Ssh;

namespace Tandem.App.ViewModels.Net;

public sealed partial class NetCommands
{
    internal Func<AppSettings> ConsoleSettings { get; set; } = () => AppSettings.Default;
    private TabViewModel? _lastConsoleTab;

    public bool HasOpenConsole => _services.ToolWindows.LastOpen<TerminalViewModel>() is not null || ConsoleTabs().Any();

    private IEnumerable<(PanelSide Side, PanelTabsViewModel Tabs, TabViewModel Tab)> ConsoleTabs()
    {
        foreach (var tab in _workspace.Left.Tabs.Where(tab => tab.IsTerminal))
            yield return (PanelSide.Left, _workspace.Left, tab);
        foreach (var tab in _workspace.Right.Tabs.Where(tab => tab.IsTerminal))
            yield return (PanelSide.Right, _workspace.Right, tab);
    }

    internal async Task<bool> ConfirmConsoleTabsCloseAsync()
    {
        foreach (var (_, _, tab) in ConsoleTabs().ToArray())
            if (!await tab.Terminal!.CanCloseAsync()) return false;
        return true;
    }

    private bool HasConsoleTabShutdowns =>
        _workspace.Left.TerminalShutdowns.Count > 0 || _workspace.Right.TerminalShutdowns.Count > 0;

    private Task CloseConsoleTabsAsync()
    {
        var pending = _workspace.Left.TerminalShutdowns.Concat(_workspace.Right.TerminalShutdowns).ToArray();
        var tabs = ConsoleTabs().ToArray();
        foreach (var panel in tabs.Select(item => item.Tabs).Distinct())
            if (panel.Active.IsTerminal) panel.ActivateLastFileTab();
        return Task.WhenAll(pending.Concat(tabs.Select(item => item.Tabs.RemoveTerminalAsync(item.Tab))));
    }

    partial void RegisterConsole()
    {
        On(CommandIds.NetConsole, ShowConsoleAsync, () => ConsoleProfile() is not null || _services.Consoles?.Profiles.Count > 0);
        On(CommandIds.NetShowConsole, ShowOpenConsoleAsync, () => !_disposed && !_closing && HasOpenConsole);
        _services.ToolWindows.OpenWindowsChanged += OnToolWindowsChanged;
        OnDispose(() => _services.ToolWindows.OpenWindowsChanged -= OnToolWindowsChanged);
        _workspace.Left.StateChanged += OnToolWindowsChanged;
        _workspace.Right.StateChanged += OnToolWindowsChanged;
        OnDispose(() =>
        {
            _workspace.Left.StateChanged -= OnToolWindowsChanged;
            _workspace.Right.StateChanged -= OnToolWindowsChanged;
        });
        OnClose(CloseConsoleTabsAsync);
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
        if (_lastConsoleTab is not null && !ConsoleTabs().Any(item => ReferenceEquals(item.Tab, _lastConsoleTab)))
            _lastConsoleTab = null;
        OnPropertyChanged(nameof(HasOpenConsole));
        UpdateAvailability();
    }

    private Task ShowOpenConsoleAsync()
    {
        if (_closing || _disposed) return Task.CompletedTask;
        if (_services.ToolWindows.LastOpen<TerminalViewModel>() is { } terminal)
            _services.ToolWindows.Show(terminal);
        else
        {
            var tabs = ConsoleTabs().ToArray();
            var target = tabs.FirstOrDefault(item => ReferenceEquals(item.Tab, _lastConsoleTab));
            if (target.Tab is null) target = tabs.LastOrDefault();
            if (target.Tab is not null)
            {
                target.Tabs.Activate(target.Tab);
                _workspace.ActiveSide = target.Side;
            }
        }
        return Task.CompletedTask;
    }

    private ConnectionProfile? ConsoleProfile() =>
        Active?.CurrentPath.ProviderId is { } providerId && _services.Net?.ProfileOf(providerId) is { } profile &&
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
        var settings = ConsoleSettings();
        var embedded = _workspace.IsLoaded && settings.SshConsolePlacement == SshConsolePlacement.PanelTab;
        TerminalViewModel? terminal = null;
        terminal = new TerminalViewModel(profile.Name, () => consoles.Open(profile.Id), new WindowTerminalClipboard(),
            question => _services.ToolWindows.ShowDialogAsync(question, ownerModel: embedded ? null : terminal),
            _reportFailure) { FontSize = settings.FontSize };
        if (!embedded)
        {
            _services.ToolWindows.Show(terminal);
            return;
        }

        var side = settings.SshConsolePanel == SshConsoleSide.Left ? PanelSide.Left : PanelSide.Right;
        var tabs = side == PanelSide.Left ? _workspace.Left : _workspace.Right;
        _lastConsoleTab = tabs.OpenTerminal(terminal);
        _workspace.ActiveSide = side;
    }
}
