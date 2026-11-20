using System.Text;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tests.Viewing;

public class HexDumpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Hello => [.. Encoding.ASCII.GetBytes("Hello, world!\n"), 0x00, 0xFF];

    [Theory]
    [InlineData(0x10L, 16, "00000010  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 0A 00 FF  |Hello, world!...|")]
    [InlineData(0x20L, 5, "00000020  48 65 6C 6C 6F                                    |Hello|")]
    [InlineData(0x30L, 9, "00000030  48 65 6C 6C 6F 2C 20 77  6F                       |Hello, wo|")]
    [InlineData(0x40L, 0, "00000040                                                    ||")]
    [InlineData(0x1_0000_0000L, 16, "100000000  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 0A 00 FF  |Hello, world!...|")]
    public void Formats_full_and_short_rows(long offset, int count, string expected)
    {
        Assert.Equal(expected, HexDump.FormatRow(offset, Hello.AsSpan(0, count)));
    }

    [Fact]
    public void Short_rows_are_as_wide_as_full_rows_before_the_bar()
    {
        var full = HexDump.FormatRow(0, Hello);
        for (var n = 0; n <= 16; n++)
            Assert.Equal(full.IndexOf('|'), HexDump.FormatRow(0, Hello.AsSpan(0, n)).IndexOf('|'));
    }

    [Fact]
    public void Row_longer_than_16_bytes_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => HexDump.FormatRow(0, new byte[17]));
    }

    [Fact]
    public async Task Rows_start_at_offset_rounded_down_and_stop_at_the_end()
    {
        var data = Enumerable.Range(0, 40).Select(i => (byte)('A' + i % 26)).ToArray();
        using var stream = new MemoryStream(data);

        var rows = await HexDump.ReadRowsAsync(stream, 0x13, 5, Ct);

        Assert.Equal(2, rows.Count);
        Assert.Equal(HexDump.FormatRow(0x10, data.AsSpan(0x10, 16)), rows[0]);
        Assert.Equal(HexDump.FormatRow(0x20, data.AsSpan(0x20, 8)), rows[1]);
        Assert.Single(await HexDump.ReadRowsAsync(stream, 39, 5, Ct)); // rounds down to 0x20
        Assert.Empty(await HexDump.ReadRowsAsync(stream, 48, 5, Ct));
        Assert.Empty(await HexDump.ReadRowsAsync(stream, 1000, 5, Ct));
    }

    [Fact]
    public async Task Hex_rows_from_8_GiB_offset_read_only_those_rows()
    {
        const long length = 16L << 30;
        var stream = new SparseStream(length, pos => (byte)(pos % 251));
        const long offset = 8L << 30;

        var rows = await HexDump.ReadRowsAsync(stream, offset + 5, 32, Ct);

        Assert.Equal(32, rows.Count);
        Assert.StartsWith("200000000  ", rows[0]);
        Assert.StartsWith("2000001F0  ", rows[31]);
        Assert.Equal(32 * HexDump.BytesPerRow, stream.BytesRead);
    }

    [Fact]
    public async Task Non_seekable_stream_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HexDump.ReadRowsAsync(new NonSeekableStream(), 0, 1, Ct));
    }
}

/// <summary>A readable stream that cannot seek, like a pipe or a decompressing archive entry.</summary>
internal sealed class NonSeekableStream : MemoryStream
{
    public override bool CanSeek => false;
}
