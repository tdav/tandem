using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;

namespace Tandem.App.ViewModels;

/// <summary>
/// The progress window's model: every operation of the <see cref="OperationQueue"/> not ended yet, running and
/// waiting, in the queue's order. The window shows itself (<see cref="IsWindowVisible"/>) only for an operation that
/// is still going <see cref="WindowDelay"/> after it was started (<see cref="Track"/>); "Background" and closing the
/// window hide it without touching the operations, and the main window's indicator shows it again.
/// </summary>
/// <remarks>Created and used on the UI thread; the queue's and the operations' events arrive on any thread and are
/// posted to it.</remarks>
public sealed partial class OperationsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan WindowDelay = TimeSpan.FromMilliseconds(500);

    private readonly OperationQueue _queue;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SynchronizationContext _ui;
    private readonly ObservableCollection<OperationItemViewModel> _items = [];
    private readonly CancellationTokenSource _disposed = new();
    private int _syncPending;

    /// <param name="delay">Waits before the window may appear; <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// when null.</param>
    public OperationsViewModel(OperationQueue queue, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        _delay = delay ?? Task.Delay;
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("The operations view must be created on the UI thread.");
        Items = new ReadOnlyObservableCollection<OperationItemViewModel>(_items);
        queue.Changed += OnQueueChanged;
        Sync();
    }

    /// <summary>The operations not ended yet, in the queue's order.</summary>
    public ReadOnlyObservableCollection<OperationItemViewModel> Items { get; }

    public bool HasOperations => _items.Count > 0;

    /// <summary>"2 operations", for the main window's indicator.</summary>
    public string IndicatorText => Loc.Count("Count_Operations", _items.Count);

    /// <summary>Whether the progress window is shown. Hiding it never cancels anything.</summary>
    [ObservableProperty]
    public partial bool IsWindowVisible { get; set; }

    /// <summary>A newly started operation: the window appears if it is still going after <see cref="WindowDelay"/>.</summary>
    public void Track(FileOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = ShowIfStillRunningAsync(operation);
    }

    /// <summary>"Background": hides the window; the operations go on.</summary>
    [RelayCommand]
    public void SendToBackground() => IsWindowVisible = false;

    /// <summary>The indicator: shows the window again while there is something to show.</summary>
    [RelayCommand]
    public void ShowWindow()
    {
        if (HasOperations) IsWindowVisible = true;
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        _queue.Changed -= OnQueueChanged;
        foreach (var item in _items) item.Detach();
        _items.Clear();
    }

    private async Task ShowIfStillRunningAsync(FileOperation operation)
    {
        try
        {
            await _delay(WindowDelay, _disposed.Token);
        }
        catch (Exception)
        {
            return; // disposed, or the delay failed: no window rather than a crash
        }
        if (_disposed.IsCancellationRequested || IsFinal(operation.State)) return;
        Sync(); // the queue's own notice may still be on its way
        if (_items.Any(i => i.Operation == operation)) IsWindowVisible = true;
    }

    internal static bool IsFinal(OperationState state) =>
        state is OperationState.Completed or OperationState.Cancelled or OperationState.Failed;

    /// <summary>Any thread; one pending post covers a burst of changes.</summary>
    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _syncPending, 1) == 1) return;
        try
        {
            _ui.Post(_ =>
            {
                Volatile.Write(ref _syncPending, 0);
                Sync();
            }, null);
        }
        catch (Exception)
        {
            // the UI is shutting down
        }
    }

    /// <summary>UI thread: makes <see cref="Items"/> match the queue.</summary>
    private void Sync()
    {
        if (_disposed.IsCancellationRequested) return;
        var operations = _queue.Operations;
        var countBefore = _items.Count;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (operations.Contains(_items[i].Operation)) continue;
            _items[i].Detach();
            _items.RemoveAt(i);
        }
        for (var i = 0; i < operations.Count; i++)
        {
            var index = IndexOf(operations[i]);
            if (index == i) continue;
            if (index >= 0) _items.Move(index, i);
            else _items.Insert(i, new OperationItemViewModel(operations[i], _ui));
        }
        foreach (var item in _items) item.Update();
        if (_items.Count == countBefore) return;
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(IndicatorText));
        if (_items.Count == 0) IsWindowVisible = false;
    }

    private int IndexOf(FileOperation operation)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Operation == operation)
                return i;
        return -1;
    }
}

/// <summary>One operation in the progress window: its title, state, progress bar and buttons. The state shown is
/// always the operation's own: pausing is a request the operation may honour late or not at all.</summary>
public sealed partial class OperationItemViewModel : ObservableObject
{
    private readonly SynchronizationContext _ui;
    private int _updatePending;
    private bool _detached;

    internal OperationItemViewModel(FileOperation operation, SynchronizationContext ui)
    {
        Operation = operation;
        _ui = ui;
        Title = OperationTexts.Title(operation.Kind, operation.Sources, operation.Destination);
        operation.Changed += OnOperationChanged;
        Update();
    }

    public FileOperation Operation { get; }

    public string Title { get; }

    [ObservableProperty]
    public partial string StateText { get; private set; } = "";

    /// <summary>0–100, from the bytes; it never moves backwards.</summary>
    [ObservableProperty]
    public partial double Percent { get; private set; }

    [ObservableProperty]
    public partial string? CurrentItem { get; private set; }

    /// <summary>Files and bytes done of the totals, the speed and the time left, when known.</summary>
    [ObservableProperty]
    public partial string DetailText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    public partial bool CanPause { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool CanResume { get; private set; }

    /// <summary>The next value of the bar: the bytes' share in percent, within 0–100, and never below
    /// <paramref name="previous"/> (bytes of a failed attempt are taken back; the bar does not jump back for it). An
    /// unknown or zero total keeps the bar where it is.</summary>
    public static double NextPercent(double previous, long done, long total)
    {
        if (total <= 0) return previous;
        var percent = Math.Clamp((double)done / total * 100, 0, 100);
        return Math.Max(previous, percent);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => Operation.Pause();

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => Operation.Resume();

    [RelayCommand]
    private void Cancel() => Operation.Cancel();

    /// <summary>UI thread: reads the operation's state and progress.</summary>
    internal void Update()
    {
        if (_detached) return;
        var state = Operation.State;
        var progress = Operation.Progress;
        StateText = Loc.Enum(state);
        Percent = NextPercent(Percent, progress.BytesDone, progress.BytesTotal);
        CurrentItem = progress.CurrentItem;
        DetailText = Describe(progress);
        CanPause = state is OperationState.Scanning or OperationState.Running;
        CanResume = state == OperationState.Paused;
    }

    internal void Detach()
    {
        _detached = true;
        Operation.Changed -= OnOperationChanged;
    }

    /// <summary>The operation's thread; one pending post covers a burst.</summary>
    private void OnOperationChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _updatePending, 1) == 1) return;
        try
        {
            _ui.Post(_ =>
            {
                Volatile.Write(ref _updatePending, 0);
                Update();
            }, null);
        }
        catch (Exception)
        {
            // the UI is shutting down
        }
    }

    private static string Describe(OperationProgress progress)
    {
        var parts = new List<string>();
        if (progress.FilesTotal > 0)
            parts.Add(Loc.Format("Progress_Files", progress.FilesDone, progress.FilesTotal));
        if (progress.BytesTotal > 0)
            parts.Add(Loc.Format("Progress_Bytes", SizeFormatter.Format(Math.Max(0, progress.BytesDone)),
                SizeFormatter.Format(progress.BytesTotal)));
        if (progress.BytesPerSecond > 0)
            parts.Add(Loc.Format("Progress_Speed", SizeFormatter.Format((long)progress.BytesPerSecond)));
        if (progress.Remaining is { } remaining)
            parts.Add(Loc.Format("Progress_Remaining", remaining.ToString(@"h\:mm\:ss", Loc.Culture)));
        return string.Join(" · ", parts);
    }
}
