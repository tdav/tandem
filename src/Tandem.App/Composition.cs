using System.Runtime.Versioning;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac;

namespace Tandem.App;

/// <summary>Everything the view models use from outside the app: file systems, platform services, the operation
/// queue, the error log. The app gets the real ones from <see cref="Composition.CreatePlatformServices"/>; tests pass in-memory and
/// fake ones.</summary>
public sealed record AppServices(
    ProviderRegistry Providers,
    IVolumeService Volumes,
    IShellIntegration Shell,
    IQuickLook QuickLook,
    IFileMetadataService Metadata,
    ITrash Trash,
    INativeCopy NativeCopy,
    IProcessRunner Processes,
    OperationQueue Operations,
    IFileAccessBroker AccessBroker,
    ErrorLog Log) : IDisposable
{
    /// <summary>Disposes the services that hold resources (the volume watch).</summary>
    public void Dispose()
    {
        if (Volumes is IDisposable volumes) volumes.Dispose();
    }
}

/// <summary>The composition root: builds the services and view models and starts them in the right order.</summary>
public static class Composition
{
    /// <summary>The real services: the local disk with unrestricted access, and the macOS implementations.</summary>
    [SupportedOSPlatform("macos")]
    public static AppServices CreatePlatformServices(ErrorLog log)
    {
        var broker = new UnrestrictedAccessBroker();
        var providers = new ProviderRegistry();
        providers.Register(new LocalFileSystemProvider(broker));
        var processes = new ProcessRunner();
        return new AppServices(providers, new MacVolumeService(), new MacShellIntegration(processes),
            new MacQuickLook(processes), new MacFileMetadataService(broker), new MacTrash(broker),
            new MacNativeCopy(broker), processes, new OperationQueue(), broker, log);
    }

    /// <summary>The main view model on the real services, its state kept in <paramref name="stateDirectory"/>. Not
    /// loaded yet: the caller sets the language first and awaits <see cref="WorkspaceViewModel.LoadAsync()"/>;
    /// <see cref="StartAsync"/> does both.</summary>
    [SupportedOSPlatform("macos")]
    public static MainViewModel Build(string stateDirectory, string homeDirectory) =>
        Build(CreatePlatformServices(new ErrorLog(DefaultLogFile(homeDirectory))), new JsonStateStore(stateDirectory),
            homeDirectory);

    /// <summary>Where the app writes unexpected errors: <c>~/Library/Logs/Tandem/tandem.log</c>.</summary>
    public static string DefaultLogFile(string homeDirectory) =>
        Path.Combine(homeDirectory, "Library", "Logs", "Tandem", "tandem.log");

    /// <summary>The main view model on <paramref name="services"/>; not loaded yet.</summary>
    public static MainViewModel Build(AppServices services, JsonStateStore store, string homeDirectory,
        Func<TimeSpan, CancellationToken, Task>? autosaveDelay = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var workspace = new WorkspaceViewModel(services.Providers, store, homeDirectory, autosaveDelay);
        return new MainViewModel(workspace, services, KeyMap.Default);
    }

    /// <summary>
    /// Starts the app's model in order: reads the saved state off the UI thread, applies its language before any
    /// view model text exists, builds the view models and opens the saved tabs. The result is loaded and ready to be
    /// shown. One <see cref="JsonStateStore"/> serves the whole session.
    /// </summary>
    public static async Task<MainViewModel> StartAsync(AppServices services, string stateDirectory,
        string homeDirectory, Func<TimeSpan, CancellationToken, Task>? autosaveDelay = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var store = new JsonStateStore(stateDirectory);
        var state = await Task.Run(() => store.Load(homeDirectory));
        Loc.SetLanguage(state.Language);
        var main = Build(services, store, homeDirectory, autosaveDelay);
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
