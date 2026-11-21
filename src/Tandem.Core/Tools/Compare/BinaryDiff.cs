using System.Buffers;

namespace Tandem.Core.Tools.Compare;

public readonly record struct ByteRange(long Offset, long Length);
public sealed record BinaryDiffResult(long LeftSize, long RightSize, IReadOnlyList<ByteRange> Ranges, bool Truncated,
    long DifferingBytes)
{
    public bool Identical => LeftSize == RightSize && DifferingBytes == 0;
}

public static class BinaryDiff
{
    public const int MaxRanges = 1000;
    private const int BlockSize = 64 * 1024;

    public static async Task<BinaryDiffResult> CompareAsync(Stream left, Stream right, IProgress<long>? bytes,
        CancellationToken ct)
    {
        var ranges = new List<ByteRange>();
        bool truncated = false;
        long offset = 0, differing = 0, rangeStart = -1;

        var (leftSize, rightSize, _) = await ReadBlocksAsync(left, right, (l, r) =>
        {
            if (rangeStart < 0 && l.SequenceEqual(r))
            {
                offset += l.Length;
                return true;
            }

            for (var i = 0; i < l.Length; i++, offset++)
            {
                if (l[i] != r[i])
                {
                    differing++;
                    if (rangeStart < 0) rangeStart = offset;
                }
                else if (rangeStart >= 0)
                {
                    EndRange();
                }
            }
            return true;
        }, stopOnLengthDifference: false, bytes, null, ct).ConfigureAwait(false);
        if (rangeStart >= 0) EndRange();
        return new(leftSize, rightSize, ranges, truncated, differing);

        void EndRange()
        {
            if (ranges.Count < MaxRanges) ranges.Add(new(rangeStart, offset - rangeStart));
            else truncated = true;
            rangeStart = -1;
        }
    }

    public static async Task<bool> AreEqualAsync(Stream left, Stream right, IProgress<long>? bytes,
        Func<CancellationToken, ValueTask>? pause, CancellationToken ct)
    {
        var (_, _, equal) = await ReadBlocksAsync(left, right, (l, r) => l.SequenceEqual(r),
            stopOnLengthDifference: true, bytes, pause, ct).ConfigureAwait(false);
        return equal;
    }

    private delegate bool CompareBlock(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right);

    private static async Task<(long LeftSize, long RightSize, bool Equal)> ReadBlocksAsync(Stream left, Stream right,
        CompareBlock compare, bool stopOnLengthDifference, IProgress<long>? bytes,
        Func<CancellationToken, ValueTask>? pause, CancellationToken ct)
    {
        var leftBuffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            long leftSize = 0, rightSize = 0;
            var leftDone = false;
            var rightDone = false;
            while (!leftDone || !rightDone)
            {
                ct.ThrowIfCancellationRequested();
                if (pause is not null) await pause(ct).ConfigureAwait(false);
                var l = leftDone ? 0 : await left.ReadAtLeastAsync(leftBuffer.AsMemory(0, BlockSize), BlockSize,
                    false, ct).ConfigureAwait(false);
                var r = rightDone ? 0 : await right.ReadAtLeastAsync(rightBuffer.AsMemory(0, BlockSize), BlockSize,
                    false, ct).ConfigureAwait(false);
                leftSize += l;
                rightSize += r;
                leftDone = l < BlockSize;
                rightDone = r < BlockSize;
                bytes?.Report(leftSize + rightSize);
                var common = Math.Min(l, r);
                if ((stopOnLengthDifference && l != r) ||
                    (common > 0 && !compare(leftBuffer.AsSpan(0, common), rightBuffer.AsSpan(0, common))))
                    return (leftSize, rightSize, false);
            }
            return (leftSize, rightSize, leftSize == rightSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }
}
