using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Vfs;
using Tandem.Net;

namespace Tandem.App.ViewModels.Net;

/// <summary>
/// The Network menu: connecting to a server, disconnecting, and the SSH console. The console's file
/// (<c>NetCommands.Console.cs</c>) registers its command through <see cref="On"/> and its cleanup through
/// <see cref="OnDispose"/> from <see cref="RegisterConsole"/>; a command not built yet stays unregistered, and its
/// menu item disabled.
/// </summary>
/// <remarks>
/// Created and used on the UI thread. The availability of every command registered with <see cref="On"/> is checked
/// again when the active panel changes, or its folder or error; a command whose availability depends on more calls
/// <see cref="UpdateAvailability"/> itself. Quitting the app awaits <see cref="CloseAsync"/> (for a while at most)
/// once the user has agreed to quit.
/// </remarks>
public sealed partial class NetCommands : ObservableObject, IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly AppServices _services;
    private readonly CommandRegistry _commands;
    private readonly Action<Exception> _reportFailure;
    private readonly Func<Func<bool>, IDisposable> _onEscape;
    private readonly List<IDisposable> _escapeHandlers = [];
    private readonly List<string> _ids = [];
    private readonly List<Action> _cleanups = [];
    private readonly List<Func<Task>> _closes = [];
    private PanelViewModel? _watched;
    private CancellationTokenSource? _connecting;
    private bool _disconnecting;
    private bool _closing;
    private bool[] _available = [];
    private bool _disposed;

    /// <param name="onEscape">Adds an Esc handler asked before the panel's keys (the main view model's
    /// <c>OnEscape</c>); disposing its result removes it.</param>
    public NetCommands(WorkspaceViewModel workspace, AppServices services, FileCommands files, CommandRegistry commands,
        Action<Exception> reportFailure, Func<Func<bool>, IDisposable> onEscape)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(files);
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _onEscape = onEscape ?? throw new ArgumentNullException(nameof(onEscape));

        On(CommandIds.NetConnect, OpenConnectionsAsync, CanOpenConnections);
        On(CommandIds.NetDisconnect, DisconnectCurrentAsync, CanDisconnect);
        if (services.Net is { } net)
        {
            net.Changed += OnConnectionsChanged;
            net.SecretSaveFailed += OnSecretSaveFailed;
            OnDispose(() =>
            {
                net.Changed -= OnConnectionsChanged;
                net.SecretSaveFailed -= OnSecretSaveFailed;
            });
            OnClose(() => Task.Run(async () => await net.DisposeAsync()));
        }
        RegisterConsole();
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        WatchPanel();
    }

    /// <summary>The console's file registers its command; called from the constructor.</summary>
    partial void RegisterConsole();

    /// <summary>True while a connection to a server is open: quitting then goes through the main view model's
    /// <c>ConfirmCloseAsync</c>, which awaits <see cref="CloseAsync"/>. Never true before connections exist.</summary>
    public bool HasOpenConnections => IsConnecting || _services.Net?.HasOpenConnections == true;

    public IReadOnlyList<ConnectionProfile> Connections => _services.Net?.Profiles ?? [];
    public bool HasConnections => Connections.Count > 0;
    public bool IsConnecting => _connecting is not null;

    public void CancelConnecting() => _connecting?.Cancel();

    /// <summary>Closes the network connections before the app quits: runs every close added with
    /// <see cref="OnClose"/> at once and completes when all have (a failure of one faults the task once all are
    /// done). Nothing to do while none was added.</summary>
    public Task CloseAsync()
    {
        _closing = true;
        CancelConnecting();
        UpdateAvailability();
        return Task.WhenAll(_closes.ToArray().Select(RunClose));
    }

    /// <summary>Adds work <see cref="CloseAsync"/> runs and waits for (closing connections, ending console
    /// sessions).</summary>
    public void OnClose(Func<Task> close)
    {
        ArgumentNullException.ThrowIfNull(close);
        _closes.Add(close);
    }

    /// <summary>Lets <paramref name="handler"/> take Esc before the panel does (while a connection is being made, so
    /// Esc cancels it): true takes the key. Disposing the result removes the handler; <see cref="Dispose"/> removes
    /// every one still registered.</summary>
    public IDisposable OnEscapeKey(Func<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = _onEscape(handler);
        _escapeHandlers.Add(registration);
        return new Registration(() =>
        {
            _escapeHandlers.Remove(registration);
            registration.Dispose();
        });
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }

    /// <summary>Registers the command <paramref name="id"/> and includes it in the availability checks.</summary>
    private void On(string id, Func<Task> run, Func<bool> canRun)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(canRun);
        if (!_ids.Contains(id)) _ids.Add(id);
        _commands.Register(id, run, canRun);
    }

    /// <summary>Adds work <see cref="Dispose"/> does: the cleanups run in the reverse order of their adding.</summary>
    private void OnDispose(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _cleanups.Add(cleanup);
    }

    /// <summary>The active panel; null until the workspace is loaded.</summary>
    private PanelViewModel? Active => _workspace.IsLoaded ? _workspace.ActivePanel : null;

    private bool CanConnect() => !_disposed && !_closing && !IsConnecting && !_disconnecting && Active is not null &&
        _services.Net is not null;

    private bool CanOpenConnections() => CanConnect() && _services.NetDialogs is not null;

    private bool CanDisconnect() => CanConnect() && Active is { } panel &&
        _services.Net?.ProfileOf(panel.CurrentPath.ProviderId) is not null;

    private bool CanOpenSavedConnection(ConnectionProfile? profile) => profile is not null && CanConnect();

    [RelayCommand(CanExecute = nameof(CanOpenSavedConnection))]
    private Task OpenSavedConnectionAsync(ConnectionProfile profile) => ConnectAsync(profile.Id);

    private async Task OpenConnectionsAsync()
    {
        if (!CanOpenConnections() || _services.Net is not { } net || _services.NetDialogs is not { } dialogs) return;
        var list = new ConnectionListViewModel(net, dialogs, BeforeDeleteAsync) { Selected = Connections.FirstOrDefault() };
        if (await dialogs.ShowConnectionsAsync(list) is { } id) await ConnectAsync(id);
    }

    public async Task ConnectAsync(Guid profileId)
    {
        if (!CanConnect() || Active is not { } panel || _services.Net is not { } net ||
            Connections.FirstOrDefault(profile => profile.Id == profileId) is not { } profile) return;

        var from = panel.CurrentPath;
        var notice = Loc.Format("Net_Connecting", profile.Address);
        using var cancellation = new CancellationTokenSource();
        using var escape = OnEscapeKey(() =>
        {
            cancellation.Cancel();
            return true;
        });
        _connecting = cancellation;
        panel.ShowNotice(notice);
        NotifyConnecting();
        try
        {
            var origin = from == default ? VfsPath.Local(_workspace.HomeDirectory)
                : _services.Providers.PersistentLocationOf(from);
            var destination = await Task.Run(() => net.ConnectAsync(profileId, origin, cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_disposed && !_closing && panel.CurrentPath == from && Panels().Contains(panel))
                await panel.NavigateToAsync(destination);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (NetAuthenticationException e) when (e.Declined) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!_closing && !cancellation.IsCancellationRequested)
                await _services.Dialogs.ShowMessageAsync(ConnectionFailure(e), e.Message);
        }
        catch (Exception e)
        {
            _reportFailure(e);
        }
        finally
        {
            if (panel.Notice == notice) panel.ShowNotice(null);
            _connecting = null;
            NotifyConnecting();
        }
    }

    private static string ConnectionFailure(Exception error) => error switch
    {
        NetAuthenticationException => Loc.Get("Net_AuthenticationFailed"),
        NetTrustException => Loc.Get("Net_TrustRefused"),
        NotSupportedException => Loc.Get("Net_UnsupportedProtocol"),
        _ => OperationTexts.ForFailure(error),
    };

    private async Task DisconnectCurrentAsync()
    {
        if (!CanDisconnect() || Active is not { } panel || _services.Net is not { } net) return;
        var id = panel.CurrentPath.ProviderId;
        _disconnecting = true;
        UpdateAvailability();
        try
        {
            if (await LeaveServersAsync(providerId => providerId == id))
                await Task.Run(() => net.DisconnectAsync(id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _services.Dialogs.ShowMessageAsync(OperationTexts.ForFailure(e), e.Message);
        }
        finally
        {
            _disconnecting = false;
            UpdateAvailability();
        }
    }

    private Task<bool> BeforeDeleteAsync(Guid id) => LeaveServersAsync(providerId =>
        _services.Net?.ProfileOf(providerId)?.Id == id);

    private async Task<bool> LeaveServersAsync(Func<string, bool> matches)
    {
        bool InUse() => _services.Operations.Operations.Any(operation =>
            operation.Sources.Any(path => ProviderChain(path).Any(matches)) ||
            operation.Destination is { } target && ProviderChain(target).Any(matches));

        if (InUse()) return await RefuseDisconnectAsync("Net_ConnectionInUse");
        var panels = Panels().Select(panel => (Panel: panel, ServerId: ProviderChain(panel.CurrentPath).FirstOrDefault(matches)))
            .Where(item => item.ServerId is not null).ToArray();
        foreach (var (panel, id) in panels)
        {
            if (!ProviderChain(panel.CurrentPath).Any(matches)) continue;
            var home = VfsPath.Local(_workspace.HomeDirectory);
            var origin = _services.Providers.UpOf(new VfsPath(id!, "/")) ?? new HostLocation(home, null);
            await panel.NavigateToAsync(origin.Directory, origin.FocusName);
            if (ProviderChain(panel.CurrentPath).Any(matches) && origin.Directory != home)
                await panel.NavigateToAsync(home);
        }

        if (InUse()) return await RefuseDisconnectAsync("Net_ConnectionInUse");
        return !Panels().Any(panel => ProviderChain(panel.CurrentPath).Any(matches)) ||
            await RefuseDisconnectAsync("Net_CouldNotLeaveConnection");
    }

    private async Task<bool> RefuseDisconnectAsync(string key)
    {
        await _services.Dialogs.ShowMessageAsync(Loc.Get(key));
        return false;
    }

    private IEnumerable<PanelViewModel> Panels() => _workspace.Left.Tabs.Concat(_workspace.Right.Tabs).Select(tab => tab.Panel);

    private IEnumerable<string> ProviderChain(VfsPath path)
    {
        var visited = new HashSet<VfsPath>();
        while (path.ProviderId is { } id && visited.Add(path))
        {
            yield return id;
            if (!_services.Providers.TryGet(id, out _) ||
                _services.Providers.HostLocationOf(path) is not { } host) yield break;
            path = host.Directory;
        }
    }

    private void OnConnectionsChanged(object? sender, EventArgs e)
    {
        void Update()
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(Connections));
            OnPropertyChanged(nameof(HasConnections));
            UpdateAvailability();
        }

        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    private void OnSecretSaveFailed(object? sender, Exception e) => Dispatcher.UIThread.Post(async () =>
    {
        if (_disposed || _closing) return;
        try { await _services.Dialogs.ShowMessageAsync(Loc.Get("Net_SecretSaveFailed")); }
        catch (Exception error) { _reportFailure(error); }
    });

    private void NotifyConnecting()
    {
        OnPropertyChanged(nameof(IsConnecting));
        UpdateAvailability();
    }

    /// <summary>Runs the cleanups (last added first; one that throws is reported and the others still run) and stops
    /// watching the panel and its Esc handlers. Closes no connection: that is <see cref="CloseAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelConnecting();
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        if (_watched is not null) _watched.PropertyChanged -= OnPanelPropertyChanged;
        _watched = null;
        foreach (var registration in _escapeHandlers.ToArray()) registration.Dispose();
        _escapeHandlers.Clear();
        for (var i = _cleanups.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanups[i]();
            }
            catch (Exception e)
            {
                _reportFailure(e);
            }
        }
        _cleanups.Clear();
    }

    private static async Task RunClose(Func<Task> close) => await close();

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ActivePanel)) WatchPanel();
    }

    private void WatchPanel()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnPanelPropertyChanged;
        _watched = Active;
        if (_watched is not null) _watched.PropertyChanged += OnPanelPropertyChanged;
        UpdateAvailability();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelViewModel.CurrentPath) or nameof(PanelViewModel.Error)) UpdateAvailability();
    }

    /// <summary>Checks every registered command's availability; raises the registry's change only when one
    /// changed.</summary>
    private void UpdateAvailability()
    {
        OpenSavedConnectionCommand.NotifyCanExecuteChanged();
        var available = _ids.Select(_commands.CanExecute).ToArray();
        if (available.SequenceEqual(_available)) return;
        _available = available;
        _commands.RaiseCanExecuteChanged();
    }
}
