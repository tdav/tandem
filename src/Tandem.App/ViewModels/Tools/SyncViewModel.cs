using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Tools.Sync;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Tools;

public sealed partial class SyncRowViewModel(SyncItem item) : ObservableObject
{
    public SyncItem Item { get; } = item;

    private SyncAction _action = item.DefaultAction;

    public SyncAction Action
    {
        get => _action;
        set
        {
            if (!Item.AllowedActions.Contains(value) || !SetProperty(ref _action, value)) return;
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(ActionDescription));
        }
    }

    public string LeftText { get; } = Describe(item.Left);
    public string RightText { get; } = Describe(item.Right);
    public string ActionText => Loc.Get($"Sync_Symbol_{Action}");
    public string ActionDescription => Loc.Enum(Action);
    public string StateText => Loc.Enum(Item.State);
    public string RelativePath => Item.RelativePath;

    private static string Describe(FileEntry? entry) => entry is null ? "" :
        Loc.Format("Sync_EntryDetails", entry.IsDirectoryLike ? Loc.Get("Panel_Folder") : SizeFormatter.Format(entry.Size),
            entry.Modified.ToLocalTime());
}

public sealed record SyncTotals(int ToRight, long ToRightBytes, int ToLeft, long ToLeftBytes, int DeleteLeft, int DeleteRight)
{
    public string Display => Loc.Format("Sync_Totals", ToRight, SizeFormatter.Format(ToRightBytes), ToLeft,
        SizeFormatter.Format(ToLeftBytes), DeleteLeft, DeleteRight);
}

public sealed record SyncConfirmation(SyncTotals Totals, bool LeftToTrash, bool RightToTrash);

public sealed partial class SyncViewModel : ObservableObject, IDisposable
{
    private readonly ProviderRegistry _providers;
    private readonly VfsPath _leftRoot;
    private readonly VfsPath _rightRoot;
    private readonly Func<SyncOperation, Task<OperationResult?>> _run;
    private readonly Func<SyncConfirmation, Task<bool>> _confirm;
    private readonly ITrash? _trash;
    private readonly INativeCopy? _nativeCopy;
    private readonly IOperationInteraction _interaction;
    private CancellationTokenSource? _scan;
    private SyncComparison? _comparison;
    private SyncRowViewModel[] _allRows = [];

    [ObservableProperty] public partial bool ByContent { get; set; }
    [ObservableProperty] public partial bool Subfolders { get; set; }
    [ObservableProperty] public partial string Mask { get; set; }
    [ObservableProperty] public partial bool IncludeHidden { get; set; }
    [ObservableProperty] public partial bool ShowEqual { get; set; }
    [ObservableProperty] public partial bool ShowToRight { get; set; }
    [ObservableProperty] public partial bool ShowToLeft { get; set; }
    [ObservableProperty] public partial bool ShowDifferent { get; set; }
    [ObservableProperty] public partial IReadOnlyList<SyncRowViewModel> Rows { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    [NotifyPropertyChangedFor(nameof(TotalsText))]
    public partial SyncTotals Totals { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsComparing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsSynchronizing { get; private set; }

    [ObservableProperty] public partial string Status { get; private set; }
    [ObservableProperty] public partial string? UnreadableStatus { get; private set; }
    [ObservableProperty] public partial string? UnreadablePaths { get; private set; }

    public string LeftTitle { get; }
    public string RightTitle { get; }
    public string TotalsText => Totals.Display;
    public bool IsDisposed { get; private set; }
    public bool CanEdit => !IsComparing && !IsSynchronizing && !IsDisposed;
    public bool CanSynchronize => CanEdit && _comparison is not null &&
        (Totals.ToRight > 0 || Totals.ToLeft > 0 || Totals.DeleteLeft > 0 || Totals.DeleteRight > 0);

    public SyncViewModel(ProviderRegistry providers, VfsPath leftRoot, VfsPath rightRoot,
        Func<SyncOperation, Task<OperationResult?>> run, Func<SyncConfirmation, Task<bool>> confirm, ITrash? trash,
        INativeCopy? nativeCopy, IOperationInteraction interaction)
    {
        _providers = providers;
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        _run = run;
        _confirm = confirm;
        _trash = trash;
        _nativeCopy = nativeCopy;
        _interaction = interaction;
        LeftTitle = providers.DisplayPath(leftRoot);
        RightTitle = providers.DisplayPath(rightRoot);
        Subfolders = true;
        Mask = "*";
        IncludeHidden = true;
        ShowToRight = ShowToLeft = ShowDifferent = true;
        Rows = [];
        Totals = new(0, 0, 0, 0, 0, 0);
        Status = "";
    }

    partial void OnByContentChanged(bool value) => Invalidate();
    partial void OnSubfoldersChanged(bool value) => Invalidate();
    partial void OnMaskChanged(string value) => Invalidate();
    partial void OnIncludeHiddenChanged(bool value) => Invalidate();
    partial void OnShowEqualChanged(bool value) => Filter();
    partial void OnShowToRightChanged(bool value) => Filter();
    partial void OnShowToLeftChanged(bool value) => Filter();
    partial void OnShowDifferentChanged(bool value) => Filter();

    [RelayCommand]
    public async Task CompareAsync()
    {
        if (!CanEdit) return;
        Invalidate();
        var options = new SyncOptions(ByContent ? SyncCompareMode.Content : SyncCompareMode.SizeAndDate,
            Subfolders, Mask ?? "", IncludeHidden);
        using var scan = new CancellationTokenSource();
        _scan = scan;
        IsComparing = true;
        Status = Loc.Format("Sync_Progress", 0, 0);
        var progress = new Progress<SyncProgress>(p =>
        {
            if (ReferenceEquals(scan, _scan) && !scan.IsCancellationRequested)
                Status = Loc.Format("Sync_Progress", p.FoldersScanned, p.FilesCompared);
        });
        try
        {
            var plan = await Task.Run(() => SyncPlanner.CompareAsync(_providers, _leftRoot, _rightRoot, options,
                new ThrottledProgress(progress), scan.Token), scan.Token);
            scan.Token.ThrowIfCancellationRequested();
            _comparison = plan;
            _allRows = plan.Items.Select(item => new SyncRowViewModel(item)).ToArray();
            foreach (var row in _allRows) row.PropertyChanged += RowChanged;
            UnreadableStatus = plan.UnreadableFolders.Count > 0
                ? Loc.Format("Sync_Unreadable", plan.UnreadableFolders.Count) : null;
            UnreadablePaths = plan.UnreadableFolders.Count > 0 ? string.Join(Environment.NewLine, plan.UnreadableFolders) : null;
            Status = Loc.Format("Sync_Compared", _allRows.Length);
            Filter();
            UpdateTotals();
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
            if (!IsDisposed) Status = Loc.Get("Sync_Stopped");
        }
        catch (ArgumentException)
        {
            Status = Loc.Get("Sync_Overlapping");
        }
        catch (Exception error) when (ItemErrors.IsItemError(error) || error is KeyNotFoundException)
        {
            Status = OperationTexts.ForFailure(error);
        }
        finally
        {
            _scan = null;
            IsComparing = false;
        }
    }

    [RelayCommand]
    public void StopComparing() => _scan?.Cancel();

    [RelayCommand]
    public async Task SynchronizeAsync()
    {
        if (!CanSynchronize || _comparison is not { } plan) return;
        var decisions = _allRows.Where(row => row.Action != SyncAction.None)
            .Select(row => new SyncDecision(row.Item, row.Action)).ToArray();
        var confirmation = new SyncConfirmation(Totals, CanTrash(_leftRoot), CanTrash(_rightRoot));
        IsSynchronizing = true;
        try
        {
            if (!await _confirm(confirmation) || IsDisposed || !ReferenceEquals(plan, _comparison)) return;
            var operation = new SyncOperation(_providers, plan, decisions, _interaction, _trash, _nativeCopy);
            await _run(operation);
        }
        finally
        {
            IsSynchronizing = false;
        }
        await CompareAsync();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Invalidate();
    }

    private bool CanTrash(VfsPath root) =>
        _trash is not null && _providers.For(root).Capabilities.HasFlag(ProviderCapabilities.Trash);

    private void Invalidate()
    {
        _scan?.Cancel();
        _comparison = null;
        foreach (var row in _allRows) row.PropertyChanged -= RowChanged;
        _allRows = [];
        Rows = [];
        UnreadableStatus = null;
        UnreadablePaths = null;
        Status = "";
        UpdateTotals();
        OnPropertyChanged(nameof(CanSynchronize));
    }

    private void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SyncRowViewModel.Action)) return;
        UpdateTotals();
        if (!ShowToRight || !ShowToLeft || !ShowDifferent) Filter();
    }

    private void Filter() => Rows = _allRows.Where(row =>
        (row.Item.State != SyncState.Equal || ShowEqual) && (row.Action switch
        {
            SyncAction.CopyToRight => ShowToRight,
            SyncAction.CopyToLeft => ShowToLeft,
            _ => row.Item.State == SyncState.Equal || ShowDifferent,
        })).ToArray();

    private void UpdateTotals()
    {
        int toRight = 0, toLeft = 0, deleteLeft = 0, deleteRight = 0;
        long rightBytes = 0, leftBytes = 0;
        foreach (var row in _allRows)
            switch (row.Action)
            {
                case SyncAction.CopyToRight:
                    toRight++;
                    if (row.Item.Left is { Kind: EntryKind.File } left) rightBytes += left.Size;
                    break;
                case SyncAction.CopyToLeft:
                    toLeft++;
                    if (row.Item.Right is { Kind: EntryKind.File } right) leftBytes += right.Size;
                    break;
                case SyncAction.DeleteLeft: deleteLeft++; break;
                case SyncAction.DeleteRight: deleteRight++; break;
            }
        Totals = new(toRight, rightBytes, toLeft, leftBytes, deleteLeft, deleteRight);
    }

    private sealed class ThrottledProgress(IProgress<SyncProgress> target) : IProgress<SyncProgress>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _next;

        public void Report(SyncProgress value)
        {
            if (_clock.ElapsedMilliseconds < _next) return;
            _next = _clock.ElapsedMilliseconds + 100;
            target.Report(value);
        }
    }
}
