using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Dialogs;

/// <summary>Asks for a name (a new folder). Accepting runs the validator; on an error the dialog stays open.</summary>
public sealed partial class NameDialogViewModel : ObservableObject
{
    private readonly TextValidator? _validate;

    public NameDialogViewModel(string titleKey, string initial, TextValidator? validate)
    {
        Title = Loc.Get(titleKey);
        Text = initial;
        _validate = validate;
    }

    public string Title { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnTextChanged(string value) => Error = null;

    public async Task<string?> AcceptAsync()
    {
        var text = Text;
        if (_validate is not null && await _validate(text) is { } error)
        {
            Error = error;
            return null;
        }
        return text;
    }
}

/// <summary>Asks before deleting: names the item or the count, says whether it goes to the Trash, and for a permanent
/// delete warns it cannot be undone and makes Cancel the default button.</summary>
public sealed class ConfirmDeleteDialogViewModel(int itemCount, string? singleName, bool permanent)
{
    public string Title => Loc.Get(permanent ? "Delete_TitlePermanent" : "Delete_TitleTrash");

    public string Message
    {
        get
        {
            var items = itemCount == 1 && singleName is not null
                ? Loc.Format("Operation_ItemName", singleName)
                : Loc.Count("Count_Items", itemCount);
            return Loc.Format(permanent ? "Delete_AskPermanent" : "Delete_AskTrash", items);
        }
    }

    /// <summary>"This cannot be undone." for a permanent delete; null otherwise.</summary>
    public string? Warning => permanent ? Loc.Get("Delete_CannotUndo") : null;

    public string PrimaryButtonText => Loc.Get(permanent ? "Button_Delete" : "Delete_MoveToTrash");

    public bool CancelIsDefault => permanent;
}

/// <summary>The destination of a file exists: both files' size and date, the choices, and "apply to all".</summary>
public sealed partial class ConflictDialogViewModel(FileEntry source, FileEntry existing) : ObservableObject
{
    /// <summary>The item being copied or moved.</summary>
    public FileEntry Source { get; } = source;

    /// <summary>The item already at the destination.</summary>
    public FileEntry Existing { get; } = existing;

    public string Title => Loc.Get("Conflict_Title");

    public string Message => Loc.Format("Conflict_Message", Existing.Name, Existing.Path.Parent?.Path ?? "/");

    public string SourceDetails => Loc.Format("Conflict_Source", Describe(Source));

    public string ExistingDetails => Loc.Format("Conflict_Existing", Describe(Existing));

    /// <summary>The buttons, in order; the first is the default, the last is Esc.</summary>
    public IReadOnlyList<ConflictAction> Actions { get; } =
    [
        ConflictAction.Overwrite, ConflictAction.OverwriteOlder, ConflictAction.Skip, ConflictAction.Rename,
        ConflictAction.Cancel,
    ];

    /// <summary>The default button: Skip, which changes nothing.</summary>
    public ConflictAction DefaultAction => ConflictAction.Skip;

    [ObservableProperty]
    public partial bool ApplyToAll { get; set; }

    public ConflictDecision Decide(ConflictAction action) => new(action, ApplyToAll && action != ConflictAction.Cancel);

    private static string Describe(FileEntry entry) =>
        Loc.Format("Conflict_Details", entry.IsDirectoryLike ? Loc.Get("Panel_Folder") : SizeFormatter.Format(entry.Size),
            entry.Modified.ToLocalTime().ToString("g", Loc.Culture));
}

/// <summary>An item failed: what happened in words, the system's message, and retry / skip / skip all / abort.</summary>
public sealed class ErrorDialogViewModel(VfsPath item, string message, string? detail, bool connectionLost = false)
{
    public string Title => Loc.Get("OperationError_Title");

    public string Message => message;

    public string? Detail => string.IsNullOrWhiteSpace(detail) ? null : detail;

    public VfsPath Item => item;

    /// <summary>The source lost its connection: retrying connects again first.</summary>
    public bool ConnectionLost => connectionLost;

    /// <summary>The text of an action's button: Retry reads "Reconnect and Retry" when the connection was lost.</summary>
    public string Label(ErrorAction action) =>
        action == ErrorAction.Retry && connectionLost ? Loc.Get("Button_ReconnectAndRetry") : Loc.Enum(action);

    /// <summary>The buttons in order; Retry is the default, Abort is Esc.</summary>
    public IReadOnlyList<ErrorAction> Actions { get; } =
        [ErrorAction.Retry, ErrorAction.Skip, ErrorAction.SkipAll, ErrorAction.Abort];
}

/// <summary>One skipped item in the summary.</summary>
public sealed record SummaryLine(string Path, string Reason, string? Detail);

/// <summary>What an operation left alone, and why.</summary>
public sealed class SummaryDialogViewModel
{
    public SummaryDialogViewModel(IReadOnlyList<SkippedItem> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        Lines = skipped.Select(s => new SummaryLine(s.Path.Path, Loc.Enum(s.Reason), s.Detail)).ToArray();
        Message = Loc.Format("Summary_Message", Loc.Count("Count_Items", skipped.Count));
    }

    public string Title => Loc.Get("Summary_Title");

    public string Message { get; }

    public IReadOnlyList<SummaryLine> Lines { get; }
}

/// <summary>Asks for a mask to mark or unmark rows by.</summary>
public sealed partial class MaskDialogViewModel : ObservableObject
{
    public MaskDialogViewModel(bool select, string initial)
    {
        Title = Loc.Get(select ? "Mask_SelectTitle" : "Mask_UnselectTitle");
        Text = initial;
    }

    public string Title { get; }

    public string Hint => Loc.Get("Mask_Hint");

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnTextChanged(string value) => Error = null;

    /// <summary>The mask, or null when it is blank (<see cref="Error"/> says so).</summary>
    public string? Accept()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            Error = Loc.Get("Mask_Empty");
            return null;
        }
        return Text.Trim();
    }
}
