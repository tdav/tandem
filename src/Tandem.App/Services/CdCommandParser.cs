using System.Text;

namespace Tandem.App.Services;

/// <summary>Why a <c>cd</c> typed in the command line goes nowhere.</summary>
public enum CdProblem
{
    /// <summary><c>cd -</c> (the previous folder) is not supported.</summary>
    PreviousFolderUnsupported,

    /// <summary><c>cd ~user</c> (another user's home) is not supported.</summary>
    UserHomeUnsupported,

    /// <summary>A quote is not closed, or the text ends in a backslash.</summary>
    Unterminated,

    /// <summary>More than one argument.</summary>
    TooManyArguments,
}

/// <summary>A <c>cd</c> typed in the command line: either the absolute, normalized <see cref="Target"/> folder or the
/// <see cref="Problem"/> that stops it. <see cref="Argument"/> is the argument as typed (trimmed).</summary>
public sealed record CdCommand(string? Target, CdProblem? Problem, string Argument);

/// <summary>
/// Recognises <c>cd</c> in the command line, which the app runs itself by moving the active panel; everything else
/// goes to the shell. Only input whose first word (leading and trailing whitespace ignored) is exactly <c>cd</c>
/// counts: <c>cdx</c>, <c>cd.</c> and <c>cd;ls</c> are ordinary commands.
/// <list type="bullet">
/// <item>No argument, <c>~</c> or <c>~/…</c> (unquoted): the home folder.</item>
/// <item>An absolute path as it is; a relative one from the current folder. "." and ".." are resolved, ".." at the
/// root stays at the root.</item>
/// <item>Single quotes keep everything literally; in double quotes a backslash escapes only <c>"</c> and <c>\</c>;
/// outside quotes a backslash escapes the next character (<c>my\ dir</c>). Quoted parts join the word around
/// them.</item>
/// <item>No expansion of variables or globs: <c>$HOME</c> and <c>*</c> are names.</item>
/// <item>Not supported: <c>cd -</c>, <c>cd ~user</c>, more than one argument, an unclosed quote.</item>
/// </list>
/// </summary>
public static class CdCommandParser
{
    /// <param name="text">The command line as typed.</param>
    /// <param name="currentDirectory">The active panel's absolute folder, where relative paths start.</param>
    /// <param name="homeDirectory">The absolute home folder, for no argument and <c>~</c>.</param>
    /// <returns>Null when the text is not a <c>cd</c>.</returns>
    public static CdCommand? Parse(string text, string currentDirectory, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(currentDirectory);
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);

        var trimmed = text.Trim();
        if (trimmed == "cd") return new CdCommand(Normalize(homeDirectory), null, "");
        if (!trimmed.StartsWith("cd", StringComparison.Ordinal) || !char.IsWhiteSpace(trimmed[2])) return null;

        var argument = trimmed[2..].Trim();
        if (Split(argument) is not { } words) return new CdCommand(null, CdProblem.Unterminated, argument);
        if (words.Count == 0) return new CdCommand(Normalize(homeDirectory), null, argument);
        if (words.Count > 1) return new CdCommand(null, CdProblem.TooManyArguments, argument);

        var (word, tildeUnquoted) = words[0];
        if (word == "-") return new CdCommand(null, CdProblem.PreviousFolderUnsupported, argument);
        if (tildeUnquoted)
        {
            if (word != "~" && !word.StartsWith("~/", StringComparison.Ordinal))
                return new CdCommand(null, CdProblem.UserHomeUnsupported, argument);
            word = homeDirectory + "/" + word[1..];
        }

        var path = word.StartsWith('/') ? word : currentDirectory + "/" + word;
        return new CdCommand(Normalize(path), null, argument);
    }

    /// <summary>The words of <paramref name="text"/>, quotes and escapes removed, each with whether it starts with an
    /// unquoted "~"; null when a quote or an escape is not finished.</summary>
    private static List<(string Word, bool TildeUnquoted)>? Split(string text)
    {
        var words = new List<(string, bool)>();
        var word = new StringBuilder();
        var inWord = false;
        var tildeUnquoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (inWord) words.Add((word.ToString(), tildeUnquoted));
                word.Clear();
                inWord = false;
                tildeUnquoted = false;
                continue;
            }

            if (!inWord) tildeUnquoted = c == '~';
            inWord = true;
            switch (c)
            {
                case '\\':
                    if (++i == text.Length) return null;
                    word.Append(text[i]);
                    break;
                case '\'':
                    var close = text.IndexOf('\'', i + 1);
                    if (close < 0) return null;
                    word.Append(text, i + 1, close - i - 1);
                    i = close;
                    break;
                case '"':
                    for (i++; ; i++)
                    {
                        if (i == text.Length) return null;
                        if (text[i] == '"') break;
                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\') i++;
                        word.Append(text[i]);
                    }
                    break;
                default:
                    word.Append(c);
                    break;
            }
        }
        if (inWord) words.Add((word.ToString(), tildeUnquoted));
        return words;
    }

    /// <summary>An absolute path with "." and ".." resolved (".." at the root stays there), repeated and trailing
    /// "/" dropped.</summary>
    private static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }
}
