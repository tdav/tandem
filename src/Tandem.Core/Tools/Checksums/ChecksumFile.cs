using System.Text;

namespace Tandem.Core.Tools.Checksums;

public sealed record ChecksumLine(string RelativePath, string Hash);

public sealed record ChecksumFileContent(IReadOnlyList<ChecksumLine> Lines, IReadOnlyList<int> UnreadableLineNumbers);

public static class ChecksumFile
{
    public const long MaxBytes = 16 * 1024 * 1024;

    public static string Format(ChecksumAlgorithm algorithm, IReadOnlyList<ChecksumLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (algorithm == ChecksumAlgorithm.Crc32)
                builder.Append(line.RelativePath).Append(' ').Append(line.Hash.ToUpperInvariant());
            else
                builder.Append(line.Hash.ToLowerInvariant()).Append(" *").Append(line.RelativePath);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    public static ChecksumFileContent Parse(ChecksumAlgorithm algorithm, string text)
    {
        var lines = new List<ChecksumLine>();
        var unreadable = new List<int>();
        var digits = Checksum.HexLength(algorithm);
        using var reader = new StringReader(text);
        for (var number = 1; reader.ReadLine() is { } line; number++)
        {
            var significant = line.AsSpan().TrimStart();
            if (significant.IsEmpty || significant[0] is ';' or '#') continue;
            if (TryParse(algorithm, line.AsSpan(), digits) is { } parsed)
                lines.Add(parsed);
            else
                unreadable.Add(number);
        }
        return new(lines, unreadable);
    }

    private static ChecksumLine? TryParse(ChecksumAlgorithm algorithm, ReadOnlySpan<char> line, int digits)
    {
        var open = line.IndexOf(" (", StringComparison.Ordinal);
        var close = line.LastIndexOf(") =", StringComparison.Ordinal);
        if (open > 0 && close > open + 2 && IsLabel(algorithm, line[..open]))
            return Line(line[(open + 2)..close], line[(close + 3)..].Trim(), digits);

        if (line.Length <= digits + 1) return null;
        if (line[digits] == ' ' && line[digits + 1] is ' ' or '*'
            && Line(line[(digits + 2)..], line[..digits], digits) is { } sum)
            return sum;

        if (algorithm != ChecksumAlgorithm.Crc32) return null;
        var trimmed = line.TrimEnd();
        var hashStart = trimmed.Length - digits;
        return hashStart > 0 && char.IsWhiteSpace(trimmed[hashStart - 1])
            ? Line(trimmed[..(hashStart - 1)], trimmed[hashStart..], digits) : null;
    }

    private static bool IsLabel(ChecksumAlgorithm algorithm, ReadOnlySpan<char> label) =>
        label.Equals(algorithm.ToString(), StringComparison.OrdinalIgnoreCase)
        || algorithm == ChecksumAlgorithm.Sha1 && label.Equals("SHA-1", StringComparison.OrdinalIgnoreCase)
        || algorithm == ChecksumAlgorithm.Sha256 && label.Equals("SHA-256", StringComparison.OrdinalIgnoreCase);

    private static ChecksumLine? Line(ReadOnlySpan<char> name, ReadOnlySpan<char> hash, int digits)
    {
        if (name.IsEmpty || hash.Length != digits) return null;
        foreach (var digit in hash)
            if (!char.IsAsciiHexDigit(digit)) return null;
        return new(name.ToString().Replace('\\', '/'), hash.ToString().ToLowerInvariant());
    }
}
