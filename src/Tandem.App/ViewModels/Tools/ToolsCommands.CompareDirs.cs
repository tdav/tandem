using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.Core.Search;
using Tandem.Core.Tools.Compare;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Tools;

public sealed partial class ToolsCommands
{
    partial void RegisterCompareDirs() => On(CommandIds.ToolsCompareDirs, CompareDirsAsync, CanCompareFolders);

    private bool CanCompareFolders() =>
        Active is { Error: null, HasForeignEntries: false } active &&
        Passive is { Error: null, HasForeignEntries: false } passive &&
        IsFolderListing(active) && IsFolderListing(passive);

    private bool IsFolderListing(PanelViewModel panel) =>
        panel.CurrentPath.ProviderId is { } id && _services.Providers.TryGet(id, out var provider) &&
        provider is not SearchResultsProvider;

    private async Task CompareDirsAsync()
    {
        if (!CanCompareFolders()) return;
        var left = _workspace.Left.Active.Panel;
        var right = _workspace.Right.Active.Panel;
        var leftRows = left.Rows.ToArray();
        var rightRows = right.Rows.ToArray();
        var noticePanel = Active!;
        var difference = await Task.Run(() => DirectoryComparer.Compare(Entries(leftRows), Entries(rightRows),
            DirectoryComparer.DefaultTolerance));
        if (!ReferenceEquals(left, _workspace.Left.Active.Panel) || !ReferenceEquals(right, _workspace.Right.Active.Panel) ||
            !leftRows.SequenceEqual(left.Rows) || !rightRows.SequenceEqual(right.Rows)) return;

        left.SelectWhere(entry => difference.MarkLeft.Contains(entry.Name));
        right.SelectWhere(entry => difference.MarkRight.Contains(entry.Name));
        noticePanel.ShowNotice(difference.MarkLeft.Count == 0 && difference.MarkRight.Count == 0
            ? Loc.Get("CompareDirs_Equal")
            : Loc.Format("CompareDirs_Marked", difference.MarkLeft.Count, difference.MarkRight.Count));
    }

    private static FileEntry[] Entries(EntryRowViewModel[] rows) => rows.Select(row => row.Entry).OfType<FileEntry>().ToArray();
}
