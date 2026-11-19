using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;

namespace Tandem.App.ViewModels.Dialogs;

/// <summary>The copy/move dialog: where to, verify content, start now. Accepting checks the text with the request's
/// validator; on an error the dialog stays open and shows it.</summary>
public sealed partial class CopyMoveDialogViewModel : ObservableObject
{
    private readonly CopyMoveRequest _request;

    public CopyMoveDialogViewModel(CopyMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        DestinationText = request.Destination;
    }

    public string Title => Loc.Get(_request.IsMove ? "Button_Move" : "Button_Copy");

    /// <summary>"Copy “report.pdf” to:" or "Copy 3 items to:".</summary>
    public string Prompt => _request is { ItemCount: 1, SingleName: { } name }
        ? Loc.Format(_request.IsMove ? "CopyMove_MoveOne" : "CopyMove_CopyOne", name)
        : Loc.Format(_request.IsMove ? "CopyMove_MoveMany" : "CopyMove_CopyMany",
            Loc.Count("Count_Items", _request.ItemCount));

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
    public async Task<CopyMoveAnswer?> AcceptAsync()
    {
        var text = DestinationText;
        if (_request.Validate is { } validate && await validate(text) is { } error)
        {
            Error = error;
            return null;
        }
        return new CopyMoveAnswer(text, VerifyContent, RunImmediately);
    }
}
