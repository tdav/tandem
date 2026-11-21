using System.Text;

namespace Tandem.App.Services;

/// <summary>
/// The arguments of a user command (a button of the button bar), written as one line with placeholders.
/// </summary>
/// <remarks>
/// The line is split into arguments at spaces (and tabs); double quotes join, and inside them <c>\"</c> and <c>\\</c>
/// stand for the characters themselves (elsewhere a backslash is just a backslash); a quote may start mid-argument
/// (<c>--x="a b"</c> is <c>--x=a b</c>). The split comes first, so a substituted path with spaces or quotes stays one
/// argument; nothing is ever given to a shell. Quoting does not change the placeholders: <c>"%s"</c> is still one
/// argument per target. Placeholders:
/// <c>%p</c> the active panel's folder, <c>%t</c> the other panel's folder, <c>%f</c> the full path of the item under
/// the cursor, <c>%n</c> its name, <c>%s</c> every target (the marked items, or the cursor item), <c>%%</c> a percent
/// sign; any other <c>%</c> is kept as it is. An argument that is exactly <c>%s</c> becomes one argument per target;
/// <c>%s</c> inside a longer argument gives the paths joined by spaces. An argument whose <c>%f</c>, <c>%n</c> or
/// <c>%s</c> has nothing to stand for is left out entirely. A line using <c>%t</c> while there is no other folder
/// cannot be used at all (<see cref="OtherFolderUnavailableException"/>): leaving that argument out would shift the
/// others, and <c>cp %s %t</c> would copy one marked file over another.
/// </remarks>
public static class ArgumentTemplate
{
    /// <summary>What the placeholders stand for.</summary>
    /// <param name="OtherPath">The other panel's folder; null when it has none on the local disk.</param>
    /// <param name="CursorPath">The item under the cursor; null on "..", or in an empty folder.</param>
    /// <param name="Targets">The full paths of the items a command acts on.</param>
    public sealed record Context(string CurrentPath, string? OtherPath, string? CursorPath,
        IReadOnlyList<string> Targets)
    {
        /// <summary>The item's name when it has no path on the local disk.</summary>
        public string? CursorName { get; init; }
    }

    /// <summary>The line uses <c>%t</c>, but <see cref="Context.OtherPath"/> is null.</summary>
    public sealed class OtherFolderUnavailableException()
        : InvalidOperationException("The line uses the other panel's folder, and there is none.");

    /// <exception cref="OtherFolderUnavailableException">The line uses <c>%t</c> and there is no other
    /// folder.</exception>
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

    /// <summary>The placeholders this template expands, after resolving quotes and escaped percent signs.</summary>
    public static IReadOnlySet<char> PlaceholdersOf(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var placeholders = new HashSet<char>();
        foreach (var token in Split(template))
        {
            for (var i = 0; i < token.Length; i++)
            {
                if (PlaceholderAt(token, i) is not { } placeholder) continue;
                if (placeholder != '%') placeholders.Add(placeholder);
                i++;
            }
        }
        return placeholders;
    }

    private static char? PlaceholderAt(string token, int index) =>
        token[index] == '%' && index + 1 < token.Length && token[index + 1] is '%' or 'p' or 't' or 'f' or 'n' or 's'
            ? token[index + 1] : null;

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

    /// <summary>The argument with its placeholders replaced; null when one of them has nothing to stand for. Reads
    /// the whole argument even then, so a <c>%t</c> without an other folder always throws.</summary>
    private static string? Substitute(string token, Context context)
    {
        var result = new StringBuilder(token.Length);
        var missing = false;
        for (var i = 0; i < token.Length; i++)
        {
            if (PlaceholderAt(token, i) is not { } placeholder)
            {
                result.Append(token[i]);
                continue;
            }
            var value = placeholder switch
            {
                '%' => "%",
                'p' => context.CurrentPath,
                't' => context.OtherPath ?? throw new OtherFolderUnavailableException(),
                'f' => context.CursorPath,
                'n' => context.CursorName ?? (context.CursorPath is { } path ? NameOf(path) : null),
                's' => context.Targets.Count > 0 ? string.Join(' ', context.Targets) : null,
                _ => throw new InvalidOperationException("Unknown argument placeholder."),
            };
            if (value is null) missing = true;
            else result.Append(value);
            i++;
        }
        return missing ? null : result.ToString();
    }

    private static string NameOf(string path) => Path.GetFileName(path.TrimEnd('/'));
}
