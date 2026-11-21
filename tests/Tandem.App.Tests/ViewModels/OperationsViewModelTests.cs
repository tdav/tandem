using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tandem.App.Resources;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Operations;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public class OperationsViewModelTests : LanguageTestBase
{
    /// <summary>A delay the test completes by hand.</summary>
    private sealed class ManualDelay
    {
        public List<(TimeSpan Delay, TaskCompletionSource Done)> Calls { get; } = [];

        public Task Wait(TimeSpan delay, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((delay, done));
            return done.Task.WaitAsync(ct);
        }
    }

    private static async Task Settle()
    {
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Operations_view_lists_running_and_queued()
    {
        var queue = new OperationQueue();
        using var vm = new OperationsViewModel(queue);
        var first = new GateOperation("v", "a.txt");
        var second = new GateOperation("v", "b.txt"); // same volume: waits for the first

        _ = queue.Enqueue(first);
        _ = queue.Enqueue(second);
        await first.Started;
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.Items.Count == 2; });

        Assert.Equal([first, second], vm.Items.Select(i => i.Operation));
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return vm.Items[0].StateText == Loc.Enum(OperationState.Running);
        });
        Assert.Equal(Loc.Enum(OperationState.Queued), vm.Items[1].StateText);
        Assert.Contains("a.txt", vm.Items[0].Title);
        Assert.Contains("/dst", vm.Items[0].Title);
        Assert.Equal("2 operations", vm.IndicatorText);
        Assert.True(vm.HasOperations);

        first.Release();
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.Items.Count == 1; });
        Assert.Same(second, vm.Items[0].Operation);
        second.Release();
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.Items.Count == 0; });
        Assert.False(vm.HasOperations);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)] // no total yet
    [InlineData(0, 50, 0, 0)]
    [InlineData(0, 50, 100, 50)]
    [InlineData(60, 50, 100, 60)] // a failed attempt took bytes back: the bar stays
    [InlineData(0, 150, 100, 100)] // never above 100
    [InlineData(0, -10, 100, 0)] // never below 0
    [InlineData(40, 10, 0, 40)] // a total that dropped to 0 keeps the bar
    [InlineData(0, long.MaxValue / 2, long.MaxValue, 50)] // no overflow
    public void Percent_is_guarded_and_monotonic(double previous, long done, long total, double expected)
    {
        Assert.Equal(expected, OperationItemViewModel.NextPercent(previous, done, total), 3);
    }

    [AvaloniaFact]
    public async Task Progress_bar_never_moves_backwards_and_updates_on_the_ui_thread()
    {
        var queue = new OperationQueue();
        using var vm = new OperationsViewModel(queue);
        var op = new GateOperation();
        _ = queue.Enqueue(op, runImmediately: true);
        await op.Started;
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.Items.Count == 1; });
        var item = vm.Items[0];
        var offThread = false;
        item.PropertyChanged += (_, _) => offThread |= !Dispatcher.UIThread.CheckAccess();

        await Task.Run(() => op.Report(60, 100));
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return item.Percent >= 60; });
        await Task.Delay(150); // past the engine's progress throttle
        await Task.Run(() => op.Report(30, 100));
        await Settle();
        await Task.Delay(150);
        await Task.Run(() => op.Report(30, 100));
        await Settle();

        Assert.Equal(60, item.Percent, 3);
        Assert.False(offThread);
        op.Release();
    }

    [AvaloniaFact]
    public async Task Window_appears_only_if_the_operation_still_runs_after_the_delay()
    {
        var queue = new OperationQueue();
        var delay = new ManualDelay();
        using var vm = new OperationsViewModel(queue, delay.Wait);

        var quick = new GateOperation("q");
        _ = queue.Enqueue(quick, runImmediately: true);
        vm.Track(quick);
        Assert.Equal(OperationsViewModel.WindowDelay, delay.Calls[0].Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(500), OperationsViewModel.WindowDelay);
        quick.Release();
        await quick.Completion;
        delay.Calls[0].Done.SetResult();
        await Settle();
        Assert.False(vm.IsWindowVisible); // finished within the delay: no window

        var slow = new GateOperation("s");
        _ = queue.Enqueue(slow, runImmediately: true);
        vm.Track(slow);
        await slow.Started;
        await Settle();
        Assert.False(vm.IsWindowVisible); // not before the delay
        delay.Calls[1].Done.SetResult();
        await Settle();
        Assert.True(vm.IsWindowVisible);

        slow.Release();
    }

    [AvaloniaFact]
    public async Task Background_hides_the_window_without_cancelling_and_the_indicator_reopens_it()
    {
        var queue = new OperationQueue();
        var delay = new ManualDelay();
        using var vm = new OperationsViewModel(queue, delay.Wait);
        var op = new GateOperation();
        _ = queue.Enqueue(op, runImmediately: true);
        vm.Track(op);
        await op.Started;
        delay.Calls[0].Done.SetResult();
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.IsWindowVisible; });

        vm.SendToBackground();
        Assert.False(vm.IsWindowVisible);
        vm.IsWindowVisible = false; // closing the window does the same
        await Settle();
        Assert.Equal(OperationState.Running, op.State);
        Assert.Equal("1 operation", vm.IndicatorText);

        vm.ShowWindowCommand.Execute(null);
        Assert.True(vm.IsWindowVisible);

        op.Release();
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return !vm.HasOperations; });
        Assert.False(vm.IsWindowVisible); // nothing left to show
    }

    [AvaloniaFact]
    public async Task Pause_resume_and_cancel_show_the_operations_own_state()
    {
        var queue = new OperationQueue();
        using var vm = new OperationsViewModel(queue);
        var op = new GateOperation();
        _ = queue.Enqueue(op, runImmediately: true);
        await op.Started;
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.Items.Count == 1; });
        var item = vm.Items[0];

        item.PauseCommand.Execute(null);
        await Settle();
        // Only a request: until the operation reaches a checkpoint it still runs, and says so; the request can be
        // withdrawn, and asking again would do nothing.
        Assert.Equal(Loc.Enum(OperationState.Running), item.StateText);
        Assert.False(item.CanPause);
        Assert.True(item.CanResume);
        op.Checkpoint();
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return item.StateText == Loc.Enum(OperationState.Paused);
        });
        Assert.True(item.CanResume);
        Assert.False(item.CanPause);

        item.ResumeCommand.Execute(null);
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return item.StateText == Loc.Enum(OperationState.Running);
        });

        item.CancelCommand.Execute(null);
        Assert.Equal(OperationState.Cancelled, (await op.Completion).State);
    }

    [AvaloniaFact]
    public async Task A_pause_request_can_be_withdrawn_before_it_takes_effect()
    {
        var queue = new OperationQueue();
        using var vm = new OperationsViewModel(queue);
        var op = new GateOperation();
        _ = queue.Enqueue(op, runImmediately: true);
        await op.Started;
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.Items.Count == 1; });
        var item = vm.Items[0];
        Assert.True(item.CanPause);
        Assert.False(item.CanResume);

        item.PauseCommand.Execute(null);
        Assert.True(item.ResumeCommand.CanExecute(null));
        item.ResumeCommand.Execute(null);

        Assert.True(item.CanPause);
        Assert.False(item.CanResume);
        Assert.False(op.IsPauseRequested);
        op.Checkpoint(); // the withdrawn request does not stop it
        await Settle();
        Assert.Equal(Loc.Enum(OperationState.Running), item.StateText);
        Assert.Equal(OperationState.Running, op.State);

        item.CancelCommand.Execute(null);
        Assert.Equal(OperationState.Cancelled, (await op.Completion).State);
    }

    [AvaloniaFact]
    public async Task Russian_indicator_uses_plural_forms()
    {
        Loc.SetLanguage("ru");
        var queue = new OperationQueue();
        using var vm = new OperationsViewModel(queue);
        var ops = Enumerable.Range(0, 5).Select(i => new GateOperation("v" + i)).ToArray();
        foreach (var op in ops) _ = queue.Enqueue(op, runImmediately: true);
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.Items.Count == 5; });

        Assert.Equal("5 операций", vm.IndicatorText);
        foreach (var op in ops) op.Release();
    }

    [AvaloniaFact]
    public async Task Operation_writing_its_collected_changes_says_so_and_offers_no_pause()
    {
        var queue = new OperationQueue();
        using var vm = new OperationsViewModel(queue);
        var op = new CommitGateOperation();
        _ = queue.Enqueue(op, runImmediately: true);
        await op.Committing;
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return vm.Items.Count == 1 && vm.Items[0].StateText == Loc.Get("Operation_Committing");
        });

        Assert.Equal("Writing the archive…", vm.Items[0].StateText);
        Assert.False(vm.Items[0].CanPause);

        op.Release();
        Assert.Equal(OperationState.Completed, (await op.Completion).State);
    }
}
