using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.Tests.Views;

/// <summary>
/// The whole app on an in-memory file system standing in for the local disk, fake platform services and a temporary
/// state folder: nothing touches the real home folder, real volumes or ~/Library.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string Home = "/home";

    private TestApp(InMemoryFileSystem fs, FakeVolumeService volumes, string stateDirectory)
    {
        Fs = fs;
        Volumes = volumes;
        StateDirectory = stateDirectory;
        Trash = new InMemoryTrash(fs);
    }

    public InMemoryFileSystem Fs { get; }

    /// <summary>Answers every dialog; a dialog without a scripted answer is cancelled.</summary>
    public ScriptedDialogs Dialogs { get; } = new();

    /// <summary>The Trash: items "moved" there are removed from <see cref="Fs"/> and listed.</summary>
    public InMemoryTrash Trash { get; }

    /// <summary>Records every shell call; launches nothing.</summary>
    public RecordingShell Shell { get; } = new();

    /// <summary>Records Quick Look calls; shows nothing.</summary>
    public RecordingQuickLook QuickLook { get; } = new();

    /// <summary>Tags and owners kept in memory; never touches real files.</summary>
    public FakeMetadata Metadata { get; } = new();

    public FakeVolumeService Volumes { get; }
    public string StateDirectory { get; }
    public MainViewModel Main { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public List<Exception> Failures { get; } = [];

    public PanelViewModel Left => Main.Workspace.Left.Active.Panel;
    public PanelViewModel Right => Main.Workspace.Right.Active.Panel;

    /// <summary>Home holds two folders and three files; /other holds one file.</summary>
    public static InMemoryFileSystem DefaultFileSystem()
    {
        var fs = new InMemoryFileSystem(VfsPath.LocalProviderId);
        fs.AddDirectory(Home + "/docs");
        fs.AddDirectory(Home + "/src");
        fs.AddFile(Home + "/README.md", "readme");
        fs.AddFile(Home + "/report.pdf", "report");
        fs.AddFile(Home + "/notes.txt", "notes");
        fs.AddFile("/other/inside.txt");
        return fs;
    }

    /// <summary>The error log of this app, in its temporary state folder.</summary>
    public string LogFile => Path.Combine(StateDirectory, "logs", "tandem.log");

    /// <param name="trashCapable">Whether the in-memory "local disk" says it has a Trash (the real one does).</param>
    /// <param name="extraProviders">Providers besides the "local disk", e.g. a non-local one.</param>
    public static AppServices Services(InMemoryFileSystem fs, FakeVolumeService volumes, string logFile,
        IDialogService? dialogs = null, ITrash? trash = null, bool trashCapable = false,
        ProviderCapabilities removedCapabilities = ProviderCapabilities.None, IShellIntegration? shell = null,
        IQuickLook? quickLook = null, IFileMetadataService? metadata = null,
        IReadOnlyList<IFileSystemProvider>? extraProviders = null)
    {
        var providers = new ProviderRegistry();
        var added = trashCapable ? ProviderCapabilities.Trash : ProviderCapabilities.None;
        providers.Register(added == ProviderCapabilities.None && removedCapabilities == ProviderCapabilities.None
            ? fs
            : new CapabilityProvider(fs, added, removedCapabilities));
        foreach (var provider in extraProviders ?? []) providers.Register(provider);
        var platform = new FakePlatform();
        return new AppServices(providers, volumes, shell ?? platform, quickLook ?? platform, metadata,
            trash ?? platform,
            new InMemoryNativeCopy(fs), platform, new OperationQueue(), new UnrestrictedAccessBroker(),
            new ErrorLog(logFile), dialogs ?? new ScriptedDialogs());
    }

    /// <param name="progressWindowDelay">How long before the progress window appears; never, when null.</param>
    public static async Task<TestApp> StartAsync(InMemoryFileSystem? fs = null, AppState? state = null,
        bool show = true, bool trashCapable = false,
        Func<TimeSpan, CancellationToken, Task>? progressWindowDelay = null,
        ProviderCapabilities removedCapabilities = ProviderCapabilities.None,
        Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null,
        IReadOnlyList<IFileSystemProvider>? extraProviders = null, bool withMetadata = true)
    {
        fs ??= DefaultFileSystem();
        var stateDirectory = Path.Combine(Path.GetTempPath(), "tandem-window-tests-" + Guid.NewGuid().ToString("N"));
        new JsonStateStore(stateDirectory).Save(state ?? AppState.Default(Home) with { Language = "en" });

        var app = new TestApp(fs, new FakeVolumeService(), stateDirectory);
        app.Main = await Composition.StartAsync(
            Services(fs, app.Volumes, app.LogFile, app.Dialogs, app.Trash, trashCapable, removedCapabilities,
                app.Shell, app.QuickLook, withMetadata ? app.Metadata : null, extraProviders),
            stateDirectory, Home,
            autosaveDelay: (_, ct) => Task.Delay(Timeout.Infinite, ct),
            progressWindowDelay: progressWindowDelay ?? ((_, ct) => Task.Delay(Timeout.Infinite, ct)),
            quickLookDelay: quickLookDelay ?? ((_, _) => Task.CompletedTask));
        app.Main.CommandFailed += (_, e) => app.Failures.Add(e);
        if (show) app.ShowWindow();
        return app;
    }

    public void ShowWindow()
    {
        Window = new MainWindow(Main, screens: null);
        Window.Show();
        Pump();
    }

    public async ValueTask DisposeAsync()
    {
        if (Window is { } window) window.Close();
        Main.Dispose();
        await Task.Yield();
        try
        {
            Directory.Delete(StateDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing saved
        }
    }

    /// <summary>Runs queued UI work and layout.</summary>
    public void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Window?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    public T Find<T>(string name) where T : Control =>
        Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    public PanelView PanelView(PanelSide side) =>
        Window.GetVisualDescendants().OfType<PanelView>().Single(p => (p.DataContext as SideViewModel)?.Side == side);

    public ListBox FileList(PanelSide side) =>
        PanelView(side).GetVisualDescendants().OfType<FileListView>().Single()
            .GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Rows");

    public void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None,
        PhysicalKey physical = PhysicalKey.None, string? symbol = null)
    {
        Window.KeyPress(key, modifiers, physical, symbol);
        Window.KeyRelease(key, modifiers, physical, symbol);
        Pump();
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Pump();
    }

    public void Click(Control control, RawInputModifiers modifiers = RawInputModifiers.None,
        MouseButton button = MouseButton.Left)
    {
        var point = CenterOf(control);
        Window.MouseDown(point, button, modifiers);
        Window.MouseUp(point, button, modifiers);
        Pump();
    }

    public Point CenterOf(Control control)
    {
        var bounds = control.Bounds;
        return control.TranslatePoint(new Point(bounds.Width / 2, bounds.Height / 2), Window)
               ?? throw new InvalidOperationException("The control is not in the window.");
    }

    public IInputElement? Focused => Window.FocusManager?.GetFocusedElement();
}

internal sealed class FakeVolumeService : IVolumeService
{
    private volatile IReadOnlyList<VolumeInfo> _volumes =
        [new VolumeInfo("Macintosh HD", "/", 1000L * 1024 * 1024 * 1024, 250L * 1024 * 1024 * 1024, false)];

    public event EventHandler? Changed;

    public int Reads;

    /// <summary>When set, <see cref="GetVolumes"/> throws it.</summary>
    public volatile Exception? Failure;

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        Interlocked.Increment(ref Reads);
        if (Failure is { } failure) throw failure;
        return _volumes;
    }

    /// <summary>The handlers subscribed now, to raise later as a late event would.</summary>
    public EventHandler? Handlers => Changed;

    /// <summary>Replaces the volumes and raises <see cref="Changed"/> from a background thread, as the real one does.</summary>
    public Task ChangeAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        _volumes = volumes;
        return Task.Run(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public int HandlerCount => Changed?.GetInvocationList().Length ?? 0;
}

/// <summary>The Trash over the in-memory "local disk": an item moved there is deleted and remembered.</summary>
internal sealed class InMemoryTrash(InMemoryFileSystem fs) : ITrash
{
    private readonly Lock _lock = new();
    private readonly List<string> _items = [];

    public IReadOnlyList<string> Items
    {
        get { lock (_lock) return _items.ToArray(); }
    }

    public async Task MoveToTrashAsync(string localPath, CancellationToken ct)
    {
        await fs.DeleteAsync(VfsPath.Local(localPath), recursive: true, ct);
        lock (_lock) _items.Add(localPath);
    }
}

/// <summary>The platform copy over the in-memory "local disk" (its provider id is "local", so the engine uses it).</summary>
internal sealed class InMemoryNativeCopy(InMemoryFileSystem fs) : INativeCopy
{
    public async Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        CancellationToken ct)
    {
        await using var source = await fs.OpenReadAsync(VfsPath.Local(sourcePath), ct);
        await using var destination = await fs.OpenWriteAsync(VfsPath.Local(destinationPath), ct);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesCopied.Report(total += read);
        }
    }
}

/// <summary>A provider with capabilities added to or taken from another's, e.g. the Trash for the in-memory "local
/// disk", or no writing for a read-only source.</summary>
internal sealed class CapabilityProvider(IFileSystemProvider inner, ProviderCapabilities extra,
    ProviderCapabilities removed = ProviderCapabilities.None) : IFileSystemProvider
{
    public string Id => inner.Id;
    public ProviderCapabilities Capabilities => (inner.Capabilities | extra) & ~removed;
    public IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory, CancellationToken ct) =>
        inner.EnumerateAsync(directory, ct);
    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => inner.GetEntryAsync(path, ct);
    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        inner.DeleteAsync(path, recursive, ct);
    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        inner.MoveAsync(source, destination, ct);
    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
        inner.CreateSymlinkAsync(path, target, ct);
    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
        inner.SetModifiedAsync(path, modified, ct);
    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => inner.SetModeAsync(path, mode, ct);
    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) => inner.IsSameEntryAsync(a, b, ct);
    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => inner.GetVolumeKeyAsync(path, ct);
    public IDisposable? Watch(VfsPath directory, Action changed) => inner.Watch(directory, changed);
}

/// <summary>Records every shell call instead of launching anything. A failure set in <see cref="Failure"/> is thrown
/// by every launching call; the apps reported for any item are <see cref="Apps"/>.</summary>
internal sealed class RecordingShell : IShellIntegration
{
    public List<(string Call, string Path)> Calls { get; } = [];
    public List<(string Path, AppInfo App)> OpenedWith { get; } = [];
    public List<string> AppQueries { get; } = [];
    public IReadOnlyList<AppInfo> Apps { get; set; } = [];
    public Exception? Failure { get; set; }

    private void Record(string call, string path)
    {
        if (!path.StartsWith('/') && call != "settings") throw new ArgumentException("Relative path.", nameof(path));
        if (Failure is { } failure) throw failure;
        Calls.Add((call, path));
    }

    public void OpenDefault(string path) => Record("open", path);
    public void OpenInTextEditor(string path) => Record("edit", path);
    public IReadOnlyList<AppInfo> GetAppsFor(string path)
    {
        AppQueries.Add(path);
        return Apps;
    }
    public void OpenWith(string path, AppInfo app)
    {
        Record("openWith", path);
        OpenedWith.Add((path, app));
    }
    public void RevealInFinder(string path) => Record("reveal", path);
    public void OpenTerminal(string directory) => Record("terminal", directory);
    public void OpenFullDiskAccessSettings() => Record("settings", "");
}

/// <summary>Records Quick Look calls: <see cref="Toggles"/> holds the paths of each toggle, <see cref="Closes"/> counts
/// closes; <see cref="IsOpen"/> follows them as the real preview would.</summary>
internal sealed class RecordingQuickLook : IQuickLook
{
    public List<IReadOnlyList<string>> Toggles { get; } = [];
    public int Closes { get; private set; }
    public bool IsOpen { get; set; }
    public Exception? Failure { get; set; }

    public void Toggle(IReadOnlyList<string> paths)
    {
        if (Failure is { } failure) throw failure;
        Toggles.Add(paths.ToArray());
        if (IsOpen) IsOpen = false;
        else if (paths.Count > 0) IsOpen = true;
    }

    public void Close()
    {
        if (!IsOpen) return;
        Closes++;
        IsOpen = false;
    }
}

/// <summary>Tags and owners in memory, by path. <see cref="Failure"/>, when set, is thrown by every call.</summary>
internal sealed class FakeMetadata : IFileMetadataService
{
    public Dictionary<string, IReadOnlyList<string>> Tags { get; } = new(StringComparer.Ordinal);
    public List<(string Path, IReadOnlyList<string> Tags)> SetCalls { get; } = [];
    public OwnerInfo Owner { get; set; } = new("user", "staff");
    public List<string> OwnerQueries { get; } = [];
    public Exception? Failure { get; set; }
    public Exception? SetFailure { get; set; }

    public IReadOnlyList<string> GetTags(string path)
    {
        if (Failure is { } failure) throw failure;
        return Tags.GetValueOrDefault(path) ?? [];
    }

    public void SetTags(string path, IReadOnlyList<string> tags)
    {
        if ((SetFailure ?? Failure) is { } failure) throw failure;
        SetCalls.Add((path, tags.ToArray()));
        Tags[path] = tags.ToArray();
    }

    public OwnerInfo GetOwner(string path)
    {
        if (Failure is { } failure) throw failure;
        OwnerQueries.Add(path);
        return Owner;
    }
}

/// <summary>Platform services this task does not use; each call fails loudly.</summary>
internal sealed class FakePlatform : IShellIntegration, IQuickLook, IFileMetadataService, ITrash, INativeCopy,
    IProcessRunner
{
    private static NotSupportedException NotHere() => new("Not used by the main window tests.");

    public void OpenDefault(string path) => throw NotHere();
    public void OpenInTextEditor(string path) => throw NotHere();
    public IReadOnlyList<AppInfo> GetAppsFor(string path) => throw NotHere();
    public void OpenWith(string path, AppInfo app) => throw NotHere();
    public void RevealInFinder(string path) => throw NotHere();
    public void OpenTerminal(string directory) => throw NotHere();
    public void OpenFullDiskAccessSettings() => throw NotHere();
    public bool IsOpen => false;
    public void Toggle(IReadOnlyList<string> paths) => throw NotHere();
    public void Close() { }
    public IReadOnlyList<string> GetTags(string path) => throw NotHere();
    public void SetTags(string path, IReadOnlyList<string> tags) => throw NotHere();
    public OwnerInfo GetOwner(string path) => throw NotHere();
    public Task MoveToTrashAsync(string localPath, CancellationToken ct) => throw NotHere();
    public Task CopyFileAsync(string sourcePath, string destinationPath, IProgress<long> bytesCopied,
        CancellationToken ct) => throw NotHere();
    public IRunningProcess Start(ProcessSpec spec) => throw NotHere();
}
