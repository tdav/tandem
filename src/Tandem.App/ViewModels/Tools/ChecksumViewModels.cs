using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Tools.Checksums;

namespace Tandem.App.ViewModels.Tools;

public sealed record ChecksumCreateRequest(ChecksumAlgorithm Algorithm, string Name);

public sealed partial class ChecksumCreateViewModel : ObservableObject, IDialogModel<ChecksumCreateRequest>
{
    public ChecksumCreateViewModel(string baseName)
    {
        Name = baseName + "." + Checksum.Extension(Algorithm);
    }

    [ObservableProperty]
    public partial ChecksumAlgorithm Algorithm { get; set; } = ChecksumAlgorithm.Sha256;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public event EventHandler<ChecksumCreateRequest?>? CloseRequested;

    partial void OnAlgorithmChanged(ChecksumAlgorithm oldValue, ChecksumAlgorithm newValue)
    {
        var previous = "." + Checksum.Extension(oldValue);
        var stem = Name.EndsWith(previous, StringComparison.OrdinalIgnoreCase) ? Name[..^previous.Length] : Name;
        Name = stem + "." + Checksum.Extension(newValue);
    }

    [RelayCommand]
    private void Accept()
    {
        var name = Name;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.AsSpan().ContainsAny('/', '\r', '\n')
            || name.Contains('\0'))
        {
            Error = Loc.Get("Checksum_InvalidName");
            return;
        }
        var extension = "." + Checksum.Extension(Algorithm);
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            Error = Loc.Format("Checksum_WrongExtension", extension);
            return;
        }
        Error = null;
        CloseRequested?.Invoke(this, new(Algorithm, name));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);
}

public sealed class ChecksumResultsViewModel
{
    public ChecksumResultsViewModel(IEnumerable<VerifyResult> results, IReadOnlyList<int> unreadableLineNumbers)
    {
        Rows = results.OrderBy(result => result.Status switch
        {
            VerifyStatus.Mismatch => 0,
            VerifyStatus.Missing => 1,
            VerifyStatus.Unreadable or VerifyStatus.OutsideFolder => 2,
            _ => 3,
        }).ToArray();
        UnreadableLineNumbers = unreadableLineNumbers;
    }

    public IReadOnlyList<VerifyResult> Rows { get; }
    public IReadOnlyList<int> UnreadableLineNumbers { get; }

    public string Summary => Loc.Format("Checksum_VerifySummary",
        Rows.Count(row => row.Status == VerifyStatus.Ok),
        Rows.Count(row => row.Status == VerifyStatus.Mismatch), Rows.Count(row => row.Status == VerifyStatus.Missing));
}
