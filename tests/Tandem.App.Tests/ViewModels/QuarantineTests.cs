using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Tests.Views;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

/// <summary>What leaves an archive that came from outside keeps its quarantine mark: unpacking, copies and moves out
/// of it, and the temporary copy Enter opens. The marks are the in-memory ones of <see cref="FakeQuarantine"/>.</summary>
[Collection(LanguageCollection.Name)]
public class QuarantineTests : LanguageTestBase
{
    private const string Mark = "0083;6560a1b2;Safari;";

    private static void AltF9(TestApp app) => app.Press(Key.F9, RawInputModifiers.Alt, PhysicalKey.F9);

    private static readonly string[] UnpackedSample =
    [
        "/dst/a.txt", "/dst/docs", "/dst/docs/readme.txt", "/dst/docs/sub", "/dst/docs/sub/data.bin", "/dst/empty",
        "/dst/привет.txt",
    ];

    [AvaloniaFact]
    public async Task Unpacking_a_marked_archive_marks_every_file_and_folder_it_writes()
    {
        await using var app = await ArchiveApp.StartAsync();
        app.Quarantine.Add("/home/sample.zip", Mark);
        ArchiveApp.Cursor(app, app.Left, "sample.zip");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("a", app.Fs.ReadAllText("/dst/a.txt"));
        var written = app.Quarantine.Written;
        Assert.Equal(UnpackedSample, written.Keys.Order(StringComparer.Ordinal));
        Assert.All(written.Values, value => Assert.Equal(Mark, value));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Unpacking_an_archive_without_a_mark_marks_nothing()
    {
        await using var app = await ArchiveApp.StartAsync();
        ArchiveApp.Cursor(app, app.Left, "sample.zip");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("a", app.Fs.ReadAllText("/dst/a.txt"));
        Assert.Equal(["/home/sample.zip"], app.Quarantine.Reads); // read once for the whole operation
        Assert.Empty(app.Quarantine.Written);
    }

    [AvaloniaTheory]
    [InlineData(Key.F5, PhysicalKey.F5)]
    [InlineData(Key.F6, PhysicalKey.F6)]
    public async Task Copy_and_move_out_of_a_marked_archive_mark_what_they_write(Key key, PhysicalKey physical)
    {
        await using var app = await ArchiveApp.StartAsync();
        app.Quarantine.Add("/home/sample.zip", Mark);
        await ArchiveApp.EnterAsync(app, "sample.zip");
        ArchiveApp.Cursor(app, app.Left, "docs");
        app.Dialogs.AnswerCopyMove();

        app.Press(key, physical: physical);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("hello\n", app.Fs.ReadAllText("/dst/docs/readme.txt"));
        Assert.Equal(["/dst/docs", "/dst/docs/readme.txt", "/dst/docs/sub", "/dst/docs/sub/data.bin"],
            app.Quarantine.Written.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Copy_between_local_folders_reads_and_writes_no_mark()
    {
        await using var app = await ArchiveApp.StartAsync();
        app.Quarantine.Add("/home/notes.txt", Mark);
        ArchiveApp.Cursor(app, app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();

        app.Press(Key.F5, physical: PhysicalKey.F5);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("notes", app.Fs.ReadAllText("/dst/notes.txt"));
        Assert.Empty(app.Quarantine.Reads);
        Assert.Empty(app.Quarantine.Written);
    }

    [AvaloniaFact]
    public async Task Mark_that_cannot_be_written_is_logged_and_the_unpack_completes()
    {
        await using var app = await ArchiveApp.StartAsync();
        app.Quarantine.Add("/home/sample.zip", Mark);
        app.Quarantine.SetFailure = new IOException("xattr refused");
        ArchiveApp.Cursor(app, app.Left, "sample.zip");
        app.Dialogs.AnswerUnpack();

        AltF9(app);
        await ArchiveApp.SettleAsync(app);

        Assert.Equal("hello\n", app.Fs.ReadAllText("/dst/docs/readme.txt"));
        Assert.Equal(ArchiveApp.RootNames, ArchiveApp.Names(app.Right));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
        Assert.Contains("xattr refused", File.ReadAllText(app.LogFile), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Enter_on_a_file_in_a_marked_archive_marks_the_temporary_copy_it_opens()
    {
        await using var app = await ArchiveApp.StartAsync();
        app.Quarantine.Add("/home/sample.zip", Mark);
        await ArchiveApp.EnterAsync(app, "sample.zip");
        ArchiveApp.Cursor(app, app.Left, "a.txt");

        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Shell.Calls.Count > 0;
        });

        var (call, copy) = Assert.Single(app.Shell.Calls);
        Assert.Equal("open", call);
        Assert.StartsWith(app.Temp.Directory, copy, StringComparison.Ordinal);
        Assert.Equal(Mark, Assert.Single(app.Quarantine.Written, m => m.Key == copy).Value);
    }

    [AvaloniaFact]
    public async Task Item_deep_in_an_archive_lies_in_the_archive_file_and_a_local_item_in_none()
    {
        await using var app = await ArchiveApp.StartAsync();
        var outer = await app.Main.ArchiveMounts.MountAsync(VfsPath.Local("/home/sample.zip"), CancellationToken.None);
        var carrier = app.Main.Files.Quarantine!;

        Assert.Equal(VfsPath.Local("/home/sample.zip"), carrier.OutermostFile(outer.Combine("docs").Combine("readme.txt")));
        Assert.Null(carrier.OutermostFile(VfsPath.Local("/home/notes.txt")));
    }
}
