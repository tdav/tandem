using Tandem.Core.Vfs.Local;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;

namespace Tandem.App.ViewModels;

/// <summary>
/// The name editor inside a row (Shift+F6, F2). It opens with the name's part before the extension selected (the
/// whole name for folders and names like ".gitignore"). Enter commits, Esc and losing the focus cancel. An unchanged
/// name closes without doing anything; an empty name, ".", ".." or a name with "/" is refused with a message and the
/// editor stays open. UI thread only.
/// </summary>
public sealed partial class InlineRenameViewModel : ObservableObject
{
    private readonly Func<string, Task> _rename;
    private readonly Action<InlineRenameViewModel> _closed;
    private readonly Action<Exception>? _onFailure;

    /// <param name="rename">Starts the rename to the given name.</param>
    /// <param name="closed">Called once the editor has closed, whatever the reason.</param>
    /// <param name="onFailure">Hears an exception from <paramref name="rename"/>; the editor never throws it.</param>
    public InlineRenameViewModel(EntryRowViewModel row, Func<string, Task> rename,
        Action<InlineRenameViewModel> closed, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(row.Entry);
        Row = row;
        _rename = rename;
        _closed = closed;
        _onFailure = onFailure;
        OriginalName = row.Name;
        Text = OriginalName;
        var extension = row.Entry.Extension;
        SelectionLength = extension.Length > 0 ? OriginalName.Length - extension.Length - 1 : OriginalName.Length;
        SelectionEnd = SelectionLength;
    }

    /// <summary>The row being renamed; a refresh that replaces the rows moves the editor to the new row of the same
    /// item.</summary>
    public EntryRowViewModel Row { get; private set; }

    /// <summary>The selection (and caret, at its end) in the editor, kept so a new editor control for a replaced
    /// row continues where the user was. Starts as the name part.</summary>
    public int SelectionStart { get; set; }

    public int SelectionEnd { get; set; }

    public string OriginalName { get; }

    /// <summary>How many characters, from the start, are selected when the editor opens.</summary>
    public int SelectionLength { get; }

    public bool IsOpen { get; private set; } = true;

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnTextChanged(string value) => Error = null;

    /// <summary>Enter. True when the editor closed (renamed, or the name was unchanged); false when the name is
    /// refused and the editor stays open with <see cref="Error"/>.</summary>
    public async Task<bool> CommitAsync()
    {
        if (!IsOpen) return true;
        var name = Text;
        if (name == OriginalName)
        {
            Close();
            return true;
        }
        if (Validate(name) is { } error)
        {
            Error = error;
            return false;
        }
        Close();
        try
        {
            await _rename(name);
        }
        catch (Exception e)
        {
            _onFailure?.Invoke(e);
        }
        return true;
    }

    /// <summary>The panel's rows were replaced: carries the open editor over to <paramref name="row"/>, the new row
    /// of the same item, with the text and selection kept.</summary>
    internal void MoveTo(EntryRowViewModel row)
    {
        if (!IsOpen || ReferenceEquals(row, Row)) return;
        // Row first: clearing the old row's editor makes its control lose the focus, and the control must then
        // see the session has moved on, not that the user left it.
        var old = Row;
        Row = row;
        if (old.Rename == this) old.Rename = null;
        row.Rename = this;
    }

    /// <summary>Esc, or the editor lost the focus.</summary>
    public void Cancel()
    {
        if (IsOpen) Close();
    }

    /// <summary>Why <paramref name="name"/> cannot be a file name; null when it can.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Loc.Get("Rename_Empty");
        if (name is "." or ".." || name.Contains('/') || name.Contains('\0')) return Loc.Format("Rename_Invalid", name);
        // On Windows a name the disk cannot hold ("a:b", "a\\b", "CON") is refused here, not by a puzzling system
        // error (or, for '\\', a move into another folder).
        if (LocalPath.Current == LocalPathStyle.Windows && !LocalPath.IsWindowsName(name))
            return Loc.Format("Rename_Invalid", name);
        return null;
    }

    private void Close()
    {
        IsOpen = false;
        if (Row.Rename == this) Row.Rename = null;
        _closed(this);
    }
}
