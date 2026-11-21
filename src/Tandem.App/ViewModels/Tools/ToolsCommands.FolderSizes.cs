using Tandem.App.Commands;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Tools;

public sealed partial class ToolsCommands
{
    partial void RegisterFolderSizes()
    {
        On(CommandIds.ToolsFolderSize, () => Active is { } panel
                ? panel.ComputeFolderSizeAsync(panel.CursorIndex) : Task.CompletedTask,
            () => Active is { IsSizing: false, CursorRow.Entry.Kind: EntryKind.Directory });
        On(CommandIds.ToolsFolderSizeAll, () => Active?.ComputeAllFolderSizesAsync() ?? Task.CompletedTask,
            () => Active is { IsSizing: false, HasFolders: true });
    }
}
