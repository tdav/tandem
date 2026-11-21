using System.Globalization;
using System.Text;

namespace Tandem.Core.Tools.Rename;

internal sealed class RenameTemplate
{
    private readonly List<Func<Parts, string>> _segments = [];

    internal readonly record struct Parts(string Name, string Extension, string Counter, DateTimeOffset Date);

    public RenameTemplate(string template)
    {
        for (var start = 0; start < template.Length;)
        {
            var opening = template.IndexOf('[', start);
            if (opening < 0)
            {
                AddLiteral(template[start..]);
                break;
            }
            if (opening > start) AddLiteral(template[start..opening]);
            var end = template.IndexOf(']', opening + 1);
            if (end < 0) throw new RenameRuleException("Unclosed template token.", opening);
            var token = template[(opening + 1)..end];
            _segments.Add(Token(token, opening));
            start = end + 1;
        }
    }

    public string Render(Parts parts)
    {
        var text = new StringBuilder();
        foreach (var segment in _segments) text.Append(segment(parts));
        return text.ToString();
    }

    private void AddLiteral(string literal) => _segments.Add(_ => literal);

    private static Func<Parts, string> Token(string token, int position) => token switch
    {
        "[" => _ => "[",
        "N" => parts => parts.Name,
        "E" => parts => parts.Extension,
        "C" => parts => parts.Counter,
        "Y" => DateToken("yyyy"),
        "M" => DateToken("MM"),
        "D" => DateToken("dd"),
        "h" => DateToken("HH"),
        "m" => DateToken("mm"),
        "s" => DateToken("ss"),
        _ when token.StartsWith('N') => RangeToken(token.AsSpan(1), position),
        _ => throw new RenameRuleException("Unknown template token.", position),
    };

    private static Func<Parts, string> DateToken(string format) =>
        parts => parts.Date.ToString(format, CultureInfo.InvariantCulture);

    private static Func<Parts, string> RangeToken(ReadOnlySpan<char> range, int position)
    {
        var separator = range.IndexOf('-');
        if (separator < 1 ||
            !int.TryParse(range[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
            first < 1)
            throw new RenameRuleException("Invalid name range.", position);
        var last = int.MaxValue;
        var tail = range[(separator + 1)..];
        if (!tail.IsEmpty &&
            (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first))
            throw new RenameRuleException("Invalid name range.", position);

        return parts =>
        {
            var indexes = StringInfo.ParseCombiningCharacters(parts.Name);
            if (first > indexes.Length) return "";
            var end = last < indexes.Length ? indexes[last] : parts.Name.Length;
            return parts.Name[indexes[first - 1]..end];
        };
    }
}
