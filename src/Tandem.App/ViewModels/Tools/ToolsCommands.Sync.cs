using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;
using Tandem.Core.Tools.Sync;

namespace Tandem.App.ViewModels.Tools;

public sealed partial class ToolsCommands
{
    private SyncViewModel? _syncWindow;

    partial void RegisterSync()
    {
        On(CommandIds.ToolsSyncDirs, ShowSyncAsync, CanSynchronizeFolders);
        OnDispose(() => _syncWindow?.Dispose());
    }

    private bool CanSynchronizeFolders() => CanCompareFolders() &&
        !Active!.CurrentPath.IsSameOrDescendantOf(Passive!.CurrentPath) &&
        !Passive.CurrentPath.IsSameOrDescendantOf(Active.CurrentPath);

    private Task ShowSyncAsync()
    {
        if (_syncWindow is { IsDisposed: false } open)
        {
            _services.ToolWindows.Show(open);
            return Task.CompletedTask;
        }
        if (!CanSynchronizeFolders()) return Task.CompletedTask;
        var left = _workspace.Left.Active.Panel.CurrentPath;
        var right = _workspace.Right.Active.Panel.CurrentPath;
        var model = new SyncViewModel(_services.Providers, left, right, Run, Confirm, _services.Trash,
            _services.NativeCopy, _interaction);
        _syncWindow = model;
        _services.ToolWindows.Show(model);
        return Task.CompletedTask;

        async Task<OperationResult?> Run(SyncOperation operation)
        {
            _files.StartOperation(operation, runImmediately: true, alsoChanged: [left, right]);
            try { return await operation.Completion; }
            catch (Exception) when (operation.State == OperationState.Failed) { return null; }
        }

        Task<bool> Confirm(SyncConfirmation confirmation)
        {
            var totals = confirmation.Totals;
            var deletes = totals.DeleteLeft > 0 || totals.DeleteRight > 0;
            var detail = deletes
                ? Loc.Format("Sync_DeleteConfirmation", totals.DeleteLeft,
                    Loc.Get(confirmation.LeftToTrash ? "Sync_ToTrash" : "Sync_Permanent"),
                    totals.DeleteRight, Loc.Get(confirmation.RightToTrash ? "Sync_ToTrash" : "Sync_Permanent"))
                : null;
            return _services.ToolWindows.ShowDialogAsync(
                new ConfirmModel(totals.Display, detail, Loc.Get("Sync_Run"), cancelIsDefault: deletes),
                ownerModel: _syncWindow);
        }
    }
}
