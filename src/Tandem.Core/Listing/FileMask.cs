namespace Tandem.Core.Listing;

/// <summary>
/// A file-name mask such as <c>*.txt;*.md</c>: parts separated by <c>;</c>, each matching the whole name,
/// with <c>*</c> (any run of characters) and <c>?</c> (exactly one character). Matching ignores case and
/// Unicode normalization form. An empty mask, and the exact part <c>*.*</c>, match every name.
/// </summary>
public sealed class FileMask
{
    private readonly string[] _parts;

    public FileMask(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _parts = pattern
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part == "*.*" ? "*" : EntrySorter.Fold(part)) // "*.*" means "all", dot or not
            .ToArray();
    }

    public bool IsMatch(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_parts.Length == 0) return true;
        var folded = EntrySorter.Fold(name);
        foreach (var part in _parts)
            if (Matches(part, folded))
                return true;
        return false;
    }

    // Greedy wildcard matching with backtracking to the most recent '*': O(pattern * name) worst case.
    private static bool Matches(string pattern, string name)
    {
        int p = 0, n = 0, starP = -1, starN = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starN = n;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                n = ++starN;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
