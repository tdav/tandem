using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    }

    public InMemoryFileSystem Fs { get; }
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

    public static AppServices Services(InMemoryFileSystem fs, FakeVolumeService volumes)
    {
        var providers = new ProviderRegistry();
        providers.Register(fs);
        var platform = new FakePlatform();
        return new AppServices(providers, volumes, platform, platform, platform, platform, platform, platform,
            new OperationQueue(), new UnrestrictedAccessBroker());
    }

    public static async Task<TestApp> StartAsync(InMemoryFileSystem? fs = null, AppState? state = null,
        bool show = true)
    {
        fs ??= DefaultFileSystem();
        var stateDirectory = Path.Combine(Path.GetTempPath(), "tandem-window-tests-" + Guid.NewGuid().ToString("N"));
        new JsonStateStore(stateDirectory).Save(state ?? AppState.Default(Home) with { Language = "en" });

        var app = new TestApp(fs, new FakeVolumeService(), stateDirectory);
        app.Main = await Composition.StartAsync(Services(fs, app.Volumes), stateDirectory, Home,
            autosaveDelay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
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

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        Interlocked.Increment(ref Reads);
        return _volumes;
    }

    /// <summary>Replaces the volumes and raises <see cref="Changed"/> from a background thread, as the real one does.</summary>
    public Task ChangeAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        _volumes = volumes;
        return Task.Run(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public int HandlerCount => Changed?.GetInvocationList().Length ?? 0;
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
