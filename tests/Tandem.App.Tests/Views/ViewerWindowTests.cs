using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;
using Tandem.App.Views.Viewing;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.Views;

/// <summary>The built-in viewer (F3) through the real windows: the main window opens it for the file under the
/// cursor, its own keys page and close it, and Quick Look stays on Cmd+Y.</summary>
[Collection(LanguageCollection.Name)]
public class ViewerWindowTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    /// <summary>Home of the default file system, plus "lines.txt": "line 001" … "line 300".</summary>
    private static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/lines.txt",
            string.Concat(Enumerable.Range(1, 300).Select(i => $"line {i:D3}\n")));
        return fs;
    }

    private static async Task<ViewerWindow> OpenViewerAsync(TestApp app, string name)
    {
        app.Left.CursorIndex = IndexOf(app.Left, name);
        app.Pump();
        app.Press(Key.F3, physical: PhysicalKey.F3);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.OfType<ViewerWindow>().Any();
        });
        var window = app.Windows.Open.OfType<ViewerWindow>().Single();
        await window.Viewer.IdleAsync();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Press(Window window, Key key, PhysicalKey physical,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        if (window.IsVisible) window.KeyRelease(key, modifiers, physical, null); // the key may have closed it
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task F3_opens_viewer_window_for_cursor_file()
    {
        await using var app = await TestApp.StartAsync(FileSystem());

        var window = await OpenViewerAsync(app, "notes.txt");

        Assert.Equal("/home/notes.txt", window.Title);
        Assert.Equal(["notes"], window.Viewer.Lines);
        var text = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "PageText");
        Assert.Equal("notes", text.Text);
        Assert.Empty(app.QuickLook.Toggles);
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task F3_on_folder_does_nothing()
    {
        await using var app = await TestApp.StartAsync(FileSystem());
        app.Left.CursorIndex = IndexOf(app.Left, "docs");
        app.Pump();

        app.Press(Key.F3, physical: PhysicalKey.F3);
        app.Left.CursorIndex = IndexOf(app.Left, "..");
        app.Pump();
        app.Press(Key.F3, physical: PhysicalKey.F3);
        await Task.Delay(50);
        app.Pump();

        Assert.Empty(app.Windows.Open);
        Assert.Empty(app.QuickLook.Toggles);
        Assert.Equal("/home", app.Left.CurrentPath.Path);
    }

    [AvaloniaTheory]
    [InlineData(Key.Escape, PhysicalKey.Escape, RawInputModifiers.None)]
    [InlineData(Key.F3, PhysicalKey.F3, RawInputModifiers.None)]
    [InlineData(Key.W, PhysicalKey.W, RawInputModifiers.Meta)]
    public async Task Esc_closes_the_viewer(Key key, PhysicalKey physical, RawInputModifiers modifiers)
    {
        await using var app = await TestApp.StartAsync(FileSystem());
        var window = await OpenViewerAsync(app, "lines.txt");

        Press(window, key, physical, modifiers);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.Count == 0;
        });

        Assert.False(window.IsVisible);
        Assert.Single(app.Main.Workspace.Left.Tabs); // Cmd+W closed the viewer, not a tab
        await window.Viewer.ScrollLinesAsync(1); // disposed: nothing happens
        Assert.Equal("line 001", window.Viewer.Lines[0]);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_closes_the_file_and_releases_its_temporary_copy()
    {
        var provider = new Tandem.App.Tests.ViewModels.StreamProvider();
        var content = "packed text\n"u8.ToArray();
        provider.Add("packed.txt", content.Length,
            () => new Tandem.App.Tests.ViewModels.TestStream(new MemoryStream(content), false));
        await using var app = await TestApp.StartAsync(FileSystem(), extraProviders: [provider]);
        await app.Left.NavigateToAsync(new Tandem.Core.Vfs.VfsPath(provider.Id, "/"));
        app.PanelView(PanelSide.Left).FocusList();
        var window = await OpenViewerAsync(app, "packed.txt");
        Assert.Equal(["packed text"], window.Viewer.Lines);
        var copy = Assert.Single(Directory.EnumerateFileSystemEntries(app.Temp.Directory));
        var copyFile = Directory.EnumerateFiles(copy).Single();

        Press(window, Key.Escape, PhysicalKey.Escape);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return !Directory.EnumerateFileSystemEntries(app.Temp.Directory).Any();
        });

        Assert.Empty(app.Windows.Open);
        Assert.False(File.Exists(copyFile));
        Assert.All(provider.Opened, s => Assert.True(((Tandem.App.Tests.ViewModels.TestStream)s).IsDisposed));
    }

    [AvaloniaFact]
    public async Task Cmd_Y_still_toggles_quick_look()
    {
        await using var app = await TestApp.StartAsync(FileSystem());
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();

        app.Press(Key.Y, RawInputModifiers.Meta, PhysicalKey.Y, "y");

        Assert.True(app.QuickLook.IsOpen);
        Assert.Equal([["/home/notes.txt"]], app.QuickLook.Toggles);
        Assert.Empty(app.Windows.Open);
    }

    [AvaloniaFact]
    public async Task PageDown_in_viewer_scrolls_a_page()
    {
        await using var app = await TestApp.StartAsync(FileSystem());
        var window = await OpenViewerAsync(app, "lines.txt");
        var viewer = window.Viewer;
        var rows = viewer.PageLines;
        Assert.InRange(rows, 2, 299);
        Assert.NotEqual(Tandem.App.ViewModels.Viewing.ViewerViewModel.DefaultPageLines, rows); // set by the view
        Assert.Equal(rows, viewer.Lines.Count); // the page fills the view's height

        Press(window, Key.PageDown, PhysicalKey.PageDown);
        await viewer.IdleAsync();
        Assert.Equal($"line {rows + 1:D3}", viewer.Lines[0]);

        Press(window, Key.Down, PhysicalKey.ArrowDown);
        await viewer.IdleAsync();
        Assert.Equal($"line {rows + 2:D3}", viewer.Lines[0]);

        Press(window, Key.End, PhysicalKey.End);
        await viewer.IdleAsync();
        Assert.Equal("line 300", viewer.Lines[^1]);

        Press(window, Key.D2, PhysicalKey.Digit2, RawInputModifiers.Meta);
        await viewer.IdleAsync();
        Assert.Equal(Tandem.App.ViewModels.Viewing.ViewerMode.Hex, viewer.Mode);

        Press(window, Key.Home, PhysicalKey.Home);
        await viewer.IdleAsync();
        Assert.Equal(0, viewer.Offset);
    }
}
