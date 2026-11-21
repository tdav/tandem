using Tandem.Core.Vfs;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tools.Compare;

public enum CompareMode { Auto, Text, Binary }
public sealed record FileComparisonResult(TextDiffResult? Text, BinaryDiffResult? Binary,
    TextEncodingInfo? LeftEncoding, TextEncodingInfo? RightEncoding);
public sealed class FileTooLargeForTextException : IOException;

public static class FileComparison
{
    public const long MaxTextBytes = 16 * 1024 * 1024;

    public static async Task<FileComparisonResult> CompareAsync(ProviderRegistry providers, VfsPath left,
        VfsPath right, CompareMode mode, TextEncodingInfo? leftEncoding, TextEncodingInfo? rightEncoding,
        IProgress<long>? bytes, CancellationToken ct)
    {
        var leftProvider = providers.For(left);
        var rightProvider = providers.For(right);
        var leftEntry = await GetFileAsync(leftProvider, left, ct).ConfigureAwait(false);
        var rightEntry = await GetFileAsync(rightProvider, right, ct).ConfigureAwait(false);
        if (mode != CompareMode.Binary && leftEntry.Size <= MaxTextBytes && rightEntry.Size <= MaxTextBytes)
        {
            var l = await TryReadTextAsync(leftProvider, leftEntry, mode, leftEncoding, ct).ConfigureAwait(false);
            if (l is not null)
            {
                var r = await TryReadTextAsync(rightProvider, rightEntry, mode, rightEncoding, ct).ConfigureAwait(false);
                if (r is not null)
                {
                    bytes?.Report(l.Value.Bytes + r.Value.Bytes);
                    ct.ThrowIfCancellationRequested();
                    var diff = TextDiff.Compare(l.Value.Text, r.Value.Text);
                    ct.ThrowIfCancellationRequested();
                    return new(diff, null, l.Value.Encoding, r.Value.Encoding);
                }
            }
        }
        else if (mode == CompareMode.Text)
        {
            throw new FileTooLargeForTextException();
        }

        await using var leftStream = await leftProvider.OpenReadAsync(left, ct).ConfigureAwait(false);
        await using var rightStream = await rightProvider.OpenReadAsync(right, ct).ConfigureAwait(false);
        return new(null, await BinaryDiff.CompareAsync(leftStream, rightStream, bytes, ct).ConfigureAwait(false),
            null, null);
    }

    private static async Task<FileEntry> GetFileAsync(IFileSystemProvider provider, VfsPath path, CancellationToken ct)
    {
        var entry = await provider.GetEntryAsync(path, ct).ConfigureAwait(false);
        return entry is { Kind: EntryKind.File } ? entry : throw new VfsNotFoundException(path.Path);
    }

    private static async Task<(string Text, TextEncodingInfo Encoding, long Bytes)?> TryReadTextAsync(
        IFileSystemProvider provider, FileEntry entry, CompareMode mode, TextEncodingInfo? encoding, CancellationToken ct)
    {
        await using var stream = await provider.OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
        var sample = new byte[TextEncodingDetector.SampleSize];
        var length = await stream.ReadAtLeastAsync(sample, sample.Length, false, ct).ConfigureAwait(false);
        var wholeFile = length < sample.Length || length == entry.Size;
        var guess = TextEncodingDetector.Detect(sample.AsSpan(0, length), wholeFile);
        if (mode == CompareMode.Auto && encoding is null &&
            (guess.LooksBinary || TextEncodingDetector.IsUnlikelyUtf16(guess, sample.AsSpan(0, length), wholeFile)))
            return null;

        using var data = new MemoryStream();
        data.Write(sample.AsSpan(0, length));
        while (true)
        {
            var n = await stream.ReadAsync(sample, ct).ConfigureAwait(false);
            if (n == 0) break;
            if (data.Length + n > MaxTextBytes)
            {
                if (mode == CompareMode.Text) throw new FileTooLargeForTextException();
                return null;
            }
            data.Write(sample.AsSpan(0, n));
        }
        encoding ??= guess.Encoding;
        return (encoding.Encoding.GetString(data.GetBuffer(), guess.BomLength, (int)data.Length - guess.BomLength),
            encoding, data.Length);
    }
}
