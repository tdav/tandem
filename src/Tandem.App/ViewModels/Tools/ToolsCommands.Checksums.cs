using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.Core.Operations;
using Tandem.Core.Tools.Checksums;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Tools;

public sealed partial class ToolsCommands
{
    partial void RegisterChecksums()
    {
        On(CommandIds.ToolsChecksumCreate, CreateChecksumAsync, CanCreateChecksum);
        On(CommandIds.ToolsChecksumVerify, VerifyChecksumAsync, CanVerifyChecksum);
    }

    private bool CanCreateChecksum() =>
        Active is { Error: null, HasForeignEntries: false } panel && IsFolderListing(panel) &&
        (panel.SelectedCount > 0 || panel.CursorRow?.Entry is not null) &&
        _services.Providers.TryGet(panel.CurrentPath.ProviderId, out var provider) &&
        provider.Capabilities.HasFlag(ProviderCapabilities.Write);

    private bool CanVerifyChecksum() => Active?.CursorRow?.Entry is { Kind: EntryKind.File } entry &&
        Checksum.FromFileName(entry.Name) is not null;

    private async Task CreateChecksumAsync()
    {
        if (!CanCreateChecksum() || Active is not { } panel) return;
        var folder = panel.CurrentPath;
        var targets = panel.GetOperationTargets();
        var stem = targets.Count == 1 ? targets[0].Name : folder.Name;
        if (stem.Length == 0) stem = Loc.Get("Checksum_DefaultName");
        var model = new ChecksumCreateViewModel(stem);
        if (await _services.ToolWindows.ShowDialogAsync(model) is not { } request) return;
        var checksum = folder.Combine(request.Name);
        var operation = new ChecksumCreateOperation(_services.Providers, targets.Select(entry => entry.Path).ToArray(),
            checksum, request.Algorithm, _interaction);
        if (!await RunChecksumAsync(operation) || panel.CurrentPath != folder) return;
        for (var index = 0; index < panel.Rows.Count; index++)
        {
            if (panel.Rows[index].Entry?.Path != checksum) continue;
            panel.CursorIndex = index;
            break;
        }
    }

    private async Task VerifyChecksumAsync()
    {
        if (!CanVerifyChecksum() || Active?.CursorRow?.Entry is not { } entry) return;
        var operation = new ChecksumVerifyOperation(_services.Providers, entry.Path, _interaction);
        if (await RunChecksumAsync(operation))
            _services.ToolWindows.Show(new ChecksumResultsViewModel(operation.Results, operation.UnreadableLineNumbers));
    }

    private async Task<bool> RunChecksumAsync(FileOperation operation)
    {
        await _files.StartOperationAndWaitAsync(operation);
        try { return (await operation.Completion).State == OperationState.Completed; }
        catch (Exception) when (operation.State == OperationState.Failed) { return false; }
    }
}
