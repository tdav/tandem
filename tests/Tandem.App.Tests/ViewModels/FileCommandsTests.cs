using Avalonia.Headless.XUnit;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The file commands on the whole app over the in-memory "local disk", with scripted dialogs. Both panels
/// start at /home (docs, src, README.md, notes.txt, report.pdf); the right one is moved to /dst where needed.</summary>
[Collection(LanguageCollection.Name)]
public class FileCommandsTests : LanguageTestBase
{
    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static string[] Names(PanelViewModel panel) => panel.Rows.Select(r => r.Name).ToArray();

    private static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory("/dst");
        return fs;
    }

    private static async Task<TestApp> StartAsync(InMemoryFileSystem? fs = null, bool trashCapable = false,
        string? right = "/dst", ProviderCapabilities removed = ProviderCapabilities.None)
    {
        var app = await TestApp.StartAsync(fs ?? FileSystem(), show: false, trashCapable: trashCapable,
            removedCapabilities: removed);
        if (right is not null) await app.Right.NavigateToAsync(VfsPath.Local(right));
        return app;
    }

    private static void Cursor(PanelViewModel panel, string name) => panel.CursorIndex = IndexOf(panel, name);

    private static void Mark(PanelViewModel panel, params string[] names)
    {
        foreach (var name in names) panel.Rows[IndexOf(panel, name)].IsSelected = true;
    }

    private static async Task<bool> Run(TestApp app, string id)
    {
        var ran = await app.Main.Commands.ExecuteAsync(id);
        await app.Main.Files.WhenIdleAsync();
        return ran;
    }

    // ---- Copy and move ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task F5_copies_selection_to_passive_panel_path()
    {
        await using var app = await StartAsync();
        Mark(app.Left, "notes.txt", "report.pdf");
        app.Dialogs.AnswerCopyMove();

        Assert.True(await Run(app, CommandIds.FileCopy));

        var request = Assert.Single(app.Dialogs.CopyMoveRequests);
        Assert.Equal(new CopyMoveRequest(false, 2, null, "/dst", request.Validate), request);
        Assert.Equal("notes", app.Fs.ReadAllText("/dst/notes.txt"));
        Assert.Equal("report", app.Fs.ReadAllText("/dst/report.pdf"));
        Assert.True(app.Fs.Exists("/home/notes.txt")); // a copy leaves the sources
        Assert.Contains("notes.txt", Names(app.Right)); // the passive panel shows the new files
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Dialogs.Summaries);
    }

    [AvaloniaFact]
    public async Task F5_with_nothing_under_cursor_does_nothing()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "..");

        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileCopy));
        Assert.False(await Run(app, CommandIds.FileCopy));
        Assert.False(await app.Main.HandleKeyAsync(KeyChord.Parse("F5")));
        Assert.Empty(app.Dialogs.Log);
    }

    [AvaloniaFact]
    public async Task F5_single_item_with_new_name_renames()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove("/dst/b.txt");

        await Run(app, CommandIds.FileCopy);

        var request = Assert.Single(app.Dialogs.CopyMoveRequests);
        Assert.Equal("notes.txt", request.SingleName);
        Assert.Equal("/dst/notes.txt", request.Destination); // one item: the path with its name
        Assert.Equal("notes", app.Fs.ReadAllText("/dst/b.txt"));
        Assert.False(app.Fs.Exists("/dst/notes.txt"));
    }

    [AvaloniaFact]
    public async Task F5_trailing_slash_means_directory()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove("/dst/new/");

        await Run(app, CommandIds.FileCopy);

        Assert.Equal("notes", app.Fs.ReadAllText("/dst/new/notes.txt"));
        Assert.Contains("new", Names(app.Right)); // the passive panel shows the folder just made
    }

    [AvaloniaFact]
    public async Task F5_new_name_with_many_items_shows_error_and_reasks()
    {
        await using var app = await StartAsync();
        Mark(app.Left, "notes.txt", "report.pdf");
        app.Dialogs.AnswerCopyMove("/dst/x.txt").AnswerCopyMove("/dst");

        await Run(app, CommandIds.FileCopy);

        Assert.Equal([Loc.Format("Destination_NewNameManyItems", "x.txt")], app.Dialogs.ValidationErrors);
        Assert.Single(app.Dialogs.CopyMoveRequests); // the same dialog, still open
        Assert.False(app.Fs.Exists("/dst/x.txt"));
        Assert.True(app.Fs.Exists("/dst/notes.txt"));
        Assert.True(app.Fs.Exists("/dst/report.pdf"));
    }

    [AvaloniaFact]
    public async Task F5_relative_destination_resolves_from_active_panel()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove("docs");

        await Run(app, CommandIds.FileCopy);

        Assert.Equal("notes", app.Fs.ReadAllText("/home/docs/notes.txt"));
    }

    [AvaloniaFact]
    public async Task F5_verify_content_reaches_the_operation()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove(verify: true);
        app.Fs.ChangeContentOnRead("/home/notes.txt", 2, "other"u8.ToArray()); // the read-back differs
        app.Dialogs.ErrorAnswers.Enqueue(ErrorAction.Skip);

        await Run(app, CommandIds.FileCopy);

        var error = Assert.Single(app.Dialogs.ErrorRequests);
        Assert.Equal(Loc.Format("OperationError_ContentMismatch", "/home/notes.txt"), error.Message);
        Assert.False(app.Fs.Exists("/dst/notes.txt"));
    }

    [AvaloniaFact]
    public async Task F6_moves_and_both_panels_refresh()
    {
        await using var app = await StartAsync();
        Mark(app.Left, "notes.txt", "README.md");
        app.Dialogs.AnswerCopyMove();

        await Run(app, CommandIds.FileMove);

        Assert.True(Assert.Single(app.Dialogs.CopyMoveRequests).IsMove);
        Assert.False(app.Fs.Exists("/home/notes.txt"));
        Assert.Equal("notes", app.Fs.ReadAllText("/dst/notes.txt"));
        Assert.DoesNotContain("notes.txt", Names(app.Left));
        Assert.DoesNotContain("README.md", Names(app.Left));
        Assert.Contains("notes.txt", Names(app.Right));
        Assert.Contains("README.md", Names(app.Right));
        Assert.Equal(0, app.Left.SelectedCount); // the marks went with the moved items
    }

    [AvaloniaFact]
    public async Task Copy_is_disabled_while_the_passive_panel_shows_an_error()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileCopy));

        await app.Right.NavigateToAsync(VfsPath.Local("/nowhere"));

        Assert.NotNull(app.Right.Error);
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileCopy));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileMove));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileDelete));
    }

    [AvaloniaFact]
    public async Task Cancelled_dialog_starts_no_operation()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        app.Dialogs.CopyMoveAnswers.Enqueue(null);
        app.Dialogs.CopyMoveAnswers.Enqueue(null);
        app.Dialogs.DeleteAnswers.Enqueue(false);
        app.Dialogs.NameAnswers.Enqueue(null);
        var before = app.Fs.AllPaths.ToArray();
        var queued = 0;
        app.Main.Services.Operations.Changed += (_, _) => Interlocked.Increment(ref queued);

        await Run(app, CommandIds.FileCopy);
        await Run(app, CommandIds.FileMove);
        await Run(app, CommandIds.FileDelete);
        await Run(app, CommandIds.FileMkdir);

        Assert.Equal(0, Volatile.Read(ref queued));
        Assert.Equal(before, app.Fs.AllPaths);
        Assert.Empty(app.Dialogs.Unscripted);
    }

    [AvaloniaFact]
    public async Task Rejected_operation_shows_message()
    {
        await using var app = await StartAsync(right: "/home/docs");
        Cursor(app.Left, "docs");
        app.Dialogs.AnswerCopyMove(); // "/home/docs/docs": the folder into itself

        await Run(app, CommandIds.FileCopy);

        var message = Assert.Single(app.Dialogs.Messages);
        Assert.StartsWith(Loc.Enum(RejectionReason.DestinationInsideSource), message);
        Assert.False(app.Fs.Exists("/home/docs/docs"));
        Assert.Empty(app.Dialogs.Summaries);
    }

    [AvaloniaFact]
    public async Task Skipped_items_show_summary()
    {
        var fs = FileSystem();
        fs.AddFile("/dst/notes.txt", "old");
        await using var app = await StartAsync(fs);
        Mark(app.Left, "notes.txt", "report.pdf");
        app.Dialogs.AnswerCopyMove();
        app.Dialogs.ConflictAnswers.Enqueue(new ConflictDecision(ConflictAction.Skip, false));

        await Run(app, CommandIds.FileCopy);

        var conflict = Assert.Single(app.Dialogs.ConflictRequests);
        Assert.Equal("/dst/notes.txt", conflict.Existing.Path.Path);
        var skipped = Assert.Single(Assert.Single(app.Dialogs.Summaries));
        Assert.Equal(SkipReason.ConflictSkipped, skipped.Reason);
        Assert.Equal("old", app.Fs.ReadAllText("/dst/notes.txt"));
        Assert.True(app.Fs.Exists("/dst/report.pdf"));
    }

    [AvaloniaFact]
    public async Task A_cancel_chosen_in_a_question_shows_no_summary()
    {
        var fs = FileSystem();
        fs.AddFile("/dst/notes.txt", "old");
        await using var app = await StartAsync(fs);
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();
        app.Dialogs.ConflictAnswers.Enqueue(new ConflictDecision(ConflictAction.Cancel, false));

        await Run(app, CommandIds.FileCopy);

        Assert.Empty(app.Dialogs.Summaries);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Equal("old", app.Fs.ReadAllText("/dst/notes.txt"));
    }

    private static InMemoryFileSystem DeniedNotes()
    {
        var fs = FileSystem();
        fs.FailOn((op, path) => op == "OpenRead" && path.Path == "/home/notes.txt",
            () => new VfsAccessDeniedException("denied by test"));
        return fs;
    }

    [AvaloniaFact]
    public async Task An_abort_chosen_by_the_user_shows_no_second_message()
    {
        await using var app = await StartAsync(DeniedNotes());
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();
        app.Dialogs.ErrorAnswers.Enqueue(ErrorAction.Abort);

        await Run(app, CommandIds.FileCopy);

        Assert.Equal(Loc.Format("OperationError_AccessDenied", "/home/notes.txt"),
            Assert.Single(app.Dialogs.ErrorRequests).Message);
        Assert.Empty(app.Dialogs.Messages); // the user stopped it; nothing more to say
        Assert.Empty(app.Dialogs.Summaries);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task A_failure_the_user_did_not_choose_is_worded_in_the_users_language()
    {
        await using var app = await StartAsync(DeniedNotes());
        Loc.SetLanguage("ru"); // after start-up, which applies the saved language
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();
        app.Main.OnWindowClosing(); // questions are now answered "abort" without asking the user

        await Run(app, CommandIds.FileCopy);

        Assert.Empty(app.Dialogs.ErrorRequests);
        var (text, detail) = Assert.Single(app.Dialogs.MessagesWithDetail);
        Assert.Equal(Loc.Format("Operation_Failed", Loc.Get("OperationFailed_AccessDenied")), text);
        Assert.StartsWith("Операция прервана", text);
        Assert.DoesNotContain("denied by test", text);
        Assert.Equal("denied by test", detail); // the system's words only as the detail
    }

    public static TheoryData<Exception, string> Failures => new()
    {
        { new VfsAccessDeniedException("x"), "OperationFailed_AccessDenied" },
        { new VfsNotFoundException("x"), "OperationFailed_NotFound" },
        { new VfsAlreadyExistsException("x"), "OperationFailed_AlreadyExists" },
        { new ContentMismatchException("x"), "OperationFailed_ContentMismatch" },
        { new IOException("x", 28), "OperationFailed_DiskFull" },
        { new IOException("x"), "OperationFailed_IO" },
        { new InvalidOperationException("x"), "OperationFailed_Other" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Failures_are_worded_by_exception_type(Exception error, string key)
    {
        Assert.Equal(Loc.Get(key), OperationTexts.ForFailure(error));
    }

    [AvaloniaFact]
    public async Task A_failure_after_an_operation_goes_to_the_failure_path_not_the_ui()
    {
        var fs = FileSystem();
        fs.AddFile("/dst/notes.txt", "old");
        await using var app = await StartAsync(fs);
        Cursor(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();
        app.Dialogs.ConflictAnswers.Enqueue(new ConflictDecision(ConflictAction.Skip, false));
        app.Dialogs.ThrowOnSummary = new InvalidOperationException("summary broke");

        await Run(app, CommandIds.FileCopy);

        Assert.Equal("summary broke", Assert.Single(app.Failures).Message);
    }

    // ---- New folder -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task F7_creates_nested_folders_and_focuses_first()
    {
        await using var app = await StartAsync(right: "/home");
        Mark(app.Left, "notes.txt");
        app.Dialogs.NameAnswers.Enqueue("a/b/c");

        await Run(app, CommandIds.FileMkdir);

        Assert.Equal("", Assert.Single(app.Dialogs.NameRequests));
        Assert.True(app.Fs.Exists("/home/a/b/c"));
        Assert.Equal("a", app.Left.CursorRow?.Name);
        Assert.Contains("a", Names(app.Right)); // the other panel shows the same folder
        Assert.True(app.Left.Rows[IndexOf(app.Left, "notes.txt")].IsSelected); // marks kept
    }

    [AvaloniaFact]
    public async Task F7_rejects_empty_name()
    {
        await using var app = await StartAsync();
        foreach (var name in new[] { "", "   ", "/abs", "a//b", "a/../b", "docs" })
            app.Dialogs.NameAnswers.Enqueue(name);
        app.Dialogs.NameAnswers.Enqueue(null);
        var before = app.Fs.AllPaths.ToArray();

        await Run(app, CommandIds.FileMkdir);

        Assert.Equal(
        [
            Loc.Get("NewFolder_Empty"), Loc.Get("NewFolder_Empty"), Loc.Format("NewFolder_Invalid", "/abs"),
            Loc.Format("NewFolder_Invalid", "a//b"), Loc.Format("NewFolder_Invalid", "a/../b"),
            Loc.Format("NewFolder_Exists", "docs"),
        ], app.Dialogs.ValidationErrors);
        Assert.Equal(before, app.Fs.AllPaths);
    }

    [AvaloniaFact]
    public async Task F7_reports_a_folder_it_cannot_check_instead_of_crashing()
    {
        var fs = FileSystem();
        var failure = new VfsAccessDeniedException("no peeking");
        fs.FailOn((op, path) => op == "GetEntry" && path.Path == "/home/secret", () => failure);
        await using var app = await StartAsync(fs);
        app.Dialogs.NameAnswers.Enqueue("secret");
        app.Dialogs.NameAnswers.Enqueue(null);

        await Run(app, CommandIds.FileMkdir);

        Assert.Equal([OperationTexts.ForError(VfsPath.Local("/home/secret"), failure)], app.Dialogs.ValidationErrors);
        Assert.Empty(app.Failures);
        Assert.False(app.Fs.Exists("/home/secret"));
    }

    [Theory]
    [InlineData("a", new[] { "a" })]
    [InlineData("a/", new[] { "a" })]
    [InlineData("a/b c/d", new[] { "a", "b c", "d" })]
    [InlineData(" lead", new[] { " lead" })]
    public void Folder_names_split_into_segments(string text, string[] expected)
    {
        Assert.Equal(expected, FileCommands.FolderSegments(text, out var error));
        Assert.Null(error);
    }

    // ---- Delete -----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task F8_uses_trash_when_available()
    {
        await using var app = await StartAsync(trashCapable: true);
        Cursor(app.Left, "notes.txt");
        app.Dialogs.DeleteAnswers.Enqueue(true);

        await Run(app, CommandIds.FileDelete);

        Assert.Equal((1, "notes.txt", false), Assert.Single(app.Dialogs.DeleteRequests));
        Assert.Equal(["/home/notes.txt"], app.Trash.Items);
        Assert.DoesNotContain("notes.txt", Names(app.Left));
    }

    [AvaloniaFact]
    public async Task F8_without_trash_support_warns_permanent()
    {
        await using var app = await StartAsync();
        Mark(app.Left, "notes.txt", "docs");
        app.Dialogs.DeleteAnswers.Enqueue(true);

        await Run(app, CommandIds.FileDelete);

        Assert.Equal((2, null, true), Assert.Single(app.Dialogs.DeleteRequests));
        Assert.Empty(app.Trash.Items);
        Assert.False(app.Fs.Exists("/home/notes.txt"));
        Assert.False(app.Fs.Exists("/home/docs"));
    }

    [AvaloniaFact]
    public async Task Shift_F8_is_permanent()
    {
        await using var app = await StartAsync(trashCapable: true);
        Cursor(app.Left, "notes.txt");
        app.Dialogs.DeleteAnswers.Enqueue(true);

        Assert.Equal(CommandIds.FileDeletePermanent, app.Main.KeyMap.Find(KeyChord.Parse("Shift+F8")));
        await Run(app, CommandIds.FileDeletePermanent);

        Assert.Equal((1, "notes.txt", true), Assert.Single(app.Dialogs.DeleteRequests));
        Assert.Empty(app.Trash.Items);
        Assert.False(app.Fs.Exists("/home/notes.txt"));
    }

    [AvaloniaFact]
    public async Task F8_on_the_parent_row_does_nothing()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "..");

        Assert.False(await Run(app, CommandIds.FileDelete));
        Assert.False(await Run(app, CommandIds.FileDeletePermanent));
        Assert.Empty(app.Dialogs.Log);
    }

    // ---- Rename -----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Rename_opens_the_editor_with_the_name_before_the_extension_selected()
    {
        var fs = FileSystem();
        fs.AddFile("/home/.gitignore");
        fs.AddFile("/home/archive.tar.gz");
        await using var app = await StartAsync(fs);
        app.Main.Workspace.ShowHidden = true;
        await app.Left.RefreshAsync();

        foreach (var (name, selected) in new[] { ("notes.txt", 5), ("docs", 4), (".gitignore", 10), ("archive.tar.gz", 11) })
        {
            Cursor(app.Left, name);
            await Run(app, CommandIds.FileRename);
            var editor = app.Main.Files.Rename;
            Assert.NotNull(editor);
            Assert.Same(editor, app.Left.CursorRow!.Rename);
            Assert.Equal(name, editor.Text);
            Assert.Equal(selected, editor.SelectionLength);
            editor.Cancel();
            Assert.Null(app.Left.CursorRow.Rename);
            Assert.Null(app.Main.Files.Rename);
        }
    }

    [AvaloniaFact]
    public async Task Rename_commits_through_a_move_and_puts_the_cursor_on_the_new_name()
    {
        await using var app = await StartAsync(right: "/home");
        Cursor(app.Left, "notes.txt");
        await Run(app, CommandIds.FileRename);
        var editor = app.Main.Files.Rename!;

        editor.Text = "renamed.txt";
        Assert.True(await editor.CommitAsync());
        await app.Main.Files.WhenIdleAsync();

        Assert.False(app.Fs.Exists("/home/notes.txt"));
        Assert.Equal("notes", app.Fs.ReadAllText("/home/renamed.txt"));
        Assert.Equal("renamed.txt", app.Left.CursorRow?.Name);
        Assert.Contains("renamed.txt", Names(app.Right));
    }

    [AvaloniaTheory]
    [InlineData(ConflictAction.Skip)]
    [InlineData(ConflictAction.Rename)]
    [InlineData(ConflictAction.Overwrite)]
    public async Task Rename_onto_an_existing_name_never_puts_the_cursor_on_the_other_file(ConflictAction answer)
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        await Run(app, CommandIds.FileRename);
        app.Dialogs.ConflictAnswers.Enqueue(new ConflictDecision(answer, false));
        var editor = app.Main.Files.Rename!;

        editor.Text = "report.pdf";
        await editor.CommitAsync();
        await app.Main.Files.WhenIdleAsync();

        Assert.Single(app.Dialogs.ConflictRequests);
        if (answer == ConflictAction.Skip)
        {
            Assert.Equal("report", app.Fs.ReadAllText("/home/report.pdf"));
            Assert.Equal("notes.txt", app.Left.CursorRow?.Name);
        }
        Assert.True(answer == ConflictAction.Overwrite || app.Left.CursorRow?.Name != "report.pdf");
    }

    [AvaloniaFact]
    public async Task An_open_rename_survives_a_refresh_and_closes_when_its_item_goes()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        await Run(app, CommandIds.FileRename);
        var editor = app.Main.Files.Rename!;
        editor.Text = "draft";
        editor.SelectionStart = 2;
        editor.SelectionEnd = 3;
        var oldRow = editor.Row;

        await app.Left.RefreshAsync(); // e.g. a change on disk: the rows are new

        Assert.NotSame(oldRow, editor.Row);
        Assert.Contains(editor.Row, app.Left.Rows);
        Assert.Same(editor, editor.Row.Rename);
        Assert.Null(oldRow.Rename);
        Assert.True(editor.IsOpen);
        Assert.Same(editor, app.Main.Files.Rename);
        Assert.Equal(("draft", 2, 3), (editor.Text, editor.SelectionStart, editor.SelectionEnd));

        await app.Fs.DeleteAsync(VfsPath.Local("/home/notes.txt"), false, TestContext.Current.CancellationToken);
        await app.Left.RefreshAsync();

        Assert.False(editor.IsOpen);
        Assert.Null(app.Main.Files.Rename);
        Assert.DoesNotContain(app.Left.Rows, r => r.Rename is not null);
    }

    [AvaloniaFact]
    public async Task A_rename_that_throws_is_reported_not_lost()
    {
        await using var app = await StartAsync();
        var row = app.Left.Rows[IndexOf(app.Left, "notes.txt")];
        var failures = new List<Exception>();
        var editor = new InlineRenameViewModel(row, _ => throw new InvalidOperationException("boom"), _ => { },
            failures.Add) { Text = "x.txt" };

        Assert.True(await editor.CommitAsync());

        Assert.Equal("boom", Assert.Single(failures).Message);
        Assert.False(editor.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_read_only_source_disables_delete_move_and_rename()
    {
        await using var app = await StartAsync(removed: ProviderCapabilities.Write);
        Cursor(app.Left, "notes.txt");

        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileDelete));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileDeletePermanent));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileMove));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileRename));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileMkdir));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.SelectByMask));
    }

    [AvaloniaFact]
    public async Task Background_tabs_showing_a_changed_folder_are_refreshed_too()
    {
        await using var app = await StartAsync();
        var background = app.Left;
        Mark(background, "notes.txt");
        await app.Main.Workspace.Left.NewTabAsync(); // the new tab, also /home, is in front now
        var front = app.Left;
        Assert.NotSame(background, front);
        Cursor(front, "notes.txt");
        app.Dialogs.AnswerCopyMove();

        await Run(app, CommandIds.FileMove);

        Assert.DoesNotContain("notes.txt", Names(background));
        Assert.Equal(0, background.SelectedCount); // no stale mark on a vanished item
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task Rename_refuses_invalid_names_and_stays_open(string name)
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        await Run(app, CommandIds.FileRename);
        var editor = app.Main.Files.Rename!;

        editor.Text = name;
        Assert.False(await editor.CommitAsync());

        Assert.NotNull(editor.Error);
        Assert.True(editor.IsOpen);
        Assert.Same(editor, app.Main.Files.Rename);
        editor.Text = "fine.txt";
        Assert.Null(editor.Error); // editing clears it
        Assert.True(app.Fs.Exists("/home/notes.txt"));
    }

    [AvaloniaFact]
    public async Task Committing_an_unchanged_name_does_nothing()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        await Run(app, CommandIds.FileRename);
        var queued = 0;
        app.Main.Services.Operations.Changed += (_, _) => Interlocked.Increment(ref queued);

        Assert.True(await app.Main.Files.Rename!.CommitAsync());
        await app.Main.Files.WhenIdleAsync();

        Assert.Equal(0, Volatile.Read(ref queued));
        Assert.Null(app.Main.Files.Rename);
    }

    [AvaloniaFact]
    public async Task Rename_is_unavailable_on_the_parent_row()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "..");

        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileRename));
        Assert.False(await Run(app, CommandIds.FileRename));
        Assert.Null(app.Main.Files.Rename);
    }

    // ---- Masks and availability -------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Select_by_mask_remembers_the_last_mask_for_the_session()
    {
        await using var app = await StartAsync();
        app.Dialogs.MaskAnswers.Enqueue("*.txt");
        app.Dialogs.MaskAnswers.Enqueue("note*");

        await Run(app, CommandIds.SelectByMask);
        Assert.Equal(["notes.txt"], app.Left.Rows.Where(r => r.IsSelected).Select(r => r.Name));
        await Run(app, CommandIds.UnselectByMask);

        Assert.Equal([(true, "*.*"), (false, "*.txt")], app.Dialogs.MaskRequests);
        Assert.Equal(0, app.Left.SelectedCount);
        Assert.Equal("note*", app.Main.Files.LastMask);
    }

    [AvaloniaFact]
    public async Task Availability_is_announced_only_when_it_changes()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        var raised = 0;
        app.Main.Commands.CanExecuteChanged += (_, _) => raised++;

        Cursor(app.Left, "report.pdf"); // still a file: nothing changes
        Assert.Equal(0, raised);
        // One bulk change each way: only Unpack follows it (no archive among the marked rows; the file under the
        // cursor again).
        app.Left.SelectAll();
        app.Left.ClearSelection();
        Assert.Equal(2, raised);
        raised = 0;

        Cursor(app.Left, ".."); // nothing to operate on any more
        Assert.InRange(raised, 1, 3); // once each for the file, shell and archive commands
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileDelete));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileMkdir));

        app.Main.Workspace.SwitchPanel(); // the right panel's cursor is on its ".." too: copy stays off
        Assert.True(raised <= 6);
    }
}
