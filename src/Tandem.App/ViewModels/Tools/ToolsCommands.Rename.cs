using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;
using Tandem.Core.Tools.Rename;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Tools;

public sealed partial class ToolsCommands
{
    private IReadOnlyList<RenamedItem> _renameUndo = [];
    private bool _renameRunning;

    public bool CanUndoRename => _renameUndo.Count != 0 && !_renameRunning;

    partial void RegisterRename()
    {
        On(CommandIds.ToolsMultiRename, MultiRenameAsync, CanMultiRename);
        On(CommandIds.ToolsUndoRename, UndoRenameAsync, () => CanUndoRename);
    }

    private bool CanMultiRename() => !_renameRunning && Active is { Error: null } panel &&
        panel.GetOperationTargets() is { Count: > 0 } targets &&
        targets.All(entry => entry.Path.Parent is { } folder && _files.CanWriteTo(folder));

    private async Task MultiRenameAsync()
    {
        if (Active is not { } panel) return;
        _renameRunning = true;
        UpdateAvailability();
        try
        {
            var targets = panel.GetOperationTargets();
            var names = await Task.Run(() => ReadRenameNamesAsync(targets));
            if (_disposed) return;
            var plan = await _services.ToolWindows.ShowDialogAsync(new MultiRenameViewModel(targets, names, TimeZoneInfo.Local));
            if (plan is not { CanRun: true } || _disposed) return;
            var steps = plan.Items.Where(item => item.Status == RenameStatus.Ok)
                .Select(item => new RenameStep(item.Entry.Path, item.Entry.Path.Parent!.Value.Combine(item.NewName)))
                .ToArray();
            var operation = new BatchRenameOperation(_services.Providers, steps, _interaction);
            await RunRenameAsync(operation);
            if (operation.Completed.Count != 0) _renameUndo = operation.Completed.ToArray();
        }
        finally
        {
            _renameRunning = false;
            if (!_disposed) UpdateAvailability();
        }
    }

    private async Task UndoRenameAsync()
    {
        _renameRunning = true;
        UpdateAvailability();
        try
        {
            var undo = _renameUndo;
            var confirmed = await _services.ToolWindows.ShowDialogAsync(new ConfirmModel(
                Loc.Format("Rename_UndoPrompt", undo.Count), null, Loc.Get("Rename_Undo")));
            if (!confirmed || _disposed) return;
            var steps = undo.Select(item => new RenameStep(item.To, item.From)).ToArray();
            var operation = new BatchRenameOperation(_services.Providers, steps, _interaction, undo);
            await RunRenameAsync(operation);
            _renameUndo = [];
        }
        finally
        {
            _renameRunning = false;
            if (!_disposed) UpdateAvailability();
        }
    }

    private async Task<IReadOnlyDictionary<VfsPath, IReadOnlySet<string>>> ReadRenameNamesAsync(
        IReadOnlyList<FileEntry> entries)
    {
        var names = new Dictionary<VfsPath, IReadOnlySet<string>>();
        foreach (var folder in entries.Select(entry => entry.Path.Parent!.Value).Distinct())
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var entry in _services.Providers.For(folder).EnumerateAsync(folder, CancellationToken.None)
                               .ConfigureAwait(false))
                set.Add(entry.Name);
            names.Add(folder, set);
        }
        return names;
    }

    private async Task RunRenameAsync(BatchRenameOperation operation)
    {
        _files.StartOperation(operation, runImmediately: false);
        try { await operation.Completion; }
        catch (Exception)
        {
            // FileCommands owns operation errors and still refreshes the panels.
        }
        if (operation.State != OperationState.Completed && operation.NeedsRecovery.Count > 0 && !_disposed)
        {
            await _files.WhenIdleAsync();
            if (!_disposed) await _services.Dialogs.ShowSummaryAsync(operation.NeedsRecovery);
        }
    }
}
