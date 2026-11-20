using System.IO.Compression;
using System.Text;

namespace Tandem.Archives.Tests;

public class ArchiveFormatsTests
{
    [Theory]
    [InlineData("a.ZIP", ArchiveFormat.Zip)]
    [InlineData("a.tar", ArchiveFormat.Tar)]
    [InlineData("a.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("a.tgz", ArchiveFormat.TarGz)]
    [InlineData("a.TAR.BZ2", ArchiveFormat.TarBz2)]
    [InlineData("a.tbz2", ArchiveFormat.TarBz2)]
    [InlineData("a.tar.xz", ArchiveFormat.TarXz)]
    [InlineData("a.txz", ArchiveFormat.TarXz)]
    [InlineData("a.7z", ArchiveFormat.SevenZip)]
    [InlineData("a.rar", ArchiveFormat.Rar)]
    [InlineData("a.docx", null)]
    [InlineData("a.gz", null)]
    [InlineData("zip", null)]
    [InlineData(".tar.gz.txt", null)]
    public void FromName(string fileName, ArchiveFormat? expected) =>
        Assert.Equal(expected, ArchiveFormats.FromName(fileName));

    [Theory]
    [InlineData("sample.zip", ArchiveFormat.Zip)]
    [InlineData("encrypted.zip", ArchiveFormat.Zip)]
    [InlineData("cp866.zip", ArchiveFormat.Zip)]
    [InlineData("slip.zip", ArchiveFormat.Zip)]
    [InlineData("broken.zip", ArchiveFormat.Zip)]
    [InlineData("sample.tar", ArchiveFormat.Tar)]
    [InlineData("trap.tar", ArchiveFormat.Tar)]
    [InlineData("sample.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("sample.tar.bz2", ArchiveFormat.TarBz2)]
    [InlineData("sample.tar.xz", ArchiveFormat.TarXz)]
    [InlineData("sample.7z", ArchiveFormat.SevenZip)]
    [InlineData("solid.7z", ArchiveFormat.SevenZip)]
    [InlineData("sample.rar", ArchiveFormat.Rar)]
    [InlineData("solid.rar", ArchiveFormat.Rar)]
    [InlineData("encrypted.7z", ArchiveFormat.SevenZip)]
    [InlineData("a.txt", null)]
    public async Task Detects_by_signature(string fixture, ArchiveFormat? expected)
    {
        using var stream = new MemoryStream(Fixture.Bytes(fixture));
        stream.Position = 3;

        var format = await ArchiveFormats.DetectAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(expected, format);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task Gzip_of_something_other_than_a_tar_is_not_an_archive()
    {
        using var stream = new MemoryStream();
        await using (var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("just a log line\n", 100))));

        Assert.Null(await ArchiveFormats.DetectAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task Empty_and_tiny_streams_are_not_archives()
    {
        Assert.Null(await ArchiveFormats.DetectAsync(new MemoryStream(), TestContext.Current.CancellationToken));
        Assert.Null(await ArchiveFormats.DetectAsync(new MemoryStream("PK"u8.ToArray()),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip, "zip")]
    [InlineData(ArchiveFormat.Tar, "tar")]
    [InlineData(ArchiveFormat.TarGz, "tar.gz")]
    [InlineData(ArchiveFormat.TarBz2, "tar.bz2")]
    [InlineData(ArchiveFormat.TarXz, "tar.xz")]
    [InlineData(ArchiveFormat.SevenZip, "7z")]
    [InlineData(ArchiveFormat.Rar, "rar")]
    public void Default_extension_names_the_format_back(ArchiveFormat format, string extension)
    {
        Assert.Equal(extension, ArchiveFormats.DefaultExtension(format));
        Assert.Equal(format, ArchiveFormats.FromName("x." + extension));
    }

    [Fact]
    public void Creatable_formats_are_those_the_library_writes()
    {
        // SharpCompress 1.0.0 has no 7z writer (SharpCompressProbeTests.Seven_zip_cannot_be_written).
        Assert.Equal([ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.TarGz], ArchiveFormats.Creatable);
    }

    [Fact]
    public void Only_zip_can_be_edited() =>
        Assert.Equal([ArchiveFormat.Zip], Enum.GetValues<ArchiveFormat>().Where(ArchiveFormats.CanEdit));
}
