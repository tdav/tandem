using System.Text;

namespace Tandem.App.Services;

/// <summary>
/// The arguments of a user command (a button of the button bar), written as one line with placeholders.
/// </summary>
/// <remarks>
/// The line is split into arguments at spaces (and tabs); double quotes join, and inside them <c>\"</c> and <c>\\</c>
/// stand for the characters themselves (elsewhere a backslash is just a backslash). The split comes first, so a
/// substituted path with spaces or quotes stays one argument; nothing is ever given to a shell. Placeholders:
/// <c>%p</c> the active panel's folder, <c>%t</c> the other panel's folder, <c>%f</c> the full path of the item under
/// the cursor, <c>%n</c> its name, <c>%s</c> every target (the marked items, or the cursor item), <c>%%</c> a percent
/// sign; any other <c>%</c> is kept as it is. An argument that is exactly <c>%s</c> becomes one argument per target;
/// <c>%s</c> inside a longer argument gives the paths joined by spaces. An argument whose <c>%f</c>, <c>%n</c>,
/// <c>%s</c> (or <c>%t</c>, with no other folder) has nothing to stand for is left out entirely.
/// </remarks>
public static class ArgumentTemplate
{
    /// <summary>What the placeholders stand for.</summary>
    /// <param name="OtherPath">The other panel's folder; empty when it has none on the local disk.</param>
    /// <param name="CursorPath">The item under the cursor; null on "..", or in an empty folder.</param>
    /// <param name="Targets">The full paths of the items a command acts on.</param>
    public sealed record Context(string CurrentPath, string OtherPath, string? CursorPath,
        IReadOnlyList<string> Targets);

    public static IReadOnlyList<string> Expand(string template, Context context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        var arguments = new List<string>();
        foreach (var token in Split(template))
        {
            if (token == "%s")
                arguments.AddRange(context.Targets);
            else if (Substitute(token, context) is { } argument)
                arguments.Add(argument);
        }
        return arguments;
    }

    /// <summary>The arguments of the line before substitution: quotes and their escapes resolved.</summary>
    private static List<string> Split(string template)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false; // a token has begun, though it may still be empty ("")
        var quoted = false;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (quoted)
            {
                if (c == '"')
                    quoted = false;
                else if (c == '\\' && i + 1 < template.Length && template[i + 1] is '"' or '\\')
                    current.Append(template[++i]);
                else
                    current.Append(c);
            }
            else if (c is ' ' or '\t')
            {
                if (!inToken) continue;
                tokens.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                inToken = true;
                if (c == '"') quoted = true;
                else current.Append(c);
            }
        }
        if (inToken) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>The argument with its placeholders replaced; null when one of them has nothing to stand for.</summary>
    private static string? Substitute(string token, Context context)
    {
        var result = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '%' || i + 1 == token.Length)
            {
                result.Append(token[i]);
                continue;
            }
            string? value;
            switch (token[i + 1])
            {
                case '%': value = "%"; break;
                case 'p': value = context.CurrentPath; break;
                case 't': value = context.OtherPath.Length > 0 ? context.OtherPath : null; break;
                case 'f': value = context.CursorPath; break;
                case 'n': value = context.CursorPath is { } path ? NameOf(path) : null; break;
                case 's': value = context.Targets.Count > 0 ? string.Join(' ', context.Targets) : null; break;
                default:
                    result.Append('%');
                    continue;
            }
            if (value is null) return null;
            result.Append(value);
            i++;
        }
        return result.ToString();
    }

    private static string NameOf(string path) => Path.GetFileName(path.TrimEnd('/'));
}
