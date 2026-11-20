using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Dialogs;
using Tandem.Archives;
using Tandem.Archives.Tests;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

/// <summary>Packing (Alt+F5) and unpacking (Alt+F9) through the real window, with real archives on the in-memory
/// "local disk".</summary>
[Collection(LanguageCollection.Name)]
public class ArchiveCommandsTests : LanguageTestBase
{
    private static void Mark(TestApp app, params string[] names)
    {
        foreach (var name in names)
        {
            ArchiveApp.Cursor(app, app.Left, name);
            app.Main.Workspace.ActivePanel.ToggleSelectAndMoveDown();
        }
        app.Pump();
    }

    private static void AltF5(TestApp app) => app.Press(Key.F5, RawInputModifiers.Alt, PhysicalKey.F5);

    private static void AltF9(TestApp app) => app.Press(Key.F9, RawInputModifiers.Alt, PhysicalKey.F9);

    [AvaloniaFact]
    public async Task Alt_F5_packs_marked_items_into_the_passive_folder()
    {
        await using var app = await ArchiveApp.StartAsync();
        Mark(app, "notes.txt", "docs");
        app.Dialogs.AnswerPack();

        AltF5(app);
        await ArchiveApp.SettleAsync(app);

        var request = Assert.Single(app.Dialogs.PackRequests);
        Assert.Equal((2, null), (request.ItemCount, request.SingleName));
        Assert.Equal("/dst/home.zip", request.Destination); // named after the active panel's folder
        Assert.Contains("home.zip", ArchiveApp.Names(app.Right));
        var root = await app.Main.ArchiveMounts.MountAsync(VfsPath.Local("/dst/home.zip"), CancellationToken.None);
        await app.Right.NavigateToAsync(root);
        Assert.Equal(["docs", "notes.txt"], ArchiveApp.Names(app.Right));
        Assert.Equal("notes", ArchiveApp.ZipEntries(app.Fs, "/dst/home.zip")["notes.txt"]);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Pack_dialog_offers_only_creatable_formats_and_rejects_an_existing_name()
    {
        var fs = ArchiveApp.FileSystem();
        fs.AddFile("/dst/notes.zip", "taken");
        await using var app = await ArchiveApp.StartAsync(fs);
        ArchiveApp.Cursor(app, app.Left, "notes.txt");
        app.Dialogs.AnswerPack();                                   // /dst/notes.zip exists: refused
        app.Dialogs.AnswerPack(format: ArchiveFormat.TarGz);       // the format changes the extension

        AltF5(app);
        await ArchiveApp.SettleAsync(app);

        var request = Assert.Single(app.Dialogs.PackRequests);
        Assert.Equal(ArchiveFormats.Creatable, request.Formats);
        Assert.DoesNotContain(ArchiveFormat.SevenZip, request.Formats);
        Assert.Equal((1, "notes.txt", "/dst/notes.zip"), (request.ItemCount, request.SingleName, request.Destination));
        Assert.Equal([Loc("OperationError_AlreadyExists", "/dst/notes.zip")], app.Dialogs.ValidationErrors);
        Assert.Equal(["/dst/notes.zip", "/dst/notes.tar.gz"], app.Dialogs.PackTextsAfterFormat);
        Assert.Equal("taken", app.Fs.ReadAllText("/dst/notes.zip"));
        Assert.True(app.Fs.Exists("/dst/notes.tar.gz"));
        var root = await app.Main.ArchiveMounts.MountAsync(VfsPath.Local("/dst/notes.tar.gz"), CancellationToken.None);
        await app.Right.NavigateToAsync(root);
        Assert.Equal(["notes.txt"], ArchiveApp.Names(app.Right));
    }

    [AvaloniaFact]
    public async Task Pack_proposes_the_single_item_name_without_its_extension()
    {
        await using var app = await ArchiveApp.StartAsync();

        foreach (var (name, proposed) in new[]
                 {
                     ("notes.txt", "/dst/notes.zip"), ("sample.tar.gz", "/dst/sample.zip"), ("docs", "/dst/docs.zip"),
                 })
        {
            ArchiveApp.Cursor(app, app.Left, name);
            AltF5(app); // unscripted: cancelled
            await ArchiveApp.SettleAsync(app);
            Assert.Equal(proposed, app.Dialogs.PackRequests[^1].Destination);
        }
    }

    [AvaloniaFact]
    public void Pack_dialog_swaps_the_extension_when_the_format_changes()
    {
        var vm = new PackDialogViewModel(new PackRequest(1, "docs", "/dst/docs.zip", ArchiveFormats.Creatable));

        vm.Format = ArchiveFormat.TarGz;
        Assert.Equal("/dst/docs.tar.gz", vm.DestinationText);
        vm.Format = ArchiveFormat.Tar;
        Assert.Equal("/dst/docs.tar", vm.DestinationText);
        vm.DestinationText = "/dst/mine";
        vm.Format = ArchiveFormat.Zip;
        Assert.Equal("/dst/mine.zip", vm.DestinationText);
        Assert.Equal(".tar.gz", PackDialogViewModel.FormatLabel(ArchiveFormat.TarGz));
    }

    [AvaloniaFact]
    public async Task Pack_needs_a_file_name_and_a_folder_that_can_be_written()
    {
        await using var app = await ArchiveApp.StartAsync();
        ArchiveApp.Cursor(app, app.Left, "notes.txt");
        app.Dialogs.AnswerPack("/dst");
        app.Dialogs.AnswerPack("../fine.zip"); // relative: from the active panel's folder

        AltF5(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal([Loc("Archive_NeedsFileName", "/dst")], app.Dialogs.ValidationErrors);
        Assert.True(app.Fs.Exists("/fine.zip"));

        // Into a 7z nothing can be written: packing is off.
        var seven = await app.Main.ArchiveMounts.MountAsync(VfsPath.Local("/home/sample.7z"), CancellationToken.None);
        await app.Right.NavigateToAsync(seven);
        ArchiveApp.Cursor(app, app.Left, "notes.txt");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ArchivePack));
    }

    [AvaloniaFact]
    public async Task Alt_F9_unpacks_the_archive_under_cursor()
    {
        await using var app = await ArchiveApp.StartAsync();
        ArchiveApp.Cursor(app, app.Left, "sample.zip");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        var request = Assert.Single(app.Dialogs.UnpackRequests);
        Assert.Equal((1, "sample.zip", "/dst", false),
            (request.ArchiveCount, request.SingleName, request.Destination, request.SeparateFolders));
        Assert.Equal(ArchiveApp.RootNames, ArchiveApp.Names(app.Right));
        Assert.Equal("a", app.Fs.ReadAllText("/dst/a.txt"));
        Assert.Equal("hello\n", app.Fs.ReadAllText("/dst/docs/readme.txt"));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Alt_F9_writes_nothing_outside_the_destination_for_a_slip_archive()
    {
        await using var app = await ArchiveApp.StartAsync();
        var before = app.Fs.AllPaths.ToHashSet(StringComparer.Ordinal);
        ArchiveApp.Cursor(app, app.Left, "slip.zip");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        var added = app.Fs.AllPaths.Where(p => !before.Contains(p)).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, p => Assert.StartsWith("/dst/", p, StringComparison.Ordinal));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Two_archives_unpack_into_separate_folders()
    {
        await using var app = await ArchiveApp.StartAsync();
        Mark(app, "sample.zip", "sample.tar.gz", "notes.txt");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        var request = Assert.Single(app.Dialogs.UnpackRequests);
        Assert.Equal((2, null, true), (request.ArchiveCount, request.SingleName, request.SeparateFolders));
        // Both are named "sample": each gets a folder of its own, and nothing collides.
        Assert.Equal(["sample", "sample 2"], ArchiveApp.Names(app.Right));
        Assert.Equal("a", app.Fs.ReadAllText("/dst/sample/a.txt"));
        Assert.Equal("a", app.Fs.ReadAllText("/dst/sample 2/a.txt"));
        Assert.Empty(app.Dialogs.ConflictRequests);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Unpack_into_a_new_named_folder_and_unavailable_without_an_archive()
    {
        await using var app = await ArchiveApp.StartAsync();
        Mark(app, "notes.txt", "README.md"); // marked items count by name only: no archive among them
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ArchiveUnpack));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.ArchivePack));
        Mark(app, "sample.zip");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.ArchiveUnpack));
        app.Left.ClearSelection();

        ArchiveApp.Cursor(app, app.Left, "sample.7z");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.ArchiveUnpack));
        app.Dialogs.AnswerUnpack("/dst/out");

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("hello\n", app.Fs.ReadAllText("/dst/out/docs/readme.txt"));
        Assert.Equal(["out"], ArchiveApp.Names(app.Right));
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task Alt_F9_unpacks_a_zip_named_docx_under_the_cursor()
    {
        var fs = ArchiveApp.FileSystem();
        fs.AddFile(TestApp.Home + "/report.docx", Fixture.Bytes("sample.zip"));
        await using var app = await ArchiveApp.StartAsync(fs);
        ArchiveApp.Cursor(app, app.Left, "report.docx");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.ArchiveUnpack)); // the content decides when it runs
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        var request = Assert.Single(app.Dialogs.UnpackRequests);
        Assert.Equal((1, "report.docx"), (request.ArchiveCount, request.SingleName));
        Assert.Equal(ArchiveApp.RootNames, ArchiveApp.Names(app.Right));
        Assert.Equal("hello\n", app.Fs.ReadAllText("/dst/docs/readme.txt"));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Alt_F9_on_a_plain_file_says_it_is_no_archive_and_writes_nothing()
    {
        await using var app = await ArchiveApp.StartAsync();
        var before = app.Fs.AllPaths.ToList();
        ArchiveApp.Cursor(app, app.Left, "notes.txt");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Empty(app.Dialogs.UnpackRequests); // told before any dialog
        Assert.Equal([Loc("Archive_NotAnArchive", "notes.txt")], app.Dialogs.Messages);
        Assert.Equal(before, app.Fs.AllPaths);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Unpacking_a_damaged_archive_is_told()
    {
        await using var app = await ArchiveApp.StartAsync();
        ArchiveApp.Cursor(app, app.Left, "broken.zip");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal(OperationTexts.ForError(VfsPath.Local("/home/broken.zip"), new ArchiveDamagedException("x")),
            Assert.Single(app.Dialogs.Messages));
        Assert.Empty(ArchiveApp.Names(app.Right));
        Assert.Empty(app.Failures);
    }

    /// <summary>The app with /proj/a/x.txt, /proj/b/y.txt and /proj/b/pack.zip found by a search of /proj, the
    /// left panel showing the result set and the right one /dst.</summary>
    private static async Task<TestApp> StartOnResultsAsync()
    {
        var fs = ArchiveApp.FileSystem();
        fs.AddFile("/proj/a/x.txt", "x");
        fs.AddFile("/proj/b/y.txt", "y");
        fs.AddFile("/proj/b/pack.zip", Fixture.Bytes("sample.zip"));
        var app = await ArchiveApp.StartAsync(fs);
        var set = app.Main.SearchResults.Add(VfsPath.Local("/proj"), "Search in /proj: *",
            [VfsPath.Local("/proj/a/x.txt"), VfsPath.Local("/proj/b/y.txt"), VfsPath.Local("/proj/b/pack.zip")]);
        await app.Left.NavigateToAsync(set);
        app.Pump();
        return app;
    }

    [AvaloniaFact]
    public async Task Pack_from_search_results_proposes_a_plain_name_and_starts_relative_text_at_the_files_folder()
    {
        await using var app = await StartOnResultsAsync();
        Mark(app, "x.txt", "y.txt");
        app.Dialogs.AnswerPack("out.zip");

        AltF5(app);
        await ArchiveApp.SettleAsync(app);

        var request = Assert.Single(app.Dialogs.PackRequests);
        Assert.Equal("/dst/" + Loc("Archive_DefaultName") + ".zip", request.Destination);
        var entries = ArchiveApp.ZipEntries(app.Fs, "/proj/a/out.zip"); // the first item's folder
        Assert.Equal(("x", "y"), (entries["x.txt"], entries["y.txt"]));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Unpack_from_search_results_starts_relative_text_at_the_archives_folder()
    {
        await using var app = await StartOnResultsAsync();
        ArchiveApp.Cursor(app, app.Left, "pack.zip");
        app.Dialogs.AnswerUnpack("unpacked");

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("/dst", Assert.Single(app.Dialogs.UnpackRequests).Destination);
        Assert.Equal("hello\n", app.Fs.ReadAllText("/proj/b/unpacked/docs/readme.txt"));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    private static string Loc(string key, params object[] args) => Resources.Loc.Format(key, args);
}
