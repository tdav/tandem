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

    internal InlineRenameViewModel(EntryRowViewModel row, Func<string, Task> rename,
        Action<InlineRenameViewModel> closed)
    {
        ArgumentNullException.ThrowIfNull(row.Entry);
        Row = row;
        _rename = rename;
        _closed = closed;
        OriginalName = row.Name;
        Text = OriginalName;
        var extension = row.Entry.Extension;
        SelectionLength = extension.Length > 0 ? OriginalName.Length - extension.Length - 1 : OriginalName.Length;
    }

    public EntryRowViewModel Row { get; }

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
        await _rename(name);
        return true;
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
        return null;
    }

    private void Close()
    {
        IsOpen = false;
        if (Row.Rename == this) Row.Rename = null;
        _closed(this);
    }
}
