using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Rename;

public static class RenamePlanner
{
    private static readonly StringComparer DiskComparer = StringComparer.OrdinalIgnoreCase;

    public static RenamePlan Build(IReadOnlyList<FileEntry> entries, RenameRule rule,
        IReadOnlyDictionary<VfsPath, IReadOnlySet<string>> namesByFolder, TimeZoneInfo zone)
    {
        if (rule.CounterDigits is < 1 or > 255)
            throw new RenameRuleException("Counter width must be between 1 and 255.", -1);
        var nameTemplate = new RenameTemplate(rule.NameTemplate);
        var extensionTemplate = new RenameTemplate(rule.ExtensionTemplate);
        var replace = Replacement(rule);
        var counterFormat = "D" + rule.CounterDigits.ToString(CultureInfo.InvariantCulture);
        var rows = new RenameItem[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var extension = entry.Extension;
            var baseName = extension.Length > 0 ? entry.Name[..^(extension.Length + 1)] : entry.Name;
            var counter = (rule.CounterStart + (long)i * rule.CounterStep)
                .ToString(counterFormat, CultureInfo.InvariantCulture);
            var parts = new RenameTemplate.Parts(baseName, extension, counter,
                TimeZoneInfo.ConvertTime(entry.Modified, zone));
            var name = nameTemplate.Render(parts);
            var newExtension = extensionTemplate.Render(parts);
            var whole = replace(newExtension.Length == 0 ? name : name + "." + newExtension);
            // A leading dot in an existing name is not an extension separator.
            var extensionIndex = whole.LastIndexOf('.');
            var emptyBase = newExtension.Length != 0 && (name.Length == 0 ||
                extensionIndex == 0 && !entry.Name.StartsWith('.'));
            if (extensionIndex <= 0) extensionIndex = whole.Length;
            whole = ChangeCase(whole[..extensionIndex], rule.Case) + whole[extensionIndex..];
            var status = entry.Path.Parent is null || emptyBase || !IsValidName(whole)
                ? RenameStatus.InvalidName
                : whole == entry.Name ? RenameStatus.Unchanged : RenameStatus.Ok;
            rows[i] = new(entry, whole, status);
        }

        foreach (var group in Enumerable.Range(0, rows.Length).GroupBy(i => rows[i].Entry.Path.Parent))
        {
            if (group.Key is not { } folder) continue;
            var counts = new Dictionary<string, int>(DiskComparer);
            var vacated = new HashSet<string>(DiskComparer);
            foreach (var index in group)
            {
                var row = rows[index];
                if (row.Status == RenameStatus.InvalidName) continue;
                var key = NameKey.Of(row.NewName);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (row.Status == RenameStatus.Ok && !NameKey.SameOnMacDisk(row.Entry.Name, row.NewName))
                    vacated.Add(NameKey.Of(row.Entry.Name));
            }
            var occupied = namesByFolder.TryGetValue(folder, out var names)
                ? names.Select(NameKey.Of).ToHashSet(DiskComparer) : new HashSet<string>(DiskComparer);
            foreach (var index in group)
            {
                var row = rows[index];
                if (row.Status != RenameStatus.Ok) continue;
                var key = NameKey.Of(row.NewName);
                var original = NameKey.Of(row.Entry.Name);
                if (counts[key] > 1)
                    rows[index] = row with { Status = RenameStatus.Duplicate };
                else if (!DiskComparer.Equals(key, original) && occupied.Contains(key) && !vacated.Contains(key))
                    rows[index] = row with { Status = RenameStatus.NameTaken };
            }
        }
        return new(rows);
    }

    internal static bool IsValidName(string name) =>
        name.Length != 0 && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\0')
        && Encoding.UTF8.GetByteCount(name) <= 255;

    private static Func<string, string> Replacement(RenameRule rule)
    {
        if (rule.Search.Length == 0) return name => name;
        if (!rule.UseRegex)
        {
            var comparison = rule.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return name => name.Replace(rule.Search, rule.Replace, comparison);
        }
        try
        {
            var options = RegexOptions.CultureInvariant | (rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            var regex = new Regex(rule.Search, options, TimeSpan.FromSeconds(1));
            return name =>
            {
                try { return regex.Replace(name, rule.Replace); }
                catch (Exception e) when (e is RegexMatchTimeoutException or ArgumentException)
                {
                    throw new RenameRuleException(e.Message, -1);
                }
            };
        }
        catch (ArgumentException e)
        {
            throw new RenameRuleException(e.Message, -1);
        }
    }

    private static string ChangeCase(string name, CaseChange change) => change switch
    {
        CaseChange.Lower => name.ToLowerInvariant(),
        CaseChange.Upper => name.ToUpperInvariant(),
        CaseChange.FirstUpper when name.Length > 0 => FirstUpper(name),
        CaseChange.EachWordUpper => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
        _ => name,
    };

    private static string FirstUpper(string name)
    {
        var first = StringInfo.GetNextTextElement(name);
        return first.ToUpperInvariant() + name[first.Length..].ToLowerInvariant();
    }
}
