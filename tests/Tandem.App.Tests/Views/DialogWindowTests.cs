using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Services;
using Tandem.App.Views.Dialogs;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

/// <summary>The real dialog windows, shown modal to a headless owner and driven with keys.</summary>
[Collection(LanguageCollection.Name)]
public class DialogWindowTests : LanguageTestBase
{
    private static (Window Owner, DialogService Service) Start()
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

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible) window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null); // closed by the press
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBox Field(Window dialog) => dialog.GetVisualDescendants().OfType<TextBox>().Single();

    private static async Task<T> Finish<T>(Task<T> task)
    {
        await TestApp.WaitUntil(() => task.IsCompleted);
        return await task;
    }

    [AvaloniaFact]
    public async Task Copy_dialog_accepts_with_enter_and_keeps_a_refused_path_open()
    {
        var (owner, service) = Start();
        var asking = service.AskCopyMoveAsync(new CopyMoveRequest(false, 2, null, "/dst",
            text => Task.FromResult(text.EndsWith('x') ? "refused" : null)));
        var dialog = Dialog<CopyMoveDialog>(owner);
        Assert.Equal("/dst", Field(dialog).Text);

        Field(dialog).Text = "/dst/x";
        Press(dialog, Key.Enter);
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
        Assert.False(asking.IsCompleted);
        Assert.True(dialog.IsVisible);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "refused" && t.IsVisible);

        Field(dialog).Text = "/dst/ok/";
        Press(dialog, Key.Enter);

        Assert.Equal(new CopyMoveAnswer("/dst/ok/", false, false), await Finish(asking));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Esc_cancels_the_copy_dialog()
    {
        var (owner, service) = Start();
        var asking = service.AskCopyMoveAsync(new CopyMoveRequest(true, 1, "a.txt", "/dst/a.txt"));

        Press(Dialog<CopyMoveDialog>(owner), Key.Escape);

        Assert.Null(await Finish(asking));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Enter_in_the_permanent_delete_dialog_cancels()
    {
        var (owner, service) = Start();
        var asking = service.ConfirmDeleteAsync(1, "a.txt", permanent: true);
        var dialog = Dialog<ConfirmDeleteDialog>(owner);
        Assert.True(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Cancel").IsDefault);

        Press(dialog, Key.Enter);

        Assert.False(await Finish(asking));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Enter_in_the_trash_dialog_deletes()
    {
        var (owner, service) = Start();
        var asking = service.ConfirmDeleteAsync(3, null, permanent: false);

        Press(Dialog<ConfirmDeleteDialog>(owner), Key.Enter);

        Assert.True(await Finish(asking));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task A_conflict_dialog_closes_when_its_question_is_withdrawn()
    {
        var (owner, service) = Start();
        var entry = new FileEntry(VfsPath.Local("/a"), EntryKind.File, 1, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, null, false);
        using var cts = new CancellationTokenSource();
        var asking = service.AskConflictAsync(entry, entry, cts.Token);
        var dialog = Dialog<ConflictDialog>(owner);

        await cts.CancelAsync();

        Assert.Equal(ConflictAction.Cancel, (await Finish(asking)).Action);
        Assert.False(dialog.IsVisible);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Conflict_dialog_returns_the_choice_with_apply_to_all()
    {
        var (owner, service) = Start();
        var entry = new FileEntry(VfsPath.Local("/a"), EntryKind.File, 1, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, null, false);
        var asking = service.AskConflictAsync(entry, entry, CancellationToken.None);
        var dialog = Dialog<ConflictDialog>(owner);

        dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
        var overwrite = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == nameof(ConflictAction.Overwrite));
        overwrite.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new ConflictDecision(ConflictAction.Overwrite, true), await Finish(asking));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Without_a_visible_owner_questions_are_cancelled_without_a_window()
    {
        var service = new DialogService();

        Assert.Null(await service.AskCopyMoveAsync(new CopyMoveRequest(false, 1, "a", "/d")));
        Assert.False(await service.ConfirmDeleteAsync(1, "a", permanent: false));
        Assert.Equal(ErrorAction.Abort, await service.AskErrorAsync(VfsPath.Local("/a"), "m", null,
            CancellationToken.None));
    }
}
