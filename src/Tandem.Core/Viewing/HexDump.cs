using System.Buffers;
using System.Text;

namespace Tandem.Core.Viewing;

/// <summary>
/// Hex view rows of 16 bytes: offset (<c>X8</c>, wider when needed), two spaces, the bytes as <c>XX </c> with an
/// extra space after the eighth, a space, then <c>|…|</c> with printable ASCII as is and other bytes as '.'.
/// A short last row is padded with spaces so its '|' lines up with full rows.
/// </summary>
/// <remarks>
/// <see cref="ReadRowsAsync"/> reads only the requested rows. It seeks the caller's stream, so calls on one
/// stream must not overlap, and nothing else may use that stream meanwhile.
/// </remarks>
public static class HexDump
{
    public const int BytesPerRow = 16;

    public static string FormatRow(long offset, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (bytes.Length > BytesPerRow)
            throw new ArgumentException($"A row holds at most {BytesPerRow} bytes.", nameof(bytes));

        var sb = new StringBuilder(80);
        sb.Append(offset.ToString("X8")).Append("  ");
        for (var i = 0; i < BytesPerRow; i++)
        {
            if (i < bytes.Length) sb.Append(bytes[i].ToString("X2")).Append(' ');
            else sb.Append("   ");
            if (i == 7) sb.Append(' ');
        }
        sb.Append(" |");
        foreach (var b in bytes)
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return sb.Append('|').ToString();
    }

    /// <summary>
    /// Up to <paramref name="rowCount"/> rows from <paramref name="offset"/> rounded down to a multiple of
    /// <see cref="BytesPerRow"/>; fewer at the end of the stream, none past it.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadRowsAsync(Stream seekable, long offset, int rowCount, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        if (!seekable.CanSeek)
            throw new ArgumentException("The stream must support seeking; copy a non-seekable source to a temporary file first.", nameof(seekable));

        var start = offset - offset % BytesPerRow;
        var length = seekable.Length;
        if (start >= length || rowCount == 0) return [];
        var count = (int)Math.Min((long)rowCount * BytesPerRow, Math.Min(length - start, Array.MaxLength));

        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            seekable.Seek(start, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var n = await seekable.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }

            var rows = new List<string>((read + BytesPerRow - 1) / BytesPerRow);
            for (var i = 0; i < read; i += BytesPerRow)
                rows.Add(FormatRow(start + i, buffer.AsSpan(i, Math.Min(BytesPerRow, read - i))));
            return rows;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
