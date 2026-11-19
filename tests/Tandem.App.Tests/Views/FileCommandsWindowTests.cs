using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tandem.App.Tests.ViewModels;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

/// <summary>The file commands driven through the real window with key presses.</summary>
[Collection(LanguageCollection.Name)]
public class FileCommandsWindowTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static string[] ListNames(ListBox list) =>
        list.Items.OfType<EntryRowViewModel>().Select(r => r.Name).ToArray();

    private static async Task<TestApp> StartAsync(Func<TimeSpan, CancellationToken, Task>? progressDelay = null)
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory("/dst");
        var app = await TestApp.StartAsync(fs, progressWindowDelay: progressDelay);
        await app.Right.NavigateToAsync(VfsPath.Local("/dst"));
        app.Pump();
        return app;
    }

    private static async Task Settle(TestApp app)
    {
        await app.Main.Files.WhenIdleAsync();
        app.Pump();
    }

    private static InlineRenameEditor? Editor(TestApp app) =>
        app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<InlineRenameEditor>().SingleOrDefault();

    [AvaloniaFact]
    public async Task F5_key_asks_copies_and_updates_both_panels()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();
        app.Dialogs.AnswerCopyMove();

        app.Press(Key.F5, physical: PhysicalKey.F5);
        await Settle(app);

        Assert.Equal("/dst/notes.txt", Assert.Single(app.Dialogs.CopyMoveRequests).Destination);
        Assert.Equal("notes", app.Fs.ReadAllText("/dst/notes.txt"));
        Assert.Contains("notes.txt", ListNames(app.FileList(PanelSide.Right)));
        Assert.Contains("notes.txt", ListNames(app.FileList(PanelSide.Left)));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F6_key_moves_and_both_lists_change()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "report.pdf");
        app.Pump();
        app.Dialogs.AnswerCopyMove();

        app.Press(Key.F6, physical: PhysicalKey.F6);
        await Settle(app);

        Assert.DoesNotContain("report.pdf", ListNames(app.FileList(PanelSide.Left)));
        Assert.Contains("report.pdf", ListNames(app.FileList(PanelSide.Right)));
    }

    [AvaloniaFact]
    public async Task F7_key_creates_the_folder_and_puts_the_list_cursor_on_it()
    {
        await using var app = await StartAsync();
        app.Dialogs.NameAnswers.Enqueue("Projects");

        app.Press(Key.F7, physical: PhysicalKey.F7);
        await Settle(app);

        Assert.True(app.Fs.Exists("/home/Projects"));
        var list = app.FileList(PanelSide.Left);
        Assert.Equal("Projects", app.Left.CursorRow?.Name);
        Assert.Equal(app.Left.CursorIndex, list.SelectedIndex);
        Assert.True(list.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task F8_key_confirms_and_deletes_permanently_without_a_trash()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();
        app.Dialogs.DeleteAnswers.Enqueue(true);

        app.Press(Key.F8, physical: PhysicalKey.F8);
        await Settle(app);

        Assert.Equal((1, "notes.txt", true), Assert.Single(app.Dialogs.DeleteRequests));
        Assert.DoesNotContain("notes.txt", ListNames(app.FileList(PanelSide.Left)));
    }

    [AvaloniaFact]
    public async Task Shift_F6_renames_inline_with_the_name_part_selected()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();

        app.Press(Key.F6, RawInputModifiers.Shift, PhysicalKey.F6);
        app.Pump();

        var editor = Editor(app);
        Assert.NotNull(editor);
        Assert.Same(editor, app.Focused);
        Assert.Equal("notes.txt", editor.Text);
        Assert.Equal((0, 5), (editor.SelectionStart, editor.SelectionEnd));

        // Typing replaces the selected part; Space and letters stay in the editor (no marking, no quick search).
        app.Type("memo");
        app.Press(Key.Space, physical: PhysicalKey.Space, symbol: " ");
        app.Type(" ");
        app.Press(Key.Back, physical: PhysicalKey.Backspace);
        Assert.Equal("memo.txt", editor.Text);
        Assert.Equal(0, app.Left.SelectedCount);
        Assert.Equal("", app.Left.QuickSearchText);

        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await Settle(app);

        Assert.Null(Editor(app));
        Assert.True(app.Fs.Exists("/home/memo.txt"));
        Assert.False(app.Fs.Exists("/home/notes.txt"));
        Assert.Equal("memo.txt", app.Left.CursorRow?.Name);
        Assert.True(app.FileList(PanelSide.Left).IsKeyboardFocusWithin);
        Assert.Equal("/home", app.Left.CurrentPath.Path); // Enter in the editor did not open anything
    }

    [AvaloniaFact]
    public async Task Esc_and_losing_the_focus_cancel_the_inline_rename()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();

        app.Press(Key.F2, physical: PhysicalKey.F2);
        app.Type("x");
        app.Press(Key.Escape, physical: PhysicalKey.Escape);
        await Settle(app);

        Assert.Null(Editor(app));
        Assert.True(app.Fs.Exists("/home/notes.txt"));
        Assert.True(app.FileList(PanelSide.Left).IsKeyboardFocusWithin);

        app.Press(Key.F2, physical: PhysicalKey.F2);
        Assert.NotNull(Editor(app));
        app.Find<TextBox>("CommandLineBox").Focus(); // the focus goes elsewhere
        app.Pump();

        Assert.Null(Editor(app));
        Assert.Null(app.Main.Files.Rename);
    }

    [AvaloniaFact]
    public async Task An_open_inline_rename_keeps_its_text_and_focus_across_a_refresh()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();
        app.Press(Key.F2, physical: PhysicalKey.F2);
        app.Type("draft");
        Assert.Equal("draft.txt", Editor(app)!.Text);

        await app.Left.RefreshAsync(); // the folder changed on disk: the rows are replaced
        app.Pump();

        var editor = Editor(app);
        Assert.NotNull(editor);
        Assert.Same(editor, app.Focused);
        Assert.Equal("draft.txt", editor.Text);
        Assert.Equal(5, editor.CaretIndex); // still after what was typed
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await Settle(app);

        Assert.True(app.Fs.Exists("/home/draft.txt"));
        Assert.Equal("draft.txt", app.Left.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task A_refused_inline_name_keeps_the_editor_open_with_the_reason()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();
        app.Press(Key.F6, RawInputModifiers.Shift, PhysicalKey.F6);
        var editor = Editor(app)!;

        editor.SelectAll();
        app.Type("a/b");
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await Settle(app);

        Assert.Same(editor, Editor(app));
        Assert.Equal(app.Main.Files.Rename!.Error, ToolTip.GetTip(editor));
        Assert.Contains("error", editor.Classes);
        Assert.True(app.Fs.Exists("/home/notes.txt"));
    }

    [AvaloniaFact]
    public async Task Progress_window_and_indicator_follow_the_operations()
    {
        await using var app = await StartAsync(progressDelay: (_, _) => Task.CompletedTask);
        var indicator = app.Find<Button>("OperationsIndicator");
        Assert.False(app.Find<Border>("OperationsBar").IsVisible);

        var op = new GateOperation();
        _ = app.Main.Services.Operations.Enqueue(op, runImmediately: true);
        app.Main.Operations.Track(op);
        await op.Started;
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Window.Progress is { IsVisible: true };
        });
        Assert.True(app.Find<Border>("OperationsBar").IsVisible);
        Assert.Equal("1 operation", indicator.Content);

        app.Window.Progress!.Close(); // closing it does not cancel anything
        app.Pump();
        Assert.Null(app.Window.Progress);
        Assert.False(app.Main.Operations.IsWindowVisible);
        Assert.False(op.Completion.IsCompleted);

        app.Click(indicator);
        Assert.True(app.Window.Progress is { IsVisible: true });

        op.Release();
        await op.Completion;
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Window.Progress is null;
        });
        Assert.False(app.Find<Border>("OperationsBar").IsVisible);
    }
}
