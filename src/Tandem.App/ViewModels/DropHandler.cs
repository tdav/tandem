using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>Where a drop at some point of a panel goes: into <paramref name="Directory"/>; the row to highlight is
/// <paramref name="RowIndex"/>, or the whole list when it is null (the panel's own folder).</summary>
public sealed record DropTarget(VfsPath Directory, int? RowIndex);

/// <summary>
/// Drag and drop of files, without the drag itself: what a pointer position targets (<see cref="TargetAt"/>), what a
/// drag from a panel carries (<see cref="DragItems"/>), whether files may be dropped (<see cref="CanDrop(IReadOnlyList{string}, VfsPath)"/>),
/// and the drop, which always ends in the ordinary copy/move dialog of <see cref="FileCommands"/> with the target
/// folder proposed: nothing is copied or moved unless the user accepts it.
/// </summary>
/// <remarks>
/// Dragged items are local paths (from Finder or from a panel on the local disk). A drop is refused when any item is
/// not an absolute local path, when the target cannot be written, when every item already lies in the target folder,
/// when the target is one of the dragged folders or inside one, or while a copy/move dialog is open. UI thread only; no disk access.
/// </remarks>
public sealed class DropHandler(WorkspaceViewModel workspace, FileCommands files)
{
    private readonly WorkspaceViewModel _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    private readonly FileCommands _files = files ?? throw new ArgumentNullException(nameof(files));

    /// <summary>Whether <paramref name="localPaths"/> may be dropped into <paramref name="targetDirectory"/>.</summary>
    public bool CanDrop(IReadOnlyList<string> localPaths, VfsPath targetDirectory) =>
        Sources(localPaths) is { } sources && CanDrop(sources, targetDirectory);

    /// <summary>As <see cref="CanDrop(IReadOnlyList{string}, VfsPath)"/>, onto the panel of <paramref name="side"/>,
    /// which must show a folder without an error.</summary>
    public bool CanDrop(IReadOnlyList<string> localPaths, PanelSide side, VfsPath targetDirectory) =>
        PanelTakesDrops(side) && CanDrop(localPaths, targetDirectory);

    /// <summary>Drops <paramref name="localPaths"/> into <paramref name="targetDirectory"/>: the copy/move dialog
    /// offering both actions, then the operation the user chose. <paramref name="move"/> only pre-selects Move (Cmd
    /// was reported at the drop; a hint, see the remarks). Does nothing when the drop is not allowed. True when the
    /// user accepted and the operation started.</summary>
    /// <remarks>The modifier is only a hint: on macOS, holding Cmd while dragging makes the system offer no
    /// operation Avalonia knows (AppKit filters the source's mask to "generic"), and while another app owns the drag
    /// this app may not see the key at all. So the dialog, not the key, decides between copy and move.</remarks>
    public Task<bool> DropAsync(IReadOnlyList<string> localPaths, VfsPath targetDirectory, bool move) =>
        Sources(localPaths) is { } sources && CanDrop(sources, targetDirectory)
            ? _files.CopyOrMoveAsync(sources, targetDirectory, move, offerBoth: true)
            : Task.FromResult(false);

    /// <summary>As <see cref="DropAsync(IReadOnlyList{string}, VfsPath, bool)"/> onto the panel of
    /// <paramref name="side"/>; once the user accepted, that side becomes the active one.</summary>
    public async Task<bool> DropAsync(IReadOnlyList<string> localPaths, PanelSide side, VfsPath targetDirectory,
        bool move)
    {
        if (!PanelTakesDrops(side) || !await DropAsync(localPaths, targetDirectory, move)) return false;
        _workspace.ActiveSide = side;
        return true;
    }

    /// <summary>
    /// What a drop at row <paramref name="rowIndex"/> of <paramref name="panel"/> targets: a folder row that folder,
    /// the ".." row the parent folder, a file row or anywhere else (a negative or too large index) the panel's own
    /// folder. Null when the panel shows no folder, or for ".." at the root.
    /// </summary>
    public static DropTarget? TargetAt(PanelViewModel panel, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var current = panel.CurrentPath;
        if (current == default) return null;
        if (rowIndex < 0 || rowIndex >= panel.Rows.Count) return new DropTarget(current, null);
        var row = panel.Rows[rowIndex];
        if (row.IsParentLink) return current.Parent is { } parent ? new DropTarget(parent, rowIndex) : null;
        return row is { IsDirectoryLike: true, Entry: { } entry }
            ? new DropTarget(entry.Path, rowIndex)
            : new DropTarget(current, null);
    }

    /// <summary>
    /// The items (on the local disk) a drag that starts on row <paramref name="rowIndex"/> carries: every marked item
    /// when that row is marked, otherwise just that row; nothing for "..", for no row, or when any of the items is not
    /// on the local disk (the items' own provider counts, not the panel's folder's). The marks are not changed.
    /// </summary>
    public static IReadOnlyList<FileEntry> DragItems(PanelViewModel panel, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (panel.CurrentPath == default || rowIndex < 0 || rowIndex >= panel.Rows.Count) return [];
        var row = panel.Rows[rowIndex];
        if (row is not { IsParentLink: false, Entry: { } entry }) return [];
        var items = row.IsSelected ? panel.GetOperationTargets() : [entry];
        return items.All(e => e.Path.ProviderId == VfsPath.LocalProviderId) ? items : [];
    }

    private bool PanelTakesDrops(PanelSide side) =>
        _workspace.IsLoaded &&
        (side == PanelSide.Left ? _workspace.Left : _workspace.Right).Active.Panel is { Error: null } panel &&
        panel.CurrentPath != default;

    private bool CanDrop(IReadOnlyList<VfsPath> sources, VfsPath target) =>
        !_files.IsAskingCopyOrMove &&
        _files.CanWriteTo(target) &&
        !sources.All(s => s.Parent == target) &&
        !sources.Any(s => target.IsSameOrDescendantOf(s));

    /// <summary>The paths as local <see cref="VfsPath"/>s; null when there are none or any is not an absolute local
    /// path.</summary>
    private static IReadOnlyList<VfsPath>? Sources(IReadOnlyList<string> localPaths)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        if (localPaths.Count == 0) return null;
        var sources = new VfsPath[localPaths.Count];
        for (var i = 0; i < localPaths.Count; i++)
        {
            var path = localPaths[i];
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.Contains('\0')) return null;
            try
            {
                sources[i] = VfsPath.Local(path);
            }
            catch (ArgumentException)
            {
                return null; // e.g. ".." above the root
            }
            if (sources[i].IsRoot) return null;
        }
        return sources;
    }
}
