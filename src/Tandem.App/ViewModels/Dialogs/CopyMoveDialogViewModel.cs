using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;

namespace Tandem.App.ViewModels.Dialogs;

/// <summary>The copy/move dialog: where to, verify content, start now. Accepting checks the text with the request's
/// validator; on an error the dialog stays open and shows it. For a drop (<see cref="OffersBoth"/>) it offers both
/// Copy and Move, the pre-selected one being the default (Enter) button.</summary>
public sealed partial class CopyMoveDialogViewModel : ObservableObject
{
    private readonly CopyMoveRequest _request;

    public CopyMoveDialogViewModel(CopyMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        DestinationText = request.Destination;
    }

    /// <summary>Whether the dialog offers both actions (a drop) instead of the one of F5/F6.</summary>
    public bool OffersBoth => _request.OffersBoth;

    /// <summary>With both actions offered: Move is pre-selected (Cmd was reported held at the drop).</summary>
    public bool MoveIsDefault => _request.IsMove;

    public string Title => Loc.Get(OffersBoth ? "CopyMove_DropTitle" : _request.IsMove ? "Button_Move" : "Button_Copy");

    /// <summary>"Copy “report.pdf” to:", "Copy 3 items to:", or for a drop "Copy or move …".</summary>
    public string Prompt
    {
        get
        {
            var (one, many) = OffersBoth ? ("CopyMove_DropOne", "CopyMove_DropMany")
                : _request.IsMove ? ("CopyMove_MoveOne", "CopyMove_MoveMany")
                : ("CopyMove_CopyOne", "CopyMove_CopyMany");
            return _request is { ItemCount: 1, SingleName: { } name }
                ? Loc.Format(one, name)
                : Loc.Format(many, Loc.Count("Count_Items", _request.ItemCount));
        }
    }

    public string PrimaryButtonText => Title;

    [ObservableProperty]
    public partial string DestinationText { get; set; }

    [ObservableProperty]
    public partial bool VerifyContent { get; set; }

    [ObservableProperty]
    public partial bool RunImmediately { get; set; }

    /// <summary>Why the typed text was refused; cleared when it is edited.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnDestinationTextChanged(string value) => Error = null;

    /// <summary>The answer, or null while the text is refused (<see cref="Error"/> says why).</summary>
    /// <param name="move">The action chosen when both are offered; null: the pre-selected one. Ignored for F5/F6,
    /// whose answer is always their own action.</param>
    public async Task<CopyMoveAnswer?> AcceptAsync(bool? move = null)
    {
        var text = DestinationText;
        if (_request.Validate is { } validate && await validate(text) is { } error)
        {
            Error = error;
            return null;
        }
        return new CopyMoveAnswer(text, VerifyContent, RunImmediately,
            OffersBoth ? move ?? _request.IsMove : _request.IsMove);
    }
}
