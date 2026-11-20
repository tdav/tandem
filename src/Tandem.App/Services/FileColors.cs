using Tandem.Core.Listing;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>
/// The user's colour rules: which palette colour a file's row is shown in. Rules are tried in order and the first
/// whose mask matches the name wins (matching ignores case and Unicode normalization, as <see cref="FileMask"/>
/// does). Folders and links to folders are never coloured. Immutable; a change of rules makes a new one.
/// </summary>
public sealed class FileColors
{
    private const string KeyPrefix = "Tandem.FileColor.";

    private readonly (FileMask Mask, string Color)[] _rules;

    public FileColors(IReadOnlyList<FileColorRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.Select(r => (new FileMask(r.Mask), r.Color)).ToArray();
    }

    /// <summary>The colour name (one of <see cref="AppSettings.ColorNames"/>) of the first rule matching the entry's
    /// name; null when none matches or the entry is folder-like.</summary>
    public string? ColorNameFor(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectoryLike) return null;
        foreach (var (mask, color) in _rules)
            if (mask.IsMatch(entry.Name))
                return color;
        return null;
    }

    /// <summary>The theme resource (a brush with a light and a dark variant in App.axaml) of a palette colour.</summary>
    public static string ResourceKey(string colorName) => KeyPrefix + colorName;
}
