using System.Text;
using Tandem.Core.Tools.Checksums;

namespace Tandem.Core.Tests.Tools.Checksums;

public sealed class ChecksumTests
{
    [Theory]
    [InlineData(ChecksumAlgorithm.Crc32, "123456789", "cbf43926")]
    [InlineData(ChecksumAlgorithm.Md5, "", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData(ChecksumAlgorithm.Sha1, "abc", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData(ChecksumAlgorithm.Sha256, "abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task Known_vectors(ChecksumAlgorithm algorithm, string input, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        Assert.Equal(expected, await Checksum.ComputeAsync(stream, algorithm, null, null, TestContext.Current.CancellationToken));
        ChecksumLine[] lines = [new("подпапка/a b.txt", expected)];
        var parsed = ChecksumFile.Parse(algorithm, ChecksumFile.Format(algorithm, lines));
        Assert.Equal(lines, parsed.Lines);
        Assert.Empty(parsed.UnreadableLineNumbers);
    }

    [Fact]
    public void Foreign_spelling_and_invalid_line_numbers()
    {
        const string hash = "d41d8cd98f00b204e9800998ecf8427e";
        var parsed = ChecksumFile.Parse(ChecksumAlgorithm.Md5, $"# comment\r\n\r\nMD5 (a.txt) = {hash}\r\ninvalid\r\n{hash}  x\\b.txt\r\n");
        Assert.Equal([new("a.txt", hash), new("x/b.txt", hash)], parsed.Lines);
        Assert.Equal([4], parsed.UnreadableLineNumbers);
    }
}
