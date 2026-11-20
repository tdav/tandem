using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.ViewModels;
using Tandem.App.Views.Dialogs;
using Tandem.App.Views.Editing;
using Tandem.App.Views.Viewing;
using Tandem.Archives;
using Tandem.Archives.Tests;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.Views;

/// <summary>The app with archive fixtures on its in-memory "local disk" and real archive mounting: the home folder
/// holds the fixtures next to the default files, the right panel shows the empty folder /dst.</summary>
internal static class ArchiveApp
{
    public static readonly string[] Fixtures =
        ["sample.zip", "sample.7z", "encrypted.zip", "broken.zip", "slip.zip", "sample.tar.gz"];

    /// <summary>The common tree of the fixtures' root (Fixtures/README.md).</summary>
    public static readonly string[] RootNames = ["a.txt", "docs", "empty", "привет.txt"];

    public static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        foreach (var name in Fixtures) fs.AddFile(TestApp.Home + "/" + name, Fixture.Bytes(name));
        fs.AddDirectory("/dst");
        return fs;
    }

    public static async Task<TestApp> StartAsync(InMemoryFileSystem? fs = null, bool show = true,
        AppState? state = null)
    {
        var app = await TestApp.StartAsync(fs ?? FileSystem(), state: state, show: show);
        if (state is null) await app.Right.NavigateToAsync(VfsPath.Local("/dst"));
        app.Pump();
        return app;
    }

    public static int IndexOf(PanelViewModel panel, string name) =>
        panel.Rows.ToList().FindIndex(r => r.Name == name);

    public static void Cursor(TestApp app, PanelViewModel panel, string name)
    {
        var index = IndexOf(panel, name);
        Assert.True(index >= 0, $"no row {name}");
        panel.CursorIndex = index;
        app.Pump();
    }

    public static bool IsArchiveRoot(VfsPath path) =>
        path.ProviderId.StartsWith("archive:", StringComparison.Ordinal) && path.IsRoot;

    /// <summary>Enter on the archive in the left panel, waiting until the panel shows its root.</summary>
    public static async Task EnterAsync(TestApp app, string archive)
    {
        Cursor(app, app.Left, archive);
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return IsArchiveRoot(app.Left.CurrentPath) && app.Left.Rows.Count > 1;
        });
    }

    public static async Task SettleAsync(TestApp app)
    {
        await app.Main.Archives.WhenIdleAsync();
        await app.Main.Files.WhenIdleAsync();
        app.Pump();
    }

    public static string[] Names(PanelViewModel panel) =>
        panel.Rows.Where(r => !r.IsParentLink).Select(r => r.Name).Order(StringComparer.Ordinal).ToArray();

    public static byte[] Bytes(InMemoryFileSystem fs, string path)
    {
        using var stream = fs.OpenReadAsync(VfsPath.Local(path), CancellationToken.None).GetAwaiter().GetResult();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The entries of a zip file on the in-memory disk, read by another library: name → content.</summary>
    public static Dictionary<string, string> ZipEntries(InMemoryFileSystem fs, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(Bytes(fs, path)), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }, StringComparer.Ordinal);
    }
}

/// <summary>Archives as folders through the real window: entering and leaving them, the file commands inside them,
/// passwords and damaged archives.</summary>
[Collection(LanguageCollection.Name)]
public class ArchiveWindowTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Enter_on_zip_opens_it_and_Backspace_returns_to_the_file()
    {
        await using var app = await ArchiveApp.StartAsync();

        await ArchiveApp.EnterAsync(app, "sample.zip");

        Assert.Equal(ArchiveApp.RootNames, ArchiveApp.Names(app.Left));
        Assert.Equal("/home/sample.zip", app.Left.DisplayPath);
        Assert.True(app.Left.Rows[0].IsParentLink);

        app.Press(Key.Back, physical: PhysicalKey.Backspace);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Left.CurrentPath == VfsPath.Local(TestApp.Home);
        });

        Assert.Equal("sample.zip", app.Left.CursorRow?.Name);
        Assert.Empty(app.Failures);
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task F5_copies_a_file_out_of_7z()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "sample.7z");
        ArchiveApp.Cursor(app, app.Left, "привет.txt");
        app.Dialogs.AnswerCopyMove();

        app.Press(Key.F5, physical: PhysicalKey.F5);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("/dst/привет.txt", Assert.Single(app.Dialogs.CopyMoveRequests).Destination);
        Assert.Equal("привет\n", app.Fs.ReadAllText("/dst/привет.txt"));
        Assert.Contains("привет.txt", ArchiveApp.Names(app.Right));
        Assert.Empty(app.Failures);
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task F5_into_zip_adds_files_and_they_are_listed()
    {
        await using var app = await ArchiveApp.StartAsync();
        var root = await app.Main.ArchiveMounts.MountAsync(VfsPath.Local("/home/sample.zip"), CancellationToken.None);
        await app.Right.NavigateToAsync(root);
        ArchiveApp.Cursor(app, app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();

        app.Press(Key.F5, physical: PhysicalKey.F5);
        await ArchiveApp.SettleAsync(app);

        Assert.Contains("notes.txt", ArchiveApp.Names(app.Right));
        var entries = ArchiveApp.ZipEntries(app.Fs, "/home/sample.zip");
        Assert.Equal("notes", entries["notes.txt"]);
        Assert.Equal("a", entries["a.txt"]);
        Assert.DoesNotContain(app.Fs.AllPaths, p => p.Contains(".tandem-", StringComparison.Ordinal));
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task F8_in_zip_warns_permanent_and_deletes_the_entry()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "sample.zip");
        ArchiveApp.Cursor(app, app.Left, "a.txt");
        app.Dialogs.DeleteAnswers.Enqueue(true);

        app.Press(Key.F8, physical: PhysicalKey.F8);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal((1, "a.txt", true), Assert.Single(app.Dialogs.DeleteRequests));
        Assert.DoesNotContain("a.txt", ArchiveApp.Names(app.Left));
        Assert.DoesNotContain("a.txt", ArchiveApp.ZipEntries(app.Fs, "/home/sample.zip").Keys);
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task F7_and_rename_work_inside_zip()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "sample.zip");
        app.Dialogs.NameAnswers.Enqueue("made");

        app.Press(Key.F7, physical: PhysicalKey.F7);
        await ArchiveApp.SettleAsync(app);

        Assert.Contains("made", ArchiveApp.Names(app.Left));
        Assert.Equal("made", app.Left.CursorRow?.Name);

        ArchiveApp.Cursor(app, app.Left, "a.txt");
        app.Press(Key.F6, RawInputModifiers.Shift, PhysicalKey.F6);
        var rename = app.Main.Files.Rename;
        Assert.NotNull(rename);
        rename.Text = "b.txt";
        Assert.True(await rename.CommitAsync());
        await ArchiveApp.SettleAsync(app);

        Assert.Contains("b.txt", ArchiveApp.Names(app.Left));
        Assert.DoesNotContain("a.txt", ArchiveApp.Names(app.Left));
        var entries = ArchiveApp.ZipEntries(app.Fs, "/home/sample.zip");
        Assert.Equal("a", entries["b.txt"]);
        Assert.Contains("made/", entries.Keys);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Write_commands_are_unavailable_inside_7z()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "sample.7z");
        ArchiveApp.Cursor(app, app.Left, "a.txt");

        foreach (var id in new[]
                 {
                     CommandIds.FileMove, CommandIds.FileMkdir, CommandIds.FileDelete, CommandIds.FileDeletePermanent,
                     CommandIds.FileRename,
                 })
            Assert.False(app.Main.Commands.CanExecute(id), id);
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileCopy)); // out of it is fine
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileView));

        // Into it: the 7z is the passive side now.
        app.Main.Workspace.SwitchPanel();
        ArchiveApp.Cursor(app, app.Main.Workspace.ActivePanel, "..");
        await app.Right.NavigateToAsync(VfsPath.Local(TestApp.Home));
        ArchiveApp.Cursor(app, app.Right, "notes.txt");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileCopy));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ArchivePack));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ArchiveUnpack));
    }

    [AvaloniaFact]
    public async Task F3_shows_a_file_from_inside_an_archive()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "sample.tar.gz");
        ArchiveApp.Cursor(app, app.Left, "привет.txt");

        app.Press(Key.F3, physical: PhysicalKey.F3);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.OfType<ViewerWindow>().Any();
        });
        var viewer = app.Windows.Open.OfType<ViewerWindow>().Single();
        await viewer.Viewer.IdleAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["привет"], viewer.Viewer.Lines);
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task F4_inside_zip_saves_the_change_into_the_archive()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "sample.zip");
        ArchiveApp.Cursor(app, app.Left, "a.txt");

        app.Press(Key.F4, physical: PhysicalKey.F4);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.OfType<EditorWindow>().Any();
        });
        var editor = app.Windows.Open.OfType<EditorWindow>().Single();
        Dispatcher.UIThread.RunJobs();
        editor.KeyTextInput("X");
        Dispatcher.UIThread.RunJobs();
        editor.KeyPress(Key.S, RawInputModifiers.Meta, PhysicalKey.S, "s");
        editor.KeyRelease(Key.S, RawInputModifiers.Meta, PhysicalKey.S, "s");
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return !editor.Editor.IsModified;
        });

        Assert.Equal("Xa", ArchiveApp.ZipEntries(app.Fs, "/home/sample.zip")["a.txt"]);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Encrypted_archive_asks_for_password_and_declining_leaves_the_panel_in_place()
    {
        var fs = ArchiveApp.FileSystem();
        fs.AddFile(TestApp.Home + "/encrypted.7z", Fixture.Bytes("encrypted.7z")); // skipped while it is absent
        await using var app = await ArchiveApp.StartAsync(fs);
        ArchiveApp.Cursor(app, app.Left, "encrypted.7z");
        app.Dialogs.PasswordAnswers.Enqueue(null);

        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Dialogs.PasswordRequests.Count == 1;
        });
        await Task.Delay(50);
        app.Pump();

        Assert.Equal([("encrypted.7z", false)], app.Dialogs.PasswordRequests);
        Assert.Equal([true], app.Dialogs.PasswordsOnUiThread);
        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Equal("encrypted.7z", app.Left.CursorRow?.Name);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Encrypted_zip_asks_at_the_first_read_and_declining_fails_that_item_only()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "encrypted.zip"); // its listing is in the clear: no question yet
        Assert.Empty(app.Dialogs.PasswordRequests);
        ArchiveApp.Cursor(app, app.Left, "a.txt");
        app.Dialogs.AnswerCopyMove();
        app.Dialogs.PasswordAnswers.Enqueue(null);
        app.Dialogs.ErrorAnswers.Enqueue(Core.Operations.ErrorAction.Skip);

        app.Press(Key.F5, physical: PhysicalKey.F5);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal([("encrypted.zip", false)], app.Dialogs.PasswordRequests);
        Assert.Equal([true], app.Dialogs.PasswordsOnUiThread);
        var (item, message, _) = Assert.Single(app.Dialogs.ErrorRequests);
        Assert.Equal("a.txt", item.Name);
        Assert.Equal(OperationTexts.ForError(item,
            new ArchivePasswordRequiredException(VfsPath.Local("/home/encrypted.zip"), false)), message);
        Assert.False(app.Fs.Exists("/dst/a.txt"));
        Assert.True(ArchiveApp.IsArchiveRoot(app.Left.CurrentPath)); // the panel stays where it was
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Wrong_password_dialog_says_so_on_the_second_ask()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "encrypted.zip");
        ArchiveApp.Cursor(app, app.Left, "a.txt");
        app.Dialogs.PasswordAnswers.Enqueue("not it");
        app.Dialogs.PasswordAnswers.Enqueue("secret");

        app.Press(Key.F3, physical: PhysicalKey.F3);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Windows.Open.OfType<ViewerWindow>().Any();
        });
        var viewer = app.Windows.Open.OfType<ViewerWindow>().Single();
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return viewer.Viewer.Lines.Count > 0;
        });

        Assert.Equal([("encrypted.zip", false), ("encrypted.zip", true)], app.Dialogs.PasswordRequests);
        Assert.Equal(["a"], viewer.Viewer.Lines);
        var dialog = new Tandem.App.ViewModels.Dialogs.PasswordDialogViewModel("encrypted.zip", previousWasWrong: true);
        Assert.Equal("That password is wrong. Try again.", dialog.WrongPassword);
        Assert.Null(new Tandem.App.ViewModels.Dialogs.PasswordDialogViewModel("encrypted.zip", false).WrongPassword);
    }

    [AvaloniaFact]
    public async Task Damaged_archive_shows_a_message()
    {
        await using var app = await ArchiveApp.StartAsync();
        ArchiveApp.Cursor(app, app.Left, "broken.zip");

        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Dialogs.Messages.Count > 0;
        });

        Assert.Equal(OperationTexts.ForFailure(new ArchiveDamagedException("damaged")),
            Assert.Single(app.Dialogs.Messages));
        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Archive_tab_is_restored_as_the_archives_folder()
    {
        AppState saved;
        await using (var app = await ArchiveApp.StartAsync())
        {
            await ArchiveApp.EnterAsync(app, "sample.zip");
            await app.Left.NavigateToAsync(app.Left.CurrentPath.Combine("docs"));
            app.Main.Workspace.Save();
            saved = new JsonStateStore(app.StateDirectory).Load(TestApp.Home);
        }

        var tab = Assert.Single(saved.Left.Tabs);
        Assert.Equal((VfsPath.LocalProviderId, TestApp.Home), (tab.ProviderId, tab.Path));
        await using var restored = await ArchiveApp.StartAsync(state: saved with { Language = "en" });
        Assert.Equal(VfsPath.Local(TestApp.Home), restored.Left.CurrentPath);
        Assert.Contains("sample.zip", ArchiveApp.Names(restored.Left));
    }

    // ---- The dialogs, as windows ---------------------------------------------------------------------

    private static (Window Owner, DialogService Service) StartDialogs()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        return (owner, new DialogService { Owner = owner });
    }

    private static T Dialog<T>(Window owner) where T : Window
    {
        Dispatcher.UIThread.RunJobs();
        return owner.OwnedWindows.OfType<T>().Single();
    }

    private static void PressEnter(Window window)
    {
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        if (window.IsVisible) window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window dialog, string name) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task Password_dialog_hides_the_typed_password_and_says_when_the_last_was_wrong()
    {
        var (owner, service) = StartDialogs();
        var asking = service.AskPasswordAsync("encrypted.zip", previousWasWrong: true, CancellationToken.None);
        var dialog = Dialog<PasswordDialog>(owner);
        var field = Named<TextBox>(dialog, "Password");

        Assert.NotEqual('\0', field.PasswordChar);
        Assert.False(field.RevealPassword);
        Assert.True(Named<TextBlock>(dialog, "Wrong").IsVisible);
        Assert.Equal("That password is wrong. Try again.", Named<TextBlock>(dialog, "Wrong").Text);
        field.Text = "secret";
        PressEnter(dialog);
        await TestApp.WaitUntil(() => asking.IsCompleted);
        Assert.Equal("secret", await asking);

        asking = service.AskPasswordAsync("encrypted.zip", previousWasWrong: false, CancellationToken.None);
        dialog = Dialog<PasswordDialog>(owner);
        Assert.False(Named<TextBlock>(dialog, "Wrong").IsVisible);
        dialog.Close();
        await TestApp.WaitUntil(() => asking.IsCompleted);
        Assert.Null(await asking);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Pack_dialog_lists_the_formats_and_a_new_format_changes_the_extension()
    {
        var (owner, service) = StartDialogs();
        var asking = service.AskPackAsync(new PackRequest(1, "docs", "/dst/docs.zip", ArchiveFormats.Creatable));
        var dialog = Dialog<PackDialog>(owner);
        var formats = Named<ComboBox>(dialog, "Format");

        Assert.Equal([".zip", ".tar", ".tar.gz"], formats.Items.Cast<string>());
        formats.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("/dst/docs.tar.gz", Named<TextBox>(dialog, "Destination").Text);
        PressEnter(dialog);
        await TestApp.WaitUntil(() => asking.IsCompleted);
        Assert.Equal(new PackAnswer("/dst/docs.tar.gz", ArchiveFormat.TarGz, false), await asking);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Unpack_dialog_keeps_separate_folders_as_proposed()
    {
        var (owner, service) = StartDialogs();
        var asking = service.AskUnpackAsync(new UnpackRequest(2, null, "/dst", SeparateFolders: true));
        var dialog = Dialog<UnpackDialog>(owner);

        Assert.True(Named<CheckBox>(dialog, "SeparateFolders").IsChecked);
        PressEnter(dialog);
        await TestApp.WaitUntil(() => asking.IsCompleted);
        Assert.Equal(new UnpackAnswer("/dst", true, false), await asking);
        owner.Close();
    }
}
