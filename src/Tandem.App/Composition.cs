using System.Runtime.Versioning;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.Archives;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Search;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac;

namespace Tandem.App;

/// <summary>Everything the view models use from outside the app: file systems, platform services, the operation
/// queue, the error log. The app gets the real ones from <see cref="Composition.CreatePlatformServices"/>; tests pass in-memory and
/// fake ones. Without <see cref="Metadata"/> the properties dialog shows tags and owner as unavailable.
/// <see cref="Temp"/> holds the copies of files opened from providers that are not the local disk; it is emptied when
/// it is created (at start) and when the services are disposed (at exit). <see cref="Windows"/> shows the editor windows
/// (and later the other non-modal windows). Without <see cref="Quarantine"/> nothing taken out of an archive gets the
/// archive's quarantine mark (<see cref="QuarantineCarrier"/>).</summary>
public sealed record AppServices(
    ProviderRegistry Providers,
    IVolumeService Volumes,
    IShellIntegration Shell,
    IQuickLook QuickLook,
    IFileMetadataService? Metadata,
    ITrash Trash,
    INativeCopy NativeCopy,
    IProcessRunner Processes,
    OperationQueue Operations,
    IFileAccessBroker AccessBroker,
    ErrorLog Log,
    IDialogService Dialogs,
    TempFileStore Temp,
    IWindowService Windows,
    IQuarantine? Quarantine = null) : IDisposable
{
    /// <summary>Disposes the services that hold resources (the volume watch) and empties <see cref="Temp"/>.</summary>
    public void Dispose()
    {
        if (Volumes is IDisposable volumes) volumes.Dispose();
        Temp.Dispose();
    }
}

/// <summary>The composition root: builds the services and view models and starts them in the right order.</summary>
public static class Composition
{
    /// <summary>The real services: the local disk with unrestricted access, the macOS implementations, and the
    /// temporary files in <see cref="TempFileStore.DefaultDirectory"/> of <paramref name="homeDirectory"/> (emptied
    /// now).</summary>
    [SupportedOSPlatform("macos")]
    public static AppServices CreatePlatformServices(ErrorLog log, string homeDirectory)
    {
        var broker = new UnrestrictedAccessBroker();
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(broker, new MacExclusiveRename()));
        var processes = new ProcessRunner();
        var dialogs = new DialogService();
        return new AppServices(providers, new MacVolumeService(), new MacShellIntegration(processes),
            new MacQuickLook(processes), new MacFileMetadataService(broker), new MacTrash(broker),
            new MacNativeCopy(broker), processes, new OperationQueue(), broker, log, dialogs,
            new TempFileStore(TempFileStore.DefaultDirectory(homeDirectory)), new WindowService(dialogs, providers),
            new MacQuarantine(broker));
    }

    /// <summary>Where the app writes unexpected errors: <c>~/Library/Logs/Tandem/tandem.log</c>.</summary>
    public static string DefaultLogFile(string homeDirectory) =>
        Path.Combine(homeDirectory, "Library", "Logs", "Tandem", "tandem.log");

    /// <summary>The main view model on <paramref name="services"/> and <paramref name="settings"/>; not loaded
    /// yet. Registers a <see cref="SearchResultsProvider"/> in <see cref="AppServices.Providers"/> unless one with its id
    /// is there.</summary>
    /// <param name="progressWindowDelay">How long an operation runs before the progress window appears; see
    /// <see cref="OperationsViewModel"/>.</param>
    /// <param name="quickLookDelay">How long the cursor rests before an open Quick Look or quick-view panel follows
    /// it; see <see cref="ShellCommands"/> and <see cref="ViewModels.Viewing.QuickViewViewModel"/>.</param>
    public static MainViewModel Build(AppServices services, JsonStateStore store, SettingsService settings,
        string homeDirectory,
        Func<TimeSpan, CancellationToken, Task>? autosaveDelay = null,
        Func<TimeSpan, CancellationToken, Task>? progressWindowDelay = null,
        Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Providers.TryGet(SearchResultsProvider.ProviderId, out _))
            services.Providers.Register(new SearchResultsProvider(services.Providers));
        var workspace = new WorkspaceViewModel(services.Providers, store, homeDirectory, autosaveDelay,
            e => services.Log.Write(e, "settings"));
        var mounts = CreateArchiveMounts(services);
        try
        {
            return new MainViewModel(workspace, services, settings, progressWindowDelay, quickLookDelay, mounts);
        }
        catch
        {
            mounts.Dispose(); // no view model owns them
            throw;
        }
    }

    /// <summary>The archive mounts over <paramref name="services"/>, asking passwords through its dialogs, added to
    /// its providers as a mounter (after any added before). The main view model owns and disposes them; the app's
    /// exit disposes the main view model.</summary>
    public static ArchiveMounts CreateArchiveMounts(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var mounts = new ArchiveMounts(services.Providers, services.Temp,
            new DialogArchivePasswordPrompt(services.Dialogs));
        services.Providers.AddMounter(mounts);
        return mounts;
    }

    /// <summary>
    /// Starts the app's model in order: reads the saved state and the settings (<c>settings.json</c>, beside
    /// <c>state.json</c>) off the UI thread, applies the language before any view model text exists, builds the view
    /// models and opens the saved tabs. The result is loaded and ready to be shown. One <see cref="JsonStateStore"/>
    /// and one <see cref="SettingsService"/> serve the whole session; a failed settings save is logged.
    /// </summary>
    public static async Task<MainViewModel> StartAsync(AppServices services, string stateDirectory,
        string homeDirectory, Func<TimeSpan, CancellationToken, Task>? autosaveDelay = null,
        Func<TimeSpan, CancellationToken, Task>? progressWindowDelay = null,
        Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var store = new JsonStateStore(stateDirectory);
        var settings = new SettingsService(new JsonSettingsStore(stateDirectory),
            e => services.Log.Write(e, "settings"));
        var state = await Task.Run(() =>
        {
            settings.Load();
            return store.Load(homeDirectory);
        });
        Loc.SetLanguage(state.Language);
        var main = Build(services, store, settings, homeDirectory, autosaveDelay, progressWindowDelay, quickLookDelay);
        try
        {
            await main.Workspace.LoadAsync(state);
        }
        catch
        {
            main.Dispose();
            throw;
        }
        return main;
    }
}
