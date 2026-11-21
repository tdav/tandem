using Tandem.App.Commands;
using Tandem.Core.Tools;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Tools;

public sealed partial class ToolsCommands
{
    partial void RegisterCompareFiles()
    {
        On(CommandIds.ToolsCompareFiles, CompareFilesAsync, () => FilePair() is not null);
        _services.Dialogs.CompareFiles = (left, right, owner) =>
            _services.ToolWindows.ShowDialogAsync(new FileCompareViewModel(_services.Providers, left.Path,
                right.Path, CancellationToken.None), owner);
        OnDispose(() => _services.Dialogs.CompareFiles = null);
    }

    private Task CompareFilesAsync()
    {
        if (FilePair() is { } pair)
            _services.ToolWindows.Show(new FileCompareViewModel(_services.Providers, pair.Left, pair.Right,
                CancellationToken.None));
        return Task.CompletedTask;
    }

    private (VfsPath Left, VfsPath Right)? FilePair()
    {
        if (Active is not { Error: null } active) return null;
        if (active.SelectedCount >= 2)
        {
            var files = active.Rows.Where(row => row.IsSelected && row.Entry?.Kind == EntryKind.File).Take(3).ToArray();
            if (files.Length == 2) return (files[0].Entry!.Path, files[1].Entry!.Path);
        }

        if (active.CursorRow?.Entry is not { Kind: EntryKind.File } source ||
            Passive is not { Error: null } passive) return null;
        var key = NameKey.Of(source.Name);
        var match = passive.Rows.FirstOrDefault(row => !row.IsForeign && row.Entry is { Kind: EntryKind.File } entry &&
            string.Equals(entry.Name, source.Name, StringComparison.Ordinal) && entry.Path != source.Path);
        match ??= passive.Rows.FirstOrDefault(row => !row.IsForeign && row.Entry is { Kind: EntryKind.File } entry &&
            NameKey.Of(entry.Name) == key && entry.Path != source.Path);
        return (match ?? passive.CursorRow)?.Entry is { Kind: EntryKind.File } existing && existing.Path != source.Path
            ? (source.Path, existing.Path) : null;
    }
}
