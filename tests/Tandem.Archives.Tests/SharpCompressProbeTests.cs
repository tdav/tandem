using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.GZip;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.Tar;
using SharpCompress.Writers.Zip;
using SharpCompress.Common.Tar.Headers;

namespace Tandem.Archives.Tests;

/// <summary>
/// Pins what SharpCompress 1.0.0 really does, before any archive code relies on it. Only library calls here.
/// <para>The 1.0.0 package keeps the 0.x entry points: <see cref="ArchiveFactory.Open(Stream, ReaderOptions)"/>,
/// <see cref="ReaderFactory.Open(Stream, ReaderOptions)"/>, <see cref="WriterFactory.Open"/> and
/// <c>ZipArchive.Create()</c>. There is no <c>OpenArchive</c>/<c>OpenReader</c>/<c>OpenWriter</c>/<c>CreateArchive</c>
/// and no <c>ArchiveDetector</c>.</para>
/// </summary>
public class SharpCompressProbeTests
{
    private static readonly byte[] DataBin = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    /// <summary>The files of the common tree: name → content.</summary>
    private static readonly Dictionary<string, byte[]> CommonFiles = new()
    {
        ["a.txt"] = "a"u8.ToArray(),
        ["docs/readme.txt"] = "hello\n"u8.ToArray(),
        ["docs/sub/data.bin"] = DataBin,
        ["привет.txt"] = Encoding.UTF8.GetBytes("привет\n"),
    };

    private static ReaderOptions Options(string? password = null) => new() { Password = password };

    private static IArchive OpenArchive(string fixture, string? password = null) =>
        ArchiveFactory.Open(new MemoryStream(Fixture.Bytes(fixture)), Options(password));

    private static byte[] ReadToEnd(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Every file in the archive, read through random access: key → content.</summary>
    private static Dictionary<string, byte[]> ReadRandomAccess(IArchive archive)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            using var stream = entry.OpenEntryStream();
            files[entry.Key!] = ReadToEnd(stream);
        }
        return files;
    }

    /// <summary>Every file in the archive, read forward-only in one pass: key → content.</summary>
    private static Dictionary<string, byte[]> ReadForwardOnly(IReader reader)
    {
        var files = new Dictionary<string, byte[]>();
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory) continue;
            using var stream = reader.OpenEntryStream();
            files[reader.Entry.Key!] = ReadToEnd(stream);
        }
        return files;
    }

    private static void AssertCommonTree(Dictionary<string, byte[]> files)
    {
        foreach (var (name, content) in CommonFiles)
        {
            Assert.True(files.ContainsKey(name), $"no {name}; got {string.Join(", ", files.Keys)}");
            Assert.Equal(content, files[name]);
        }
    }

    // ---- Reading model ------------------------------------------------------------------------------------

    /// <summary>zip, tar and 7z (solid or not) have random access through <see cref="ArchiveFactory"/>.</summary>
    [Theory]
    [InlineData("sample.zip")]
    [InlineData("sample.tar")]
    [InlineData("sample.7z")]
    [InlineData("solid.7z")]
    public void Lists_and_reads_every_readable_format(string fixture)
    {
        using var archive = OpenArchive(fixture);

        AssertCommonTree(ReadRandomAccess(archive));
        Assert.Contains(archive.Entries, e => e.IsDirectory && e.Key!.TrimEnd('/') == "empty");
    }

    /// <summary>Compressed tars are read forward-only through <see cref="ReaderFactory"/>, which sees through the
    /// compression to the tar inside.</summary>
    [Theory]
    [InlineData("sample.tar")]
    [InlineData("sample.tar.gz")]
    [InlineData("sample.tar.bz2")]
    [InlineData("sample.tar.xz")]
    public void Tars_read_forward_only_whatever_the_compression(string fixture)
    {
        using var reader = ReaderFactory.Open(new MemoryStream(Fixture.Bytes(fixture)), Options());

        Assert.Equal(ArchiveType.Tar, reader.ArchiveType);
        AssertCommonTree(ReadForwardOnly(reader));
    }

    /// <summary>No random access into a compressed tar: <see cref="ArchiveFactory"/> opens a .tar.gz as a
    /// one-entry gzip archive and does not recognise .tar.bz2/.tar.xz at all; <see cref="TarArchive"/> refuses a
    /// .tar.gz.</summary>
    [Fact]
    public void Compressed_tars_have_no_random_access()
    {
        using (var gz = OpenArchive("sample.tar.gz"))
        {
            Assert.IsType<GZipArchive>(gz);
            Assert.Single(gz.Entries);
        }
        Assert.Throws<InvalidOperationException>(() => OpenArchive("sample.tar.bz2"));
        Assert.Throws<InvalidOperationException>(() => OpenArchive("sample.tar.xz"));
        Assert.Throws<IncompleteArchiveException>(() =>
        {
            using var tar = TarArchive.Open(new MemoryStream(Fixture.Bytes("sample.tar.gz")), Options());
            _ = tar.Entries.ToList();
        });
    }

    /// <summary>7z has no forward-only reader: a pass over it goes through <see cref="IArchive.ExtractAllEntries"/>.
    /// </summary>
    [Fact]
    public void Seven_zip_has_no_forward_only_reader() =>
        Assert.Throws<InvalidFormatException>(() =>
            ReaderFactory.Open(new MemoryStream(Fixture.Bytes("sample.7z")), Options()));

    /// <summary>Solidity is told by <see cref="IArchive.IsSolid"/>. <see cref="IEntry.IsSolid"/> is false for the
    /// first entry of a solid block, so it cannot tell on its own. bsdtar's stored 7z is not solid, its compressed
    /// one is.</summary>
    [Fact]
    public void Archive_tells_whether_it_is_solid()
    {
        using (var store = OpenArchive("sample.7z")) Assert.False(store.IsSolid);
        using (var solid = OpenArchive("solid.7z"))
        {
            Assert.True(solid.IsSolid);
            Assert.False(solid.Entries.First(e => e.Key == "a.txt").IsSolid);
            Assert.True(solid.Entries.First(e => e.Key == "docs/readme.txt").IsSolid);
        }
        using (var zip = OpenArchive("sample.zip")) Assert.False(zip.IsSolid);
    }

    [Theory]
    [InlineData("solid.7z")]
    [InlineData("solid.rar")]
    public void Solid_archives_extract_all_entries_in_one_pass(string fixture)
    {
        using var archive = OpenArchive(fixture);
        Assert.True(archive.IsSolid);
        var expected = archive.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Key!, e => e.Size);

        using var reader = archive.ExtractAllEntries();
        var files = ReadForwardOnly(reader);

        Assert.Equal(expected.Keys.Order(), files.Keys.Order());
        foreach (var (key, size) in expected) Assert.Equal(size, files[key].LongLength);
        if (fixture == "solid.7z") AssertCommonTree(files);
    }

    /// <summary>External RAR fixtures do not hold the common tree: every file reads to its listed size.</summary>
    [Theory]
    [InlineData("sample.rar")]
    [InlineData("solid.rar")]
    public void Rar_lists_and_reads(string fixture)
    {
        using var archive = OpenArchive(fixture);

        Assert.Equal(ArchiveType.Rar, archive.Type);
        var files = archive.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var stream = file.OpenEntryStream();
            Assert.Equal(file.Size, ReadToEnd(stream).LongLength);
        }
    }

    [Fact]
    public void Entry_stream_may_be_abandoned_half_read()
    {
        foreach (var fixture in new[] { "sample.zip", "sample.tar", "sample.7z", "solid.7z" })
        {
            using var archive = OpenArchive(fixture);
            using (var half = archive.Entries.First(e => e.Key == "docs/sub/data.bin").OpenEntryStream())
                half.ReadExactly(new byte[100]);
            using var next = archive.Entries.First(e => e.Key == "docs/readme.txt").OpenEntryStream();
            Assert.Equal("hello\n"u8.ToArray(), ReadToEnd(next));
        }

        // Forward-only: the reader skips the rest of an abandoned entry on the next move.
        using var reader = ReaderFactory.Open(new MemoryStream(Fixture.Bytes("sample.tar.gz")), Options());
        byte[]? after = null;
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.Key == "docs/sub/data.bin")
                using (var half = reader.OpenEntryStream())
                    half.ReadExactly(new byte[100]);
            else if (reader.Entry.Key == "привет.txt")
                using (var next = reader.OpenEntryStream())
                    after = ReadToEnd(next);
        }
        Assert.Equal(CommonFiles["привет.txt"], after);
    }

    // ---- Entries ----------------------------------------------------------------------------------------------

    [Fact]
    public void Tar_symlink_entry_exposes_its_target()
    {
        using var archive = OpenArchive("trap.tar");

        var link = archive.Entries.Single(e => e.Key == "link");
        Assert.Equal("/tmp", link.LinkTarget);
        Assert.False(link.IsDirectory);
        var file = archive.Entries.Single(e => e.Key == "link/file.txt");
        Assert.Null(file.LinkTarget);
    }

    /// <summary><see cref="IEntry.Attrib"/> is not implemented for tar: it throws.</summary>
    [Fact]
    public void Tar_entry_attributes_throw()
    {
        using var archive = OpenArchive("sample.tar");
        Assert.Throws<NotImplementedException>(() => archive.Entries.First().Attrib);
    }

    /// <summary>Names come as stored: no confinement, no separator rewriting.</summary>
    [Fact]
    public void Slip_names_are_given_as_stored()
    {
        using var archive = OpenArchive("slip.zip");
        Assert.Equal(["../../evil.txt", "/abs.txt", "a/../../b.txt", "dir\\win.txt"],
            archive.Entries.Select(e => e.Key));
    }

    /// <summary>The fixtures were made at 2024-01-02 03:04:05 UTC. Tar and 7z store that instant, and readers return
    /// it as a <see cref="DateTimeKind.Local"/> time.</summary>
    [Theory]
    [InlineData("sample.tar")]
    [InlineData("sample.7z")]
    public void Tar_and_7z_times_are_instants(string fixture)
    {
        using var archive = OpenArchive(fixture);
        var modified = archive.Entries.First(e => e.Key == "a.txt").LastModifiedTime;

        Assert.Equal(DateTimeKind.Local, modified?.Kind);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), modified!.Value.ToUniversalTime());
    }

    /// <summary>Zip stores wall-clock fields with no zone (the fixture was written in UTC, two-second step: 05 is
    /// 06). The reader hands them back unchanged, labelled <see cref="DateTimeKind.Local"/>, whatever the zone.
    /// </summary>
    [Fact]
    public void Zip_times_are_zone_less_wall_clock()
    {
        using var archive = OpenArchive("sample.zip");
        var modified = archive.Entries.First(e => e.Key == "a.txt").LastModifiedTime;

        Assert.Equal(DateTimeKind.Local, modified?.Kind);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 6), DateTime.SpecifyKind(modified!.Value, DateTimeKind.Unspecified));
    }

    // ---- Zip names --------------------------------------------------------------------------------------------

    /// <summary>macOS /usr/bin/zip stores Cyrillic names as UTF-8 without the UTF-8 flag (bit 11); the default
    /// decoding reads them as UTF-8. A CP866 name without the flag comes out garbled.</summary>
    [Fact]
    public void Zip_names_without_the_utf8_flag_decode_as_utf8_by_default()
    {
        var bytes = Fixture.Bytes("sample.zip");
        var header = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("привет.txt")) - 30; // its local file header
        Assert.True("PK\u0003\u0004"u8.SequenceEqual(bytes.AsSpan(header, 4)));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, header + 6) & 0x800);
        using (var mac = OpenArchive("sample.zip")) Assert.Contains("привет.txt", mac.Entries.Select(e => e.Key));
        using (var dos = OpenArchive("cp866.zip")) Assert.NotEqual("привет.txt", dos.Entries.Single().Key);
    }

    /// <summary><see cref="ArchiveEncoding.CustomDecoder"/> gets every name's bytes with
    /// <see cref="EncodingType.Default"/> when the flag is clear and <see cref="EncodingType.UTF8"/> when it is set,
    /// so "valid UTF-8, else CP866" can be decided there.</summary>
    [Fact]
    public void Custom_decoder_reads_utf8_first_then_cp866()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp866 = Encoding.GetEncoding(866);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var types = new List<EncodingType>();
        var encoding = new ArchiveEncoding
        {
            CustomDecoder = (data, start, count, type) =>
            {
                lock (types) types.Add(type);
                try
                {
                    return strictUtf8.GetString(data, start, count);
                }
                catch (DecoderFallbackException)
                {
                    return cp866.GetString(data, start, count);
                }
            },
        };
        string[] Names(byte[] zip)
        {
            using var archive = ArchiveFactory.Open(new MemoryStream(zip), new ReaderOptions { ArchiveEncoding = encoding });
            return archive.Entries.Select(e => e.Key!).ToArray();
        }

        Assert.Equal(["привет.txt"], Names(Fixture.Bytes("cp866.zip")));
        Assert.Contains("привет.txt", Names(Fixture.Bytes("sample.zip")));
        Assert.DoesNotContain(EncodingType.UTF8, types);

        types.Clear();
        Assert.Equal(["привет.txt"], Names(WriteZip(new ArchiveEncoding { Default = Encoding.UTF8 })));
        Assert.Contains(EncodingType.UTF8, types);
    }

    /// <summary>The zip writer stores names as UTF-8 but sets the UTF-8 flag only when
    /// <see cref="ArchiveEncoding.Default"/> is set to <see cref="Encoding.UTF8"/> explicitly.</summary>
    [Fact]
    public void Zip_writer_sets_the_utf8_flag_only_when_told()
    {
        var plain = WriteZip(null);
        var flagged = WriteZip(new ArchiveEncoding { Default = Encoding.UTF8 });

        Assert.Equal(0, BitConverter.ToUInt16(plain, 6) & 0x800);
        Assert.Equal(0x800, BitConverter.ToUInt16(flagged, 6) & 0x800);
        var utf8Name = Encoding.UTF8.GetBytes("привет.txt");
        Assert.Equal(utf8Name, plain.AsSpan(30, utf8Name.Length).ToArray());
        Assert.Equal(utf8Name, flagged.AsSpan(30, utf8Name.Length).ToArray());
    }

    private static byte[] WriteZip(ArchiveEncoding? encoding)
    {
        var output = new MemoryStream();
        var options = new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true };
        if (encoding is not null) options.ArchiveEncoding = encoding;
        using (var writer = WriterFactory.Open(output, ArchiveType.Zip, options))
            writer.Write("привет.txt", new MemoryStream("x"u8.ToArray()), DateTime.UtcNow);
        return output.ToArray();
    }

    // ---- Writing ----------------------------------------------------------------------------------------------

    internal static WriterOptions WriterOptionsFor(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true },
        ArchiveFormat.Tar => new TarWriterOptions(CompressionType.None, true, TarHeaderWriteFormat.GNU_TAR_LONG_LINK)
            { LeaveStreamOpen = true },
        ArchiveFormat.TarGz => new TarWriterOptions(CompressionType.GZip, true, TarHeaderWriteFormat.GNU_TAR_LONG_LINK)
            { LeaveStreamOpen = true },
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static ArchiveType TypeOf(ArchiveFormat format) =>
        format == ArchiveFormat.Zip ? ArchiveType.Zip : ArchiveType.Tar;

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.Tar)]
    [InlineData(ArchiveFormat.TarGz)]
    public void Creates_and_reads_back(ArchiveFormat format)
    {
        var output = new MemoryStream();
        var longName = "deep/" + new string('n', 150) + ".txt"; // past ustar's 100-byte name field
        using (var writer = WriterFactory.Open(output, TypeOf(format), WriterOptionsFor(format)))
        {
            foreach (var (name, content) in CommonFiles)
                writer.Write(name, new MemoryStream(content), DateTime.UtcNow);
            writer.Write(longName, new MemoryStream("long"u8.ToArray()), DateTime.UtcNow);
        }

        output.Position = 0;
        using var reader = ReaderFactory.Open(output, Options());
        var files = ReadForwardOnly(reader);
        AssertCommonTree(files);
        Assert.Equal("long"u8.ToArray(), files[longName]);
    }

    /// <summary>No 7z writer exists in 1.0.0, either way in.</summary>
    [Fact]
    public void Seven_zip_cannot_be_written()
    {
        Assert.Throws<NotSupportedException>(() =>
            WriterFactory.Open(new MemoryStream(), ArchiveType.SevenZip, new WriterOptions(CompressionType.LZMA)));
        Assert.Throws<NotSupportedException>(() => ArchiveFactory.Create(ArchiveType.SevenZip));
    }

    [Fact]
    public void Zip_writer_works_on_a_non_seekable_stream()
    {
        var sink = new MemoryStream();
        using (var forwardOnly = new WriteOnlyStream(sink))
        using (var writer = WriterFactory.Open(forwardOnly, ArchiveType.Zip, new ZipWriterOptions(CompressionType.Deflate)))
        {
            writer.Write("docs/readme.txt", new MemoryStream("hello\n"u8.ToArray()), DateTime.UtcNow);
            writer.Write("docs/sub/data.bin", new MemoryStream(DataBin), DateTime.UtcNow);
        }

        using var archive = ArchiveFactory.Open(new MemoryStream(sink.ToArray()), Options());
        var files = ReadRandomAccess(archive);
        Assert.Equal("hello\n"u8.ToArray(), files["docs/readme.txt"]);
        Assert.Equal(DataBin, files["docs/sub/data.bin"]);
    }

    /// <summary>Directories are written with <see cref="IWriter.WriteDirectory"/>. Given a
    /// <see cref="DateTimeKind.Utc"/> or <see cref="DateTimeKind.Local"/> time, both writers store the right instant
    /// (an unspecified one is not: <see cref="SharpCompressZoneProbeTests"/>).</summary>
    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.Tar)]
    [InlineData(ArchiveFormat.TarGz)]
    public void Writer_stores_an_empty_directory_and_modified_time(ArchiveFormat format)
    {
        var utc = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        var local = new DateTime(2023, 5, 6, 7, 8, 10, DateTimeKind.Local);
        var output = new MemoryStream();
        using (var writer = WriterFactory.Open(output, TypeOf(format), WriterOptionsFor(format)))
        {
            writer.WriteDirectory("empty", utc);
            writer.Write("local.txt", new MemoryStream("l"u8.ToArray()), local);
        }

        output.Position = 0;
        using var reader = ReaderFactory.Open(output, Options());
        var entries = new Dictionary<string, (bool Directory, DateTime? Modified)>();
        while (reader.MoveToNextEntry())
            entries[reader.Entry.Key!] = (reader.Entry.IsDirectory, reader.Entry.LastModifiedTime);

        Assert.Equal((true, utc.ToLocalTime()), entries["empty/"]);
        Assert.Equal(local, entries["local.txt"].Modified);
    }

    // ---- Encryption and damage --------------------------------------------------------------------------------

    [Fact]
    public void Encrypted_zip_lists_without_password_and_marks_entries_encrypted()
    {
        using var archive = OpenArchive("encrypted.zip");

        Assert.Equal(CommonFiles.Keys.Order(), archive.Entries.Select(e => e.Key!).Order());
        Assert.All(archive.Entries, e => Assert.True(e.IsEncrypted));
        using var plain = OpenArchive("sample.zip");
        Assert.All(plain.Entries, e => Assert.False(e.IsEncrypted));
    }

    [Theory]
    [InlineData("encrypted.zip", "secret")]
    [InlineData("encrypted.7z", Fixture.External7zPassword)]
    public void Encrypted_entries_read_with_the_right_password(string fixture, string password)
    {
        using var archive = OpenArchive(fixture, password);

        var files = archive.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var stream = file.OpenEntryStream();
            Assert.Equal(file.Size, ReadToEnd(stream).LongLength);
        }
        if (fixture == "encrypted.zip") AssertCommonTree(ReadRandomAccess(archive));
    }

    /// <summary>Both a missing and a wrong zip password throw <see cref="SharpCompress.Common.CryptographicException"/>
    /// (not the BCL one) when the entry is opened, random access or forward-only alike; only the message differs:
    /// "No password supplied for encrypted zip." and "The password did not match.". The caller knows which case it
    /// is from whether it gave a password.</summary>
    [Fact]
    public void Wrong_or_missing_password_fails_with_a_recognisable_exception()
    {
        foreach (var password in new[] { null, "wrong" })
        {
            using var archive = OpenArchive("encrypted.zip", password);
            var entry = archive.Entries.First(e => e.Key == "docs/readme.txt");
            var random = Assert.Throws<SharpCompress.Common.CryptographicException>(() => entry.OpenEntryStream());

            using var reader = ReaderFactory.Open(new MemoryStream(Fixture.Bytes("encrypted.zip")), Options(password));
            var forward = Assert.Throws<SharpCompress.Common.CryptographicException>(() =>
            {
                while (reader.MoveToNextEntry()) ReadToEnd(reader.OpenEntryStream());
            });

            var message = password is null ? "No password supplied for encrypted zip." : "The password did not match.";
            Assert.Equal(message, random.Message);
            Assert.Equal(message, forward.Message);
        }
    }

    /// <summary>A 7z with encrypted headers cannot even be listed without its password.</summary>
    [Fact]
    public void Header_encrypted_7z_does_not_list_without_password()
    {
        var bytes = Fixture.Bytes("encrypted.7z");

        var error = Record.Exception(() =>
        {
            using var archive = ArchiveFactory.Open(new MemoryStream(bytes), Options());
            _ = archive.Entries.ToList();
        });
        Assert.NotNull(error);

        using var opened = ArchiveFactory.Open(new MemoryStream(bytes), Options(Fixture.External7zPassword));
        Assert.NotEmpty(opened.Entries);
    }

    /// <summary>A zip cut in half: random access fails when listing with <see cref="ArchiveException"/> (no central
    /// directory); forward-only reads the complete entries and then hits the end of the stream.</summary>
    [Fact]
    public void Truncated_zip_fails_to_list()
    {
        using (var archive = OpenArchive("broken.zip"))
            Assert.Throws<ArchiveException>(() => archive.Entries.ToList());

        using var reader = ReaderFactory.Open(new MemoryStream(Fixture.Bytes("broken.zip")), Options());
        Assert.Throws<EndOfStreamException>(() => ReadForwardOnly(reader));
    }

    /// <summary>A stream that can only be written forward: no seeking, no length, no position.</summary>
    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

[CollectionDefinition(nameof(LocalZoneCollection), DisableParallelization = true)]
public sealed class LocalZoneCollection;

/// <summary>Probes whose answer depends on the local time zone. Each runs with the zone switched to a fixed UTC+5
/// with no daylight saving (Asia/Tashkent), alone, since the zone is process-wide.</summary>
[Collection(nameof(LocalZoneCollection))]
public class SharpCompressZoneProbeTests
{
    private static void InZone(string zone, TimeSpan offset, Action action)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The TZ variable picks the local zone on Unix only.");
        var saved = Environment.GetEnvironmentVariable("TZ");
        Environment.SetEnvironmentVariable("TZ", zone);
        TimeZoneInfo.ClearCachedData();
        try
        {
            Assert.Equal(offset, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2022, 3, 4)));
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", saved);
            TimeZoneInfo.ClearCachedData();
        }
    }

    /// <summary>An <see cref="DateTimeKind.Unspecified"/> time is taken as UTC by the zip writer and as local by the
    /// tar writer, so Tandem must always pass a time with a kind.</summary>
    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.Tar)]
    [InlineData(ArchiveFormat.TarGz)]
    public void Writers_disagree_about_an_unspecified_time(ArchiveFormat format) =>
        InZone("Asia/Tashkent", TimeSpan.FromHours(5), () =>
        {
            var unspecified = new DateTime(2022, 3, 4, 5, 6, 8, DateTimeKind.Unspecified);
            var output = new MemoryStream();
            using (var writer = WriterFactory.Open(output, SharpCompressProbeTests.TypeOf(format),
                       SharpCompressProbeTests.WriterOptionsFor(format)))
                writer.Write("unspecified.txt", new MemoryStream("u"u8.ToArray()), unspecified);

            output.Position = 0;
            using var reader = ReaderFactory.Open(output, new ReaderOptions());
            Assert.True(reader.MoveToNextEntry());
            var stored = reader.Entry.LastModifiedTime!.Value.ToUniversalTime();

            var expected = format == ArchiveFormat.Zip
                ? new DateTime(2022, 3, 4, 5, 6, 8, DateTimeKind.Utc) // taken as UTC
                : new DateTime(2022, 3, 4, 0, 6, 8, DateTimeKind.Utc); // taken as UTC+5 local
            Assert.Equal(expected, stored);
        });
}
