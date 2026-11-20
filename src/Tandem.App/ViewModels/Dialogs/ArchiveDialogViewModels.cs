using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Archives;

namespace Tandem.App.ViewModels.Dialogs;

/// <summary>The pack dialog: the archive file, its format, start now. Choosing another format puts that format's
/// extension (<see cref="ArchiveFormats.DefaultExtension"/>) in place of the old one at the end of the text. Accepting
/// checks the text with the request's validator; on an error the dialog stays open and shows it.</summary>
public sealed partial class PackDialogViewModel : ObservableObject
{
    private readonly PackRequest _request;

    public PackDialogViewModel(PackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Formats.Count == 0) throw new ArgumentException("No format to offer.", nameof(request));
        _request = request;
        DestinationText = request.Destination;
        Format = request.Formats[0];
    }

    public string Title => Loc.Get("Archive_PackTitle");

    /// <summary>"Pack “docs” into the archive:" or "Pack 3 items into the archive:".</summary>
    public string Prompt => _request is { ItemCount: 1, SingleName: { } name }
        ? Loc.Format("Archive_PackOne", name)
        : Loc.Format("Archive_PackMany", Loc.Count("Count_Items", _request.ItemCount));

    public IReadOnlyList<ArchiveFormat> Formats => _request.Formats;

    /// <summary>What the format list shows for a format: its extension.</summary>
    public static string FormatLabel(ArchiveFormat format) => "." + ArchiveFormats.DefaultExtension(format);

    [ObservableProperty]
    public partial string DestinationText { get; set; }

    [ObservableProperty]
    public partial ArchiveFormat Format { get; set; }

    [ObservableProperty]
    public partial bool RunImmediately { get; set; }

    /// <summary>Why the typed text was refused; cleared when it is edited.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnDestinationTextChanged(string value) => Error = null;

    partial void OnFormatChanged(ArchiveFormat oldValue, ArchiveFormat newValue)
    {
        var text = DestinationText;
        var old = "." + ArchiveFormats.DefaultExtension(oldValue);
        if (text.EndsWith(old, StringComparison.OrdinalIgnoreCase)) text = text[..^old.Length];
        DestinationText = text + "." + ArchiveFormats.DefaultExtension(newValue);
    }

    /// <summary>The answer, or null while the text is refused (<see cref="Error"/> says why).</summary>
    public async Task<PackAnswer?> AcceptAsync()
    {
        var text = DestinationText;
        if (_request.Validate is { } validate && await validate(text) is { } error)
        {
            Error = error;
            return null;
        }
        return new PackAnswer(text, Format, RunImmediately);
    }
}

/// <summary>The unpack dialog: the destination folder, each archive into its own folder, start now.</summary>
public sealed partial class UnpackDialogViewModel : ObservableObject
{
    private readonly UnpackRequest _request;

    public UnpackDialogViewModel(UnpackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        DestinationText = request.Destination;
        SeparateFolders = request.SeparateFolders;
    }

    public string Title => Loc.Get("Archive_UnpackTitle");

    /// <summary>"Unpack “a.zip” to:" or "Unpack 2 items to:".</summary>
    public string Prompt => _request is { ArchiveCount: 1, SingleName: { } name }
        ? Loc.Format("Archive_UnpackOne", name)
        : Loc.Format("Archive_UnpackMany", Loc.Count("Count_Items", _request.ArchiveCount));

    [ObservableProperty]
    public partial string DestinationText { get; set; }

    [ObservableProperty]
    public partial bool SeparateFolders { get; set; }

    [ObservableProperty]
    public partial bool RunImmediately { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnDestinationTextChanged(string value) => Error = null;

    public async Task<UnpackAnswer?> AcceptAsync()
    {
        var text = DestinationText;
        if (_request.Validate is { } validate && await validate(text) is { } error)
        {
            Error = error;
            return null;
        }
        return new UnpackAnswer(text, SeparateFolders, RunImmediately);
    }
}

/// <summary>Asks for an archive's password; after a wrong one it says so. The password is kept only by this model
/// and the field while the dialog is open.</summary>
public sealed partial class PasswordDialogViewModel(string archiveName, bool previousWasWrong) : ObservableObject
{
    public string Title => Loc.Get("Archive_PasswordTitle");

    public string Prompt => Loc.Format("Archive_PasswordPrompt", archiveName);

    /// <summary>"That password is wrong." after a wrong one; null otherwise.</summary>
    public string? WrongPassword => previousWasWrong ? Loc.Get("Archive_PasswordWrong") : null;

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    /// <summary>The typed password (an empty one is a password too).</summary>
    public string Accept() => Password;
}
