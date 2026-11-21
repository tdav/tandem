using System.IO.Hashing;
using System.Text;

namespace Tandem.Core.Tests.Tools;

/// <summary>
/// Pins what System.IO.Hashing 11.0.0 does, for the checksum tools (tasks 6, 15).
/// <list type="bullet">
/// <item><c>new Crc32()</c>, <c>Append(ReadOnlySpan&lt;byte&gt;)</c> any number of times, then
/// <c>GetCurrentHashAsUInt32()</c>: the CRC-32 (IEEE 802.3, the one in zip and .sfv files) as a number.</item>
/// <item><c>Crc32.Hash(bytes)</c> and <c>GetCurrentHash()</c> return the four bytes <b>little-endian</b> (least
/// significant byte first): for "123456789" they are 26 39 F4 CB. An .sfv file writes the number big-endian in hex
/// ("CBF43926"), so format the UInt32 with "X8", not the bytes in order.</item>
/// <item>Appending in pieces gives the one-shot result; <c>Reset()</c> starts again.</item>
/// </list>
/// </summary>
public sealed class HashingProbeTests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void Crc32_of_123456789_is_CBF43926()
    {
        var crc = new Crc32();
        crc.Append(Check);

        Assert.Equal(0xCBF43926u, crc.GetCurrentHashAsUInt32());
        Assert.Equal("CBF43926", crc.GetCurrentHashAsUInt32().ToString("X8"));
        Assert.Equal(new byte[] { 0x26, 0x39, 0xF4, 0xCB }, Crc32.Hash(Check)); // little-endian
        Assert.Equal(Crc32.Hash(Check), crc.GetCurrentHash());
        Assert.Equal(0xCBF43926u, Crc32.HashToUInt32(Check));
    }

    [Fact]
    public void Crc32_appended_in_pieces_equals_one_shot()
    {
        var data = new byte[100_003];
        new Random(7).NextBytes(data);
        var crc = new Crc32();
        for (var offset = 0; offset < data.Length; offset += 4096)
            crc.Append(data.AsSpan(offset, Math.Min(4096, data.Length - offset)));

        Assert.Equal(Crc32.HashToUInt32(data), crc.GetCurrentHashAsUInt32());

        crc.Reset();
        crc.Append(Check);
        Assert.Equal(0xCBF43926u, crc.GetCurrentHashAsUInt32());
    }
}
