using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Editing;
using Tandem.App.Views.Editing;

namespace Tandem.App.Tests.Views;

/// <summary>The built-in editor (F4) through the real windows: the main window opens it, the
/// <see cref="WindowService"/> keeps it, and closing it or quitting asks about unsaved changes.</summary>
[Collection(LanguageCollection.Name)]
public class EditorWindowTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    /// <summary>The user goes back to the main window's file list. (The headless platform has one keyboard focus for
    /// all windows, which an editor window took.)</summary>
    private static void BackToMainWindow(TestApp app)
    {
        app.Window.Activate();
        app.PanelView(PanelSide.Left).FocusList();
        app.Pump();
    }

    private static async Task<EditorWindow> OpenEditorAsync(TestApp app, string name)
    {
        BackToMainWindow(app);
        app.Left.CursorIndex = IndexOf(app.Left, name);
        app.Pump();
        var before = app.Windows.Open.Count;
        app.Press(Key.F4, physical: PhysicalKey.F4);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.Count > before;
        });
        var editor = Assert.IsType<EditorWindow>(app.Windows.Open[^1]);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return editor;
    }

    private static TextEditor TextEditorOf(Window window) =>
        window.GetVisualDescendants().OfType<TextEditor>().Single();

    private static void Press(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physical,
        string? symbol = null)
    {
        window.KeyPress(key, modifiers, physical, symbol);
        if (window.IsVisible) window.KeyRelease(key, modifiers, physical, symbol); // the key may have closed it
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task F4_opens_an_editor_window_with_the_file_text()
    {
        await using var app = await TestApp.StartAsync();

        var window = await OpenEditorAsync(app, "notes.txt");

        Assert.True(window.IsVisible);
        var editor = TextEditorOf(window);
        Assert.Equal("notes", editor.Document.Text);
        Assert.True(editor.ShowLineNumbers);
        Assert.Equal("/home/notes.txt", window.Title);
        Assert.Empty(app.Failures);
        Assert.Empty(app.Shell.Calls);
    }

    [AvaloniaFact]
    public async Task Typing_marks_modified_and_Cmd_S_saves()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenEditorAsync(app, "notes.txt");

        Type(window, "X");
        Assert.True(window.Editor.IsModified);
        Assert.Equal("/home/notes.txt *", window.Title);

        Press(window, Key.S, RawInputModifiers.Meta, PhysicalKey.S, "s");
        await TestApp.WaitUntil(() =>
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            return !window.Editor.IsModified;
        });

        Assert.Equal("Xnotes", app.Fs.ReadAllText("/home/notes.txt"));
        Assert.Equal("/home/notes.txt", window.Title);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Closing_modified_editor_asks_and_Cancel_keeps_it_open()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenEditorAsync(app, "notes.txt");
        Type(window, "X");
        app.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesAnswer.Cancel);

        Press(window, Key.W, RawInputModifiers.Meta, PhysicalKey.W, "w");
        await TestApp.WaitUntil(() => app.Dialogs.SaveChangesRequests.Count == 1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(["notes.txt"], app.Dialogs.SaveChangesRequests);
        Assert.True(window.IsVisible);
        Assert.Contains(window, app.Windows.Open);

        app.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesAnswer.Discard);
        Press(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
        await TestApp.WaitUntil(() => app.Windows.Open.Count == 0);

        Assert.False(window.IsVisible);
        Assert.Equal("notes", app.Fs.ReadAllText("/home/notes.txt"));
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_search_panel_first_then_the_window()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenEditorAsync(app, "notes.txt");
        var search = TextEditorOf(window).SearchPanel;

        Press(window, Key.F, RawInputModifiers.Meta, PhysicalKey.F, "f");
        Assert.True(search.IsOpened);
        Press(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
        Assert.False(search.IsOpened);
        Assert.True(window.IsVisible);

        TextEditorOf(window).TextArea.Focus();
        Press(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
        await TestApp.WaitUntil(() => app.Windows.Open.Count == 0);
        Assert.Empty(app.Dialogs.SaveChangesRequests);
    }

    [AvaloniaFact]
    public async Task Enter_in_a_crlf_file_inserts_crlf()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/dos.txt", "one\r\ntwo");
        await using var app = await TestApp.StartAsync(fs);
        var window = await OpenEditorAsync(app, "dos.txt");
        TextEditorOf(window).CaretOffset = 3;

        Press(window, Key.Enter, RawInputModifiers.None, PhysicalKey.Enter);
        Assert.True(await window.Editor.SaveAsync());

        Assert.Equal("one\r\n\r\ntwo", app.Fs.ReadAllText(TestApp.Home + "/dos.txt"));
    }

    [AvaloniaFact]
    public async Task Closing_with_Save_saves_and_a_failed_save_keeps_the_window()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenEditorAsync(app, "notes.txt");
        Type(window, "X");
        app.Fs.FailWriteAfter("/home/.notes.txt.tandem-part", 0, () => new IOException("disk full"));
        app.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesAnswer.Save);

        window.Close();
        await TestApp.WaitUntil(() => window.Editor.Error is not null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.Equal("notes", app.Fs.ReadAllText("/home/notes.txt"));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == window.Editor.Error);

        app.Fs.FailWriteAfter("/home/.notes.txt.tandem-part", long.MaxValue, () => new IOException("never"));
        app.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesAnswer.Save);
        window.Close();
        await TestApp.WaitUntil(() => app.Windows.Open.Count == 0);

        Assert.Equal("Xnotes", app.Fs.ReadAllText("/home/notes.txt"));
    }

    [AvaloniaFact]
    public async Task Second_F4_on_same_file_reuses_the_window()
    {
        await using var app = await TestApp.StartAsync();
        var first = await OpenEditorAsync(app, "notes.txt");

        BackToMainWindow(app);
        app.Press(Key.F4, physical: PhysicalKey.F4);
        await Task.Delay(50);
        app.Pump();

        Assert.Equal([first], app.Windows.Open);
        Assert.True(first.IsActive);

        var second = await OpenEditorAsync(app, "README.md");
        Assert.Equal([first, second], app.Windows.Open);
    }

    [AvaloniaFact]
    public async Task Quit_with_modified_editor_can_be_cancelled()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenEditorAsync(app, "notes.txt");
        Type(window, "X");
        app.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesAnswer.Cancel);

        app.Window.Close();
        await TestApp.WaitUntil(() => app.Dialogs.SaveChangesRequests.Count == 1);
        app.Pump();

        Assert.True(app.Window.IsVisible);
        Assert.True(window.IsVisible);
        Assert.Empty(app.Dialogs.QuitRequests);

        app.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesAnswer.Discard);
        app.Window.Close();
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return !app.Window.IsVisible;
        });

        Assert.False(window.IsVisible);
        Assert.Empty(app.Windows.Open);
        Assert.Equal("notes", app.Fs.ReadAllText("/home/notes.txt"));
    }

    [AvaloniaFact]
    public async Task Quit_closes_unmodified_editors_without_asking()
    {
        await using var app = await TestApp.StartAsync();
        var window = await OpenEditorAsync(app, "notes.txt");

        app.Window.Close();
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return !app.Window.IsVisible;
        });

        Assert.False(window.IsVisible);
        Assert.Empty(app.Dialogs.SaveChangesRequests);
    }

    [AvaloniaFact]
    public async Task F4_on_too_large_file_shows_message()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/big.log", new byte[EditorViewModel.MaxFileBytes + 1]);
        await using var app = await TestApp.StartAsync(fs);
        app.Left.CursorIndex = IndexOf(app.Left, "big.log");
        app.Pump();

        app.Press(Key.F4, physical: PhysicalKey.F4);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Dialogs.Messages.Count == 1;
        });

        Assert.Equal("“big.log” is larger than 32 MB and cannot be opened in the editor. Use View (F3) instead.",
            app.Dialogs.Messages[0]);
        Assert.Empty(app.Windows.Open);
    }

    [AvaloniaFact]
    public async Task F4_is_off_on_a_folder()
    {
        await using var app = await TestApp.StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "docs");
        app.Pump();

        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileEdit));
        app.Press(Key.F4, physical: PhysicalKey.F4);
        await Task.Delay(20);
        app.Pump();

        Assert.Empty(app.Windows.Open);
    }
}
