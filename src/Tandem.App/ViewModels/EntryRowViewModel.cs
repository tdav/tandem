using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// One row of a panel: a directory entry, or the ".." link to the parent folder. Rows are built off the UI thread in
/// bulk, so the display texts are formatted on first read and remembered, not in the constructor.
/// </summary>
public sealed partial class EntryRowViewModel : ObservableObject
{
    private const string ParentLinkName = "..";

    private string? _name;
    private string? _extension;
    private string? _sizeText;
    private string? _dateText;
    private string? _modeText;
    private string? _foldedName;
    private bool _isSelected;
    private InlineRenameViewModel? _rename;

    public EntryRowViewModel(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
    }

    private EntryRowViewModel()
    {
        IsParentLink = true;
        _name = ParentLinkName;
        _extension = "";
        _sizeText = "";
        _dateText = "";
        _modeText = "";
    }

    /// <summary>A row for the ".." link.</summary>
    public static EntryRowViewModel CreateParentLink() => new();

    /// <summary>The entry; null for the ".." row.</summary>
    public FileEntry? Entry { get; }

    public bool IsParentLink { get; }

    /// <summary>A directory, a link to a directory, or "..": opening the row changes the folder.</summary>
    public bool IsDirectoryLike => IsParentLink || Entry!.IsDirectoryLike;

    /// <summary>The full name, extension included; a link shows its own name.</summary>
    public string Name => _name ??= Entry!.Name;

    public string Extension => _extension ??= Entry!.Extension;

    /// <summary>The size for files; the word "Folder" for folders and links to folders; empty for "..".</summary>
    public string SizeText => _sizeText ??= IsDirectoryLike ? Loc.Get("Panel_Folder") : SizeFormatter.Format(Entry!.Size);

    /// <summary>The modification time as short date and time in the current language; empty for "..".</summary>
    public string DateText => _dateText ??= Entry!.Modified.ToLocalTime().ToString("g", Loc.Culture);

    /// <summary>The permissions as nine characters, e.g. "rwxr-xr-x"; empty when the entry has no mode.</summary>
    public string ModeText => _modeText ??= Entry!.Mode is { } mode ? FormatMode(mode) : "";

    /// <summary>Whether the row is marked. The ".." row refuses to be marked: setting true leaves it false. A change
    /// made here (e.g. by the view's binding) is reported to the panel listing the row, which keeps its totals.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetSelectedSilently(value)) SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>The inline name editor open on this row (Shift+F6); null when the row shows its name.</summary>
    public InlineRenameViewModel? Rename
    {
        get => _rename;
        internal set
        {
            if (SetProperty(ref _rename, value)) OnPropertyChanged(nameof(IsRenaming));
        }
    }

    public bool IsRenaming => _rename is not null;

    /// <summary>The panel whose rows include this one hears every change made through <see cref="IsSelected"/>;
    /// null while the row is not shown. UI thread only.</summary>
    internal Action<EntryRowViewModel>? SelectionChanged { get; set; }

    /// <summary>Changes the mark without reporting to the panel, for the panel's own bulk changes; still raises
    /// <see cref="ObservableObject.PropertyChanged"/> for the view. True when the mark changed.</summary>
    internal bool SetSelectedSilently(bool value)
    {
        if (value && IsParentLink)
        {
            OnPropertyChanged(nameof(IsSelected)); // a binding that pushed true reads false back
            return false;
        }
        if (_isSelected == value) return false;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
        return true;
    }

    /// <summary>The bytes the row adds to a selection's size: the size of a file, 0 for anything directory-like.</summary>
    internal long SelectableBytes => IsDirectoryLike ? 0 : Entry!.Size;

    /// <summary><see cref="Name"/> folded as the sorter folds it, for matching typed text.</summary>
    internal string FoldedName => _foldedName ??= EntrySorter.Fold(Name);

    private static string FormatMode(UnixFileMode mode) => string.Create(9, mode, static (chars, m) =>
    {
        chars[0] = Has(m, UnixFileMode.UserRead) ? 'r' : '-';
        chars[1] = Has(m, UnixFileMode.UserWrite) ? 'w' : '-';
        chars[2] = Execute(m, UnixFileMode.UserExecute, UnixFileMode.SetUser, 's');
        chars[3] = Has(m, UnixFileMode.GroupRead) ? 'r' : '-';
        chars[4] = Has(m, UnixFileMode.GroupWrite) ? 'w' : '-';
        chars[5] = Execute(m, UnixFileMode.GroupExecute, UnixFileMode.SetGroup, 's');
        chars[6] = Has(m, UnixFileMode.OtherRead) ? 'r' : '-';
        chars[7] = Has(m, UnixFileMode.OtherWrite) ? 'w' : '-';
        chars[8] = Execute(m, UnixFileMode.OtherExecute, UnixFileMode.StickyBit, 't');
    });

    private static bool Has(UnixFileMode mode, UnixFileMode bit) => (mode & bit) != 0;

    // As ls shows it: the special bit replaces the execute character, lower case when execute is also set.
    private static char Execute(UnixFileMode mode, UnixFileMode execute, UnixFileMode special, char specialChar) =>
        (Has(mode, execute), Has(mode, special)) switch
        {
            (true, true) => specialChar,
            (false, true) => char.ToUpperInvariant(specialChar),
            (true, false) => 'x',
            _ => '-',
        };
}
