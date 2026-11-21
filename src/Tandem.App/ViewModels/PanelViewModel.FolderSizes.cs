using Tandem.Core.Tools;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

public sealed partial class PanelViewModel
{
    private readonly Dictionary<VfsPath, FolderSize> _folderSizes = [];
    private Dictionary<VfsPath, EntryRowViewModel>? _sizedRows;
    private CancellationTokenSource? _folderSizeCts;
    private VfsPath? _sizingPath;
    private bool _hasFolders;

    public bool IsSizing => _folderSizeCts is not null;
    public bool HasFolders => _hasFolders;

    public Task ComputeFolderSizeAsync(int rowIndex) =>
        (uint)rowIndex < (uint)_rows.Count && _rows[rowIndex].Entry is { Kind: EntryKind.Directory } entry
            ? ComputeFolderSizesAsync([entry.Path])
            : Task.CompletedTask;

    public Task ComputeAllFolderSizesAsync() =>
        ComputeFolderSizesAsync(_rows.Items.Where(row => row.Entry?.Kind == EntryKind.Directory)
            .Select(row => row.Entry!.Path).Distinct().ToArray());

    private async Task ComputeFolderSizesAsync(IReadOnlyList<VfsPath> paths)
    {
        if (_disposed || IsSizing || _showingPreview || paths.Count == 0) return;
        using var run = new CancellationTokenSource();
        _folderSizeCts = run;
        AttachFolderSizes(_rows.Items);
        OnPropertyChanged(nameof(IsSizing));
        try
        {
            foreach (var path in paths)
            {
                run.Token.ThrowIfCancellationRequested();
                _sizingPath = path;
                UpdateSizeRow(path);
                var provider = _providers.For(path);
                var size = await Task.Run(() => FolderSizeCalculator.ComputeAsync(provider, path, null, run.Token));
                run.Token.ThrowIfCancellationRequested();
                _folderSizes[path] = size;
                _sizingPath = null;
                UpdateSizeRow(path);
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(run, _folderSizeCts))
            {
                var current = _sizingPath;
                _sizingPath = null;
                if (current is { } path) UpdateSizeRow(path);
                _folderSizeCts = null;
                _sizedRows = null;
                OnPropertyChanged(nameof(IsSizing));
            }
        }
    }

    private void UpdateSizeRow(VfsPath path)
    {
        if (_sizedRows?.GetValueOrDefault(path) is not { } row) return;
        var previous = row.SelectableBytes;
        row.ApplyFolderSize(_folderSizes.GetValueOrDefault(path), _sizingPath == path);
        var difference = row.SelectableBytes - previous;
        if (difference == 0) return;
        _totalBytes += difference;
        OnPropertyChanged(nameof(TotalBytes));
        if (!row.IsSelected) return;
        _selectedBytes += difference;
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(StatusText));
    }

    private void AttachFolderSizes(IReadOnlyList<EntryRowViewModel> rows)
    {
        var visible = _folderSizeCts is not null ? new Dictionary<VfsPath, EntryRowViewModel>() : null;
        var hasFolders = false;
        foreach (var row in rows)
        {
            if (row.Entry is not { Kind: EntryKind.Directory } entry) continue;
            hasFolders = true;
            row.ApplyFolderSize(_folderSizes.GetValueOrDefault(entry.Path), _sizingPath == entry.Path);
            if (visible is not null) visible[entry.Path] = row;
        }
        _sizedRows = visible;
        SetProperty(ref _hasFolders, hasFolders, nameof(HasFolders));
    }

    private void PruneFolderSizes(IEnumerable<FileEntry> entries)
    {
        if (_folderSizes.Count == 0) return;
        var directories = entries.Where(entry => entry.Kind == EntryKind.Directory).Select(entry => entry.Path).ToHashSet();
        foreach (var removed in _folderSizes.Keys.Where(path => !directories.Contains(path)).ToArray())
            _folderSizes.Remove(removed);
    }

    private void ResetFolderSizes()
    {
        var running = _folderSizeCts;
        _folderSizeCts = null;
        running?.Cancel();
        _folderSizes.Clear();
        _sizedRows = null;
        _sizingPath = null;
        if (running is not null) OnPropertyChanged(nameof(IsSizing));
    }
}
