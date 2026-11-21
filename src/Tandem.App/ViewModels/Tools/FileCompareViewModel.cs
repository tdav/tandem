using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;
using Tandem.Core.Tools.Compare;
using Tandem.Core.Vfs;
using Tandem.Core.Viewing;

namespace Tandem.App.ViewModels.Tools;

public sealed record BinaryRangeRow(long Offset, long Length, string LeftHex, string RightHex)
{
    public string OffsetText => Offset.ToString("X8", Loc.Culture);
}
public sealed record CompareModeChoice(CompareMode Value, string Label);
public sealed record CompareEncodingChoice(TextEncodingInfo? Value, string Label);

public sealed partial class FileCompareViewModel : ObservableObject, IDialogModel<bool>, IAsyncDisposable
{
    private readonly ProviderRegistry _providers;
    private readonly VfsPath _left;
    private readonly VfsPath _right;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _comparison;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;
    private IReadOnlyList<int> _differenceStarts = [];

    [ObservableProperty] public partial CompareMode Mode { get; set; }
    [ObservableProperty] public partial TextEncodingInfo? LeftEncoding { get; set; }
    [ObservableProperty] public partial TextEncodingInfo? RightEncoding { get; set; }
    [ObservableProperty] public partial IReadOnlyList<DiffRow> Rows { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<BinaryRangeRow> Ranges { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<object> DisplayRows { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; }
    [ObservableProperty] public partial int CurrentRow { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string? Error { get; private set; }

    public string LeftTitle { get; }
    public string RightTitle { get; }
    public IReadOnlyList<CompareModeChoice> ModeChoices { get; }
    public IReadOnlyList<CompareEncodingChoice> EncodingChoices { get; }

    public CompareModeChoice SelectedMode
    {
        get => ModeChoices.First(choice => choice.Value == Mode);
        set { if (value is not null) Mode = value.Value; }
    }

    public CompareEncodingChoice SelectedLeftEncoding
    {
        get => EncodingChoices.FirstOrDefault(choice => choice.Value == LeftEncoding) ?? EncodingChoices[0];
        set { if (value is not null) LeftEncoding = value.Value; }
    }

    public CompareEncodingChoice SelectedRightEncoding
    {
        get => EncodingChoices.FirstOrDefault(choice => choice.Value == RightEncoding) ?? EncodingChoices[0];
        set { if (value is not null) RightEncoding = value.Value; }
    }

    public event EventHandler<bool>? CloseRequested;

    internal FileCompareViewModel(ProviderRegistry providers, VfsPath left, VfsPath right, CancellationToken ct)
    {
        _providers = providers;
        _left = left;
        _right = right;
        _lifetime = ct;
        LeftTitle = providers.DisplayPath(left);
        RightTitle = providers.DisplayPath(right);
        ModeChoices = Enum.GetValues<CompareMode>().Select(mode => new CompareModeChoice(mode, Loc.Enum(mode))).ToArray();
        EncodingChoices = [new(null, Loc.Get("Compare_EncodingAuto")), ..TextEncodings.All.Select(e => new CompareEncodingChoice(e, e.Id))];
        Rows = [];
        Ranges = [];
        DisplayRows = [];
        Summary = "";
        CurrentRow = -1;
        Reload();
    }

    public static async Task<FileCompareViewModel> OpenAsync(ProviderRegistry providers, VfsPath left, VfsPath right,
        CancellationToken ct)
    {
        var model = new FileCompareViewModel(providers, left, right, ct);
        await model.WhenReadyAsync();
        return model;
    }

    public Task WhenReadyAsync() => _pending;

    partial void OnModeChanged(CompareMode value)
    {
        OnPropertyChanged(nameof(SelectedMode));
        Reload();
    }

    partial void OnLeftEncodingChanged(TextEncodingInfo? value)
    {
        OnPropertyChanged(nameof(SelectedLeftEncoding));
        Reload();
    }

    partial void OnRightEncodingChanged(TextEncodingInfo? value)
    {
        OnPropertyChanged(nameof(SelectedRightEncoding));
        Reload();
    }

    public void NextDifference()
    {
        foreach (var row in _differenceStarts)
            if (row > CurrentRow)
            {
                CurrentRow = row;
                return;
            }
    }

    public void PreviousDifference()
    {
        for (var i = _differenceStarts.Count - 1; i >= 0; i--)
            if (_differenceStarts[i] < CurrentRow)
            {
                CurrentRow = _differenceStarts[i];
                return;
            }
    }

    public void Close() => CloseRequested?.Invoke(this, false);

    private void Reload()
    {
        if (_disposed) return;
        _comparison?.Cancel();
        var previous = _pending;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _comparison = source;
        IsBusy = true;
        Error = null;
        Summary = Loc.Get("Compare_Working");
        _pending = CompareAsync(Mode, LeftEncoding, RightEncoding, previous, source);
    }

    private async Task CompareAsync(CompareMode mode, TextEncodingInfo? leftEncoding, TextEncodingInfo? rightEncoding,
        Task previous, CancellationTokenSource source)
    {
        using (source)
        {
            var ct = source.Token;
            try
            {
                // The preceding request closes its streams before this one borrows connections from the same pool.
                await previous;
                var (result, ranges) = await Task.Run(async () =>
                {
                    var comparison = await FileComparison.CompareAsync(_providers, _left, _right, mode,
                        leftEncoding, rightEncoding, null, ct);
                    var preview = comparison.Binary is { Ranges.Count: > 0 } binary
                        ? await PreviewRangesAsync(binary.Ranges, ct) : [];
                    return (comparison, preview);
                }, ct);
                if (ct.IsCancellationRequested) return;

                CurrentRow = -1;
                Rows = result.Text?.Rows ?? [];
                Ranges = ranges;
                DisplayRows = result.Text is not null ? Rows : Ranges;
                _differenceStarts = result.Text?.DifferenceStarts ?? Enumerable.Range(0, Ranges.Count).ToArray();
                Summary = SummaryOf(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (FileTooLargeForTextException)
            {
                if (!ct.IsCancellationRequested) ReportError(Loc.Get("Compare_TooLargeForText"));
            }
            catch (Exception error) when (ItemErrors.IsItemError(error) || error is KeyNotFoundException)
            {
                if (!ct.IsCancellationRequested) ReportError(OperationTexts.ForFailure(error));
            }
            finally
            {
                if (ReferenceEquals(source, _comparison))
                {
                    _comparison = null;
                    if (!_disposed) IsBusy = false;
                }
            }
        }
    }

    private void ReportError(string message)
    {
        Rows = [];
        Ranges = [];
        DisplayRows = [];
        _differenceStarts = [];
        CurrentRow = -1;
        Summary = "";
        Error = message;
    }

    private async Task<IReadOnlyList<BinaryRangeRow>> PreviewRangesAsync(IReadOnlyList<ByteRange> ranges, CancellationToken ct)
    {
        var left = await ReadPreviewsAsync(_left, ranges, ct);
        var right = await ReadPreviewsAsync(_right, ranges, ct);
        return ranges.Select((range, index) => new BinaryRangeRow(range.Offset, range.Length, left[index], right[index]))
            .ToArray();
    }

    private async Task<string[]> ReadPreviewsAsync(VfsPath path, IReadOnlyList<ByteRange> ranges, CancellationToken ct)
    {
        await using var stream = await _providers.For(path).OpenReadAsync(path, ct);
        var result = new string[ranges.Count];
        var buffer = new byte[64 * 1024];
        long position = 0;
        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            if (stream.CanSeek)
            {
                stream.Seek(range.Offset, SeekOrigin.Begin);
                position = range.Offset;
            }
            while (position < range.Offset)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(range.Offset - position, buffer.Length)), ct);
                if (count == 0) break;
                position += count;
            }
            var requested = (int)Math.Min(HexDump.BytesPerRow, range.Length);
            var read = position == range.Offset ? await stream.ReadAtLeastAsync(buffer.AsMemory(0, requested),
                requested, false, ct) : 0;
            result[i] = HexDump.FormatRow(range.Offset, buffer.AsSpan(0, read));
            position += read;
        }
        return result;
    }

    private static string SummaryOf(FileComparisonResult comparison)
    {
        if (comparison.Text is { } text)
        {
            if (text.LineEndingsDiffer) return Loc.Get("Compare_LineEndings");
            return text.DifferenceStarts.Count == 0 ? Loc.Get("Compare_Identical")
                : Loc.Format("Compare_TextSummary", text.DifferenceStarts.Count);
        }

        var binary = comparison.Binary!;
        if (binary.Identical) return Loc.Get("Compare_Identical");
        var differences = Loc.Format("Compare_ByteSummary", binary.DifferingBytes);
        if (binary.Truncated) differences += " " + Loc.Format("Compare_Truncated", BinaryDiff.MaxRanges);
        return binary.LeftSize == binary.RightSize ? differences
            : Loc.Format("Compare_DifferentSizes", SizeFormatter.Format(binary.LeftSize),
                SizeFormatter.Format(binary.RightSize), differences);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _comparison?.Cancel();
        await _pending;
    }
}
