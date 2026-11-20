using SharpCompress.Common;
using SharpCompress.Readers;

namespace Tandem.Archives;

/// <summary>An archive format Tandem reads.</summary>
public enum ArchiveFormat { Zip, Tar, TarGz, TarBz2, TarXz, SevenZip, Rar }

/// <summary>Archive formats by file name and by content, and what can be done with each.</summary>
public static class ArchiveFormats
{
    /// <summary>File name endings, longest first so ".tar.gz" wins over a shorter match.</summary>
    private static readonly (string Suffix, ArchiveFormat Format)[] Suffixes =
    [
        (".tar.bz2", ArchiveFormat.TarBz2), (".tar.gz", ArchiveFormat.TarGz), (".tar.xz", ArchiveFormat.TarXz),
        (".tbz2", ArchiveFormat.TarBz2), (".tgz", ArchiveFormat.TarGz), (".txz", ArchiveFormat.TarXz),
        (".zip", ArchiveFormat.Zip), (".tar", ArchiveFormat.Tar), (".rar", ArchiveFormat.Rar),
        (".7z", ArchiveFormat.SevenZip),
    ];

    /// <summary>Bytes read to recognise a format: enough for the tar magic at offset 257.</summary>
    private const int HeaderSize = 512;

    private const int TarMagicOffset = 257;

    /// <summary>The format a file name says (.zip .tar .tar.gz .tgz .tar.bz2 .tbz2 .tar.xz .txz .7z .rar, any
    /// case); null for any other name.</summary>
    public static ArchiveFormat? FromName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        foreach (var (suffix, format) in Suffixes)
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return format;
        return null;
    }

    /// <summary>The name without the archive ending <see cref="FromName"/> recognises ("site.tar.gz" → "site"); a
    /// name with no such ending as it is.</summary>
    public static string NameWithoutSuffix(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        foreach (var (suffix, _) in Suffixes)
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
        return fileName;
    }

    /// <summary>The format the content of <paramref name="seekable"/> has, by its signature; null when it is none
    /// of them. A gzip, bzip2 or xz stream counts only if a tar is inside. The stream is left at its start.</summary>
    public static async Task<ArchiveFormat?> DetectAsync(Stream seekable, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        if (!seekable.CanSeek) throw new ArgumentException("The stream must be seekable.", nameof(seekable));
        try
        {
            seekable.Position = 0;
            var header = new byte[HeaderSize];
            var length = await seekable.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);
            var head = header.AsSpan(0, length);
            if (FromSignature(head) is not { } format) return null;
            if (format is ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz)
            {
                seekable.Position = 0;
                return HoldsTar(seekable) ? format : null;
            }
            return format;
        }
        finally
        {
            seekable.Position = 0;
        }
    }

    private static ArchiveFormat? FromSignature(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("PK\u0003\u0004"u8) || head.StartsWith("PK\u0005\u0006"u8) ||
            head.StartsWith("PK\u0007\u0008"u8))
            return ArchiveFormat.Zip;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return ArchiveFormat.SevenZip;
        if (head.StartsWith("Rar!\u001A\u0007"u8)) return ArchiveFormat.Rar;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B])) return ArchiveFormat.TarGz;
        if (head.StartsWith("BZh"u8)) return ArchiveFormat.TarBz2;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00])) return ArchiveFormat.TarXz;
        if (head.Length >= TarMagicOffset + 5 && head.Slice(TarMagicOffset, 5).SequenceEqual("ustar"u8))
            return ArchiveFormat.Tar;
        return null;
    }

    /// <summary>Whether the compressed stream decompresses to a tar (the reader looks through the compression).
    /// </summary>
    private static bool HoldsTar(Stream compressed)
    {
        try
        {
            using var reader = ReaderFactory.Open(compressed, new ReaderOptions { LeaveStreamOpen = true });
            return reader.ArchiveType == ArchiveType.Tar;
        }
        catch (Exception e) when (e is SharpCompressException or InvalidOperationException or IOException
                                      or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The usual extension, without the dot: "zip", "tar", "tar.gz", "tar.bz2", "tar.xz", "7z", "rar".
    /// </summary>
    public static string DefaultExtension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "zip",
        ArchiveFormat.Tar => "tar",
        ArchiveFormat.TarGz => "tar.gz",
        ArchiveFormat.TarBz2 => "tar.bz2",
        ArchiveFormat.TarXz => "tar.xz",
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Rar => "rar",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The formats a new archive can be packed into. SharpCompress 1.0.0 has no 7z writer, so 7z is not
    /// among them.</summary>
    public static IReadOnlyList<ArchiveFormat> Creatable { get; } =
        [ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.TarGz];

    /// <summary>Whether an existing archive of this format can be changed in place: only zip.</summary>
    public static bool CanEdit(ArchiveFormat format) => format == ArchiveFormat.Zip;
}
