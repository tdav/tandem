using System.Globalization;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

// Marking rows, the status line, the items an operation applies to, and jump-to-name typing.
//
// The ".." row is never marked and never counted. Selection totals are kept as running sums: every change of a mark
// goes through Account, whether it comes from the panel's own commands or from a row's IsSelected (the view's
// binding). Bulk commands change the marks silently and raise the totals' notifications once. A new listing
// recomputes the totals once; a refresh carries the marks over by path, a navigation starts with none.
public sealed partial class PanelViewModel
{
    private int _selectedCount;
    private long _selectedBytes;
    private int _totalCount;
    private long _totalBytes;
    private string _quickSearchText = "";
    private Action<EntryRowViewModel>? _onRowSelectionChanged;

    /// <summary>Marked rows.</summary>
    public int SelectedCount => _selectedCount;

    /// <summary>The size of the marked files; folders count as 0.</summary>
    public long SelectedBytes => _selectedBytes;

    /// <summary>Rows other than "..".</summary>
    public int TotalCount => _totalCount;

    /// <summary>The size of all files listed; folders count as 0.</summary>
    public long TotalBytes => _totalBytes;

    /// <summary>Some rows are entries of another folder than the one shown (<see cref="EntryRowViewModel.IsForeign"/>),
    /// e.g. search results.</summary>
    public bool HasForeignEntries { get; private set; }

    /// <summary>"Selected {count} of {total}, {size}" in the current language.</summary>
    public string StatusText =>
        Loc.Format("Panel_Status", _selectedCount, _totalCount, SizeFormatter.Format(_selectedBytes));

    /// <summary>The text typed so far for jump-to-name; "" when no quick search is going on.</summary>
    public string QuickSearchText
    {
        get => _quickSearchText;
        private set => SetProperty(ref _quickSearchText, value);
    }

    // ---- Marking -----------------------------------------------------------------------------------------

    /// <summary>Insert: toggles the mark of the row under the cursor (not "..") and moves the cursor down one row.</summary>
    public void ToggleSelectAndMoveDown()
    {
        if (CursorRow is not { } row) return;
        if (ChangeSelection(row, !row.IsSelected)) RaiseSelectionChanged();
        EndQuickSearch();
        SetCursor(_cursorIndex + 1);
    }

    /// <summary>Moves the cursor by <paramref name="delta"/> rows, clamped at the ends. With
    /// <paramref name="extendSelection"/> (Shift) the rows passed over become marked.</summary>
    /// <remarks>
    /// The rule: the rows from the cursor's row (included) toward the requested target row (excluded) become marked,
    /// as far as the listing goes; ".." is skipped and nothing is unmarked. So Shift+Down marks the row the cursor
    /// leaves, not the row it lands on, and Shift+Up likewise marks the row left behind. When the request goes past an
    /// end, the cursor stops at the end row and that row is marked too, since the move passed over it: Shift+Down on
    /// the last row marks it, and Shift+End marks through the last row.
    /// </remarks>
    public void MoveCursor(int delta, bool extendSelection)
    {
        EndQuickSearch();
        if (_rows.Count == 0) return;
        var from = _cursorIndex;
        var target = (long)from + delta;
        if (extendSelection && delta != 0)
        {
            var step = Math.Sign(delta);
            var changed = false;
            for (var i = (long)from; i != target && i >= 0 && i < _rows.Count; i += step)
                changed |= ChangeSelection(_rows[(int)i], true);
            if (changed) RaiseSelectionChanged();
        }
        SetCursor((int)Math.Clamp(target, 0, _rows.Count - 1));
    }

    /// <summary>Marks the rows, folders included, whose names match <paramref name="mask"/> (see
    /// <see cref="FileMask"/>); other marks stay.</summary>
    public void SelectByMask(string mask)
    {
        var fileMask = new FileMask(mask);
        ApplyToAll(row => row.IsSelected || fileMask.IsMatch(row.Name));
    }

    /// <summary>Unmarks the rows whose names match <paramref name="mask"/>; other marks stay.</summary>
    public void UnselectByMask(string mask)
    {
        var fileMask = new FileMask(mask);
        ApplyToAll(row => row.IsSelected && !fileMask.IsMatch(row.Name));
    }

    public void SelectAll() => ApplyToAll(static _ => true);

    public void InvertSelection() => ApplyToAll(static row => !row.IsSelected);

    public void ClearSelection() => ApplyToAll(static _ => false);

    /// <summary>What a file operation applies to: the marked entries in row order, otherwise the entry under the
    /// cursor; empty when nothing is marked and the cursor is on ".." or there are no rows.</summary>
    public IReadOnlyList<FileEntry> GetOperationTargets()
    {
        if (_selectedCount > 0)
        {
            var targets = new List<FileEntry>(_selectedCount);
            foreach (var row in _rows.Items)
                if (row.IsSelected)
                    targets.Add(row.Entry!);
            return targets;
        }
        return CursorRow is { IsParentLink: false, Entry: { } entry } ? [entry] : [];
    }

    private void ApplyToAll(Func<EntryRowViewModel, bool> selected)
    {
        var changed = false;
        foreach (var row in _rows.Items)
            if (!row.IsParentLink)
                changed |= ChangeSelection(row, selected(row));
        if (changed) RaiseSelectionChanged();
    }

    /// <summary>Changes one mark without notifying the totals; the caller raises <see cref="RaiseSelectionChanged"/>
    /// once. True when the mark changed.</summary>
    private bool ChangeSelection(EntryRowViewModel row, bool selected)
    {
        if (!row.SetSelectedSilently(selected)) return false;
        Account(row);
        return true;
    }

    /// <summary>A row's mark has just flipped: adds it to the totals or takes it out. The only place the running
    /// selection totals change, apart from recounting a new listing.</summary>
    private void Account(EntryRowViewModel row)
    {
        var sign = row.IsSelected ? 1 : -1;
        _selectedCount += sign;
        _selectedBytes += sign * row.SelectableBytes;
    }

    /// <summary>A row's <see cref="EntryRowViewModel.IsSelected"/> was set from outside, e.g. by the view.</summary>
    private void OnRowSelectionChanged(EntryRowViewModel row)
    {
        Account(row);
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Before <paramref name="rows"/> replace the shown rows: the old rows stop reporting their marks, the new
    /// ones start, and the totals are counted afresh (marks carried over by a refresh included).</summary>
    private void AttachRows(IReadOnlyList<EntryRowViewModel> rows)
    {
        InlineRenameViewModel? editor = null;
        foreach (var row in _rows.Items)
        {
            row.SelectionChanged = null;
            editor ??= row.Rename;
        }
        if (editor is not null) CarryRenameOver(editor, rows);

        var handler = _onRowSelectionChanged ??= OnRowSelectionChanged;
        int total = 0, selected = 0;
        long totalBytes = 0, selectedBytes = 0;
        var foreign = false;
        foreach (var row in rows)
        {
            row.SelectionChanged = handler;
            if (row.IsParentLink) continue;
            foreign |= row.IsForeign;
            var bytes = row.SelectableBytes;
            total++;
            totalBytes += bytes;
            if (!row.IsSelected) continue;
            selected++;
            selectedBytes += bytes;
        }
        _totalCount = total;
        _totalBytes = totalBytes;
        _selectedCount = selected;
        _selectedBytes = selectedBytes;
        HasForeignEntries = foreign;
    }

    /// <summary>An inline rename is open while the rows are replaced (a refresh): it moves to the new row of the same
    /// item, or is cancelled when the item is gone.</summary>
    private static void CarryRenameOver(InlineRenameViewModel editor, IReadOnlyList<EntryRowViewModel> rows)
    {
        var path = editor.Row.Entry!.Path;
        foreach (var row in rows)
        {
            if (row.IsParentLink || row.Entry!.Path != path) continue;
            editor.MoveTo(row);
            return;
        }
        editor.Cancel();
    }

    private void RaiseListingTotalsChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TotalBytes));
        RaiseSelectionChanged();
    }

    // ---- Quick search ------------------------------------------------------------------------------------

    /// <summary>Adds <paramref name="text"/> (one character, or several from an input method or a paste) to the quick
    /// search and moves the cursor to the first row whose name starts with the whole search text. Names and text are
    /// compared as the sorter folds them: case-insensitive, both in form C. ".." never matches. When no row matches,
    /// nothing of <paramref name="text"/> is taken, the cursor stays and the result is false.</summary>
    public bool QuickSearchInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return false;
        var candidate = _quickSearchText + text;
        var index = FindFirstPrefixMatch(candidate);
        if (index < 0) return false;
        QuickSearchText = candidate;
        SetCursor(index);
        return true;
    }

    /// <summary>Removes the last character (a whole emoji or accented letter) from the quick search and moves the
    /// cursor to the first match of what is left. When nothing is left the search ends and the cursor stays.</summary>
    public void QuickSearchBackspace()
    {
        var text = _quickSearchText;
        if (text.Length == 0) return;
        var shorter = text[..LastTextElementStart(text)];
        QuickSearchText = shorter;
        if (shorter.Length == 0) return;
        var index = FindFirstPrefixMatch(shorter);
        if (index >= 0) SetCursor(index);
    }

    /// <summary>Esc: ends the quick search; the cursor stays.</summary>
    public void QuickSearchCancel() => EndQuickSearch();

    private void EndQuickSearch() => QuickSearchText = "";

    private int FindFirstPrefixMatch(string prefix)
    {
        var folded = EntrySorter.Fold(prefix);
        var rows = _rows.Items;
        for (var i = 0; i < rows.Count; i++)
            if (!rows[i].IsParentLink && rows[i].FoldedName.StartsWith(folded, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static int LastTextElementStart(string text)
    {
        var start = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) start = elements.ElementIndex;
        return start;
    }
}
