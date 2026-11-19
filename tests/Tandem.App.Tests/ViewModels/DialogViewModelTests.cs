using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Dialogs;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public class DialogViewModelTests : LanguageTestBase
{
    private static FileEntry File(string path, long size, DateTimeOffset modified) =>
        new(VfsPath.Local(path), EntryKind.File, size, modified, modified, null, false);

    [Fact]
    public void Copy_prompt_names_the_single_item_or_counts_them_in_russian()
    {
        Loc.SetLanguage("ru");

        Assert.Equal("Копировать «report.pdf» в:",
            new CopyMoveDialogViewModel(new CopyMoveRequest(false, 1, "report.pdf", "/dst/report.pdf")).Prompt);
        Assert.Equal("Копировать 3 объекта в:",
            new CopyMoveDialogViewModel(new CopyMoveRequest(false, 3, null, "/dst")).Prompt);
        Assert.Equal("Перенести 5 объектов в:",
            new CopyMoveDialogViewModel(new CopyMoveRequest(true, 5, null, "/dst")).Prompt);
        Assert.Equal("Перенести 21 объект в:",
            new CopyMoveDialogViewModel(new CopyMoveRequest(true, 21, null, "/dst")).Prompt);
    }

    [Fact]
    public void Copy_prompt_in_english()
    {
        Assert.Equal("Copy “a.txt” to:", new CopyMoveDialogViewModel(new CopyMoveRequest(false, 1, "a.txt", "/d")).Prompt);
        Assert.Equal("Move 2 items to:", new CopyMoveDialogViewModel(new CopyMoveRequest(true, 2, null, "/d")).Prompt);
        Assert.Equal("Move", new CopyMoveDialogViewModel(new CopyMoveRequest(true, 2, null, "/d")).PrimaryButtonText);
    }

    [Fact]
    public async Task A_drop_dialog_offers_both_actions_and_answers_the_one_chosen()
    {
        var copyFirst = new CopyMoveDialogViewModel(new CopyMoveRequest(false, 1, "a.txt", "/d/a.txt", OffersBoth: true));
        Assert.True(copyFirst.OffersBoth);
        Assert.False(copyFirst.MoveIsDefault);
        Assert.Equal("Copy or Move", copyFirst.Title);
        Assert.Equal("Copy or move “a.txt” to:", copyFirst.Prompt);
        Assert.False((await copyFirst.AcceptAsync())!.IsMove); // Enter: the pre-selected Copy
        Assert.True((await copyFirst.AcceptAsync(move: true))!.IsMove);

        var moveFirst = new CopyMoveDialogViewModel(new CopyMoveRequest(true, 2, null, "/d", OffersBoth: true));
        Assert.True(moveFirst.MoveIsDefault); // Cmd was reported held at the drop
        Assert.Equal("Copy or move 2 items to:", moveFirst.Prompt);
        Assert.True((await moveFirst.AcceptAsync())!.IsMove);
        Assert.False((await moveFirst.AcceptAsync(move: false))!.IsMove);
    }

    [Fact]
    public void The_drop_dialog_in_russian()
    {
        Loc.SetLanguage("ru");
        var vm = new CopyMoveDialogViewModel(new CopyMoveRequest(false, 1, "a.txt", "/d/a.txt", OffersBoth: true));

        Assert.Equal("Копирование или перенос", vm.Title);
        Assert.Equal("Копировать или перенести «a.txt» в:", vm.Prompt);
    }

    [Fact]
    public async Task A_keyboard_dialog_always_answers_its_own_action()
    {
        var move = new CopyMoveDialogViewModel(new CopyMoveRequest(true, 1, "a.txt", "/d/a.txt"));
        var copy = new CopyMoveDialogViewModel(new CopyMoveRequest(false, 1, "a.txt", "/d/a.txt"));

        Assert.False(move.OffersBoth);
        Assert.True((await move.AcceptAsync(move: false))!.IsMove);
        Assert.False((await copy.AcceptAsync(move: true))!.IsMove);
    }

    [Fact]
    public async Task Copy_dialog_starts_with_the_proposed_path_and_safe_defaults()
    {
        var vm = new CopyMoveDialogViewModel(new CopyMoveRequest(false, 1, "a.txt", "/dst/a.txt"));

        Assert.Equal("/dst/a.txt", vm.DestinationText);
        Assert.False(vm.VerifyContent);
        Assert.False(vm.RunImmediately);
        var answer = await vm.AcceptAsync();
        Assert.Equal(new CopyMoveAnswer("/dst/a.txt", false, false), answer);
    }

    [Fact]
    public async Task Copy_dialog_stays_open_with_the_validation_error()
    {
        var seen = new List<string>();
        var vm = new CopyMoveDialogViewModel(new CopyMoveRequest(false, 3, null, "/dst", text =>
        {
            seen.Add(text);
            return Task.FromResult(text == "/dst/x" ? "bad" : null);
        }));

        vm.DestinationText = "/dst/x";
        Assert.Null(await vm.AcceptAsync());
        Assert.Equal("bad", vm.Error);

        vm.DestinationText = "/dst/ok/";
        vm.VerifyContent = true;
        vm.RunImmediately = true;
        Assert.Null(vm.Error); // editing clears the old message
        Assert.Equal(new CopyMoveAnswer("/dst/ok/", true, true), await vm.AcceptAsync());
        Assert.Equal(["/dst/x", "/dst/ok/"], seen);
    }

    [Fact]
    public async Task Name_dialog_validates()
    {
        var vm = new NameDialogViewModel("NewFolder_Title", "", text =>
            Task.FromResult(text.Length == 0 ? "empty" : null));

        Assert.Equal(Loc.Get("NewFolder_Title"), vm.Title);
        Assert.Null(await vm.AcceptAsync());
        Assert.Equal("empty", vm.Error);
        vm.Text = "a/b";
        Assert.Equal("a/b", await vm.AcceptAsync());
    }

    [Fact]
    public void Trash_confirmation_names_the_item_and_defaults_to_delete()
    {
        Loc.SetLanguage("ru");
        var vm = new ConfirmDeleteDialogViewModel(1, "report.pdf", permanent: false);

        Assert.Contains("«report.pdf»", vm.Message);
        Assert.Contains("Корзин", vm.Message);
        Assert.False(vm.CancelIsDefault);
        Assert.Null(vm.Warning);
    }

    [Fact]
    public void Permanent_confirmation_says_it_cannot_be_undone_and_defaults_to_cancel()
    {
        Loc.SetLanguage("ru");
        var vm = new ConfirmDeleteDialogViewModel(3, null, permanent: true);

        Assert.Contains("3 объекта", vm.Message);
        Assert.DoesNotContain("Корзин", vm.Message);
        Assert.Equal(Loc.Get("Delete_CannotUndo"), vm.Warning);
        Assert.True(vm.CancelIsDefault);
    }

    [Fact]
    public void Conflict_dialog_shows_size_and_date_of_both_files()
    {
        var older = new DateTimeOffset(2024, 1, 2, 3, 4, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2025, 6, 7, 8, 9, 0, TimeSpan.Zero);
        var vm = new ConflictDialogViewModel(File("/src/a.txt", 2048, newer), File("/dst/a.txt", 10, older));

        Assert.Contains("a.txt", vm.Message);
        Assert.Contains(SizeFormatter.Format(2048), vm.SourceDetails);
        Assert.Contains(newer.ToLocalTime().ToString("g", Loc.Culture), vm.SourceDetails);
        Assert.Contains(SizeFormatter.Format(10), vm.ExistingDetails);
        Assert.Contains(older.ToLocalTime().ToString("g", Loc.Culture), vm.ExistingDetails);
        Assert.False(vm.ApplyToAll);

        vm.ApplyToAll = true;
        Assert.Equal(new ConflictDecision(ConflictAction.Skip, true), vm.Decide(ConflictAction.Skip));
        Assert.Equal(ConflictAction.Cancel, vm.Actions[^1]);
    }

    [Fact]
    public void Error_dialog_offers_retry_skip_skip_all_and_abort()
    {
        var vm = new ErrorDialogViewModel(VfsPath.Local("/a.txt"), "message", "raw detail");

        Assert.Equal("message", vm.Message);
        Assert.Equal("raw detail", vm.Detail);
        Assert.Equal([ErrorAction.Retry, ErrorAction.Skip, ErrorAction.SkipAll, ErrorAction.Abort], vm.Actions);
    }

    [Fact]
    public void Summary_words_each_skip_reason()
    {
        var vm = new SummaryDialogViewModel(
        [
            new SkippedItem(VfsPath.Local("/a"), SkipReason.ConflictSkipped),
            new SkippedItem(VfsPath.Local("/b"), SkipReason.Error, "disk on fire"),
        ]);

        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal("/a", vm.Lines[0].Path);
        Assert.Equal(Loc.Enum(SkipReason.ConflictSkipped), vm.Lines[0].Reason);
        Assert.Equal("disk on fire", vm.Lines[1].Detail);
        Assert.Contains("2", vm.Message);
    }

    [Fact]
    public void Mask_dialog_refuses_an_empty_mask()
    {
        var vm = new MaskDialogViewModel(select: true, "*.txt");

        Assert.Equal("*.txt", vm.Text);
        Assert.Equal(Loc.Get("Mask_SelectTitle"), vm.Title);
        vm.Text = "  ";
        Assert.Null(vm.Accept());
        Assert.NotNull(vm.Error);
        vm.Text = "*.md";
        Assert.Equal("*.md", vm.Accept());
        Assert.Equal(Loc.Get("Mask_UnselectTitle"), new MaskDialogViewModel(select: false, "*.*").Title);
    }
}
