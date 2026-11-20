using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>An entry as the archive library lists it, before Tandem makes a tree of it.</summary>
/// <param name="Key">The name as stored, separators and all.</param>
/// <param name="LinkTarget">For a symbolic link, where it points; null otherwise.</param>
internal sealed record RawEntry(string Key, bool IsDirectory, long Size, DateTimeOffset? Modified, string? LinkTarget,
    bool IsEncrypted);

/// <summary>One file, folder or link of an archive's tree.</summary>
/// <param name="Path">The path inside the archive, "/docs/readme.txt"; "/" for the root.</param>
/// <param name="EntryKey">The library's key of the entry this node shows; null for a folder no entry
/// names (the root, or a parent the archive leaves out).</param>
/// <param name="KeyOccurrence">Which of the entries with <paramref name="EntryKey"/> it is, counting from 0 in the
/// archive's order: an archive may hold one key twice.</param>
internal sealed record ArchiveNode(string Path, EntryKind Kind, long Size, DateTimeOffset Modified,
    string? LinkTarget, bool IsEncrypted, string? EntryKey, int KeyOccurrence)
{
    public string Name => Path == "/" ? "" : Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// The tree of an archive, built from its entries in their order so that no name can lead out of the archive's root
/// or through a file or a link:
/// <list type="bullet">
/// <item>'\' is a separator like '/'; empty, "." and ".." segments are dropped, so "../../x" becomes "/x" and an
/// absolute name starts at the archive's root;</item>
/// <item>NUL in a name becomes '_';</item>
/// <item>an entry left without a name is dropped, and so is an entry below a file or a link;</item>
/// <item>a name met again gets a number, "name (2).ext" (a folder met again is the same folder);</item>
/// <item>missing parent folders are made, with the archive's time.</item>
/// </list>
/// Immutable once built, so it may be read from any thread.
/// </summary>
internal sealed class ArchiveIndex
{
    private const string Root = "/";

    private readonly Dictionary<string, ArchiveNode> _nodes;
    private readonly Dictionary<string, ArchiveNode[]> _children;

    private ArchiveIndex(Dictionary<string, ArchiveNode> nodes, Dictionary<string, ArchiveNode[]> children,
        int renamedOrDropped)
    {
        _nodes = nodes;
        _children = children;
        RenamedOrDroppedCount = renamedOrDropped;
    }

    /// <summary>How many entries were left out (no name, below a file or link) or shown under another name than they
    /// are stored with (numbered, NUL replaced, ".." dropped).</summary>
    public int RenamedOrDroppedCount { get; }

    /// <summary>Every node except the root.</summary>
    public int Count => _nodes.Count - 1;

    public static ArchiveIndex Build(IEnumerable<RawEntry> entries, DateTimeOffset archiveTime)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new Builder(archiveTime);
        foreach (var entry in entries) builder.Add(entry);
        return builder.ToIndex();
    }

    /// <summary>The node at <paramref name="path"/> (normalized, as in <see cref="VfsPath.Path"/>); null if
    /// none.</summary>
    public ArchiveNode? Find(string path) => _nodes.GetValueOrDefault(path);

    /// <summary>The children of the folder at <paramref name="path"/>, in the archive's order; null when there is no
    /// folder there.</summary>
    public IReadOnlyList<ArchiveNode>? ChildrenOf(string path) => _children.GetValueOrDefault(path);

    private static string Join(string parent, string name) => parent == Root ? Root + name : parent + "/" + name;

    private sealed class Builder(DateTimeOffset archiveTime)
    {
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal)
        {
            [Root] = new Node(Root, EntryKind.Directory, 0, archiveTime, null, false, null, 0),
        };
        private readonly Dictionary<string, int> _occurrences = new(StringComparer.Ordinal);
        private int _renamedOrDropped;

        public void Add(RawEntry entry)
        {
            var key = entry.Key ?? "";
            var occurrence = _occurrences.GetValueOrDefault(key);
            _occurrences[key] = occurrence + 1;

            var renamed = false;
            var segments = new List<string>();
            foreach (var segment in key.Split('/', '\\'))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    renamed = true;
                    continue;
                }
                if (segment.Contains('\0'))
                {
                    renamed = true;
                    segments.Add(segment.Replace('\0', '_'));
                    continue;
                }
                segments.Add(segment);
            }
            if (segments.Count == 0)
            {
                _renamedOrDropped++;
                return;
            }

            var parent = _nodes[Root];
            for (var i = 0; i < segments.Count - 1; i++)
            {
                var path = Join(parent.Path, segments[i]);
                if (!_nodes.TryGetValue(path, out var next))
                {
                    next = new Node(path, EntryKind.Directory, 0, archiveTime, null, false, null, 0);
                    _nodes[path] = next;
                    parent.Children.Add(next);
                }
                else if (next.Kind != EntryKind.Directory)
                {
                    _renamedOrDropped++; // below a file or a link: it could lead anywhere the link does
                    return;
                }
                parent = next;
            }

            var kind = entry.LinkTarget is not null ? EntryKind.Symlink
                : entry.IsDirectory ? EntryKind.Directory
                : EntryKind.File;
            var name = segments[^1];
            if (_nodes.TryGetValue(Join(parent.Path, name), out var existing))
            {
                if (kind == EntryKind.Directory && existing.Kind == EntryKind.Directory)
                {
                    if (existing.EntryKey is null)
                    {
                        existing.EntryKey = key;
                        existing.KeyOccurrence = occurrence;
                        existing.Modified = entry.Modified ?? existing.Modified;
                    }
                    if (renamed) _renamedOrDropped++;
                    return;
                }
                name = FreeName(parent.Path, name);
                renamed = true;
            }

            var node = new Node(Join(parent.Path, name), kind, kind == EntryKind.File ? entry.Size : 0,
                entry.Modified ?? archiveTime, entry.LinkTarget, entry.IsEncrypted, key, occurrence);
            _nodes[node.Path] = node;
            parent.Children.Add(node);
            if (renamed) _renamedOrDropped++;
        }

        public ArchiveIndex ToIndex()
        {
            var nodes = new Dictionary<string, ArchiveNode>(_nodes.Count, StringComparer.Ordinal);
            foreach (var (path, node) in _nodes) nodes[path] = node.Freeze();
            var children = new Dictionary<string, ArchiveNode[]>(StringComparer.Ordinal);
            foreach (var (path, node) in _nodes)
                if (node.Kind == EntryKind.Directory)
                    children[path] = node.Children.Select(c => nodes[c.Path]).ToArray();
            return new ArchiveIndex(nodes, children, _renamedOrDropped);
        }

        /// <summary>"name (2).ext", "name (3).ext"… — the first that is not taken in the folder.</summary>
        private string FreeName(string parent, string name)
        {
            var dot = name.LastIndexOf('.');
            var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
            for (var n = 2; ; n++)
            {
                var candidate = $"{stem} ({n}){extension}";
                if (!_nodes.ContainsKey(Join(parent, candidate))) return candidate;
            }
        }
    }

    private sealed class Node(string path, EntryKind kind, long size, DateTimeOffset modified, string? linkTarget,
        bool isEncrypted, string? entryKey, int keyOccurrence)
    {
        public string Path { get; } = path;
        public EntryKind Kind { get; } = kind;
        public DateTimeOffset Modified { get; set; } = modified;
        public string? EntryKey { get; set; } = entryKey;
        public int KeyOccurrence { get; set; } = keyOccurrence;
        public List<Node> Children { get; } = [];

        public ArchiveNode Freeze() =>
            new(Path, Kind, size, Modified, linkTarget, isEncrypted, EntryKey, KeyOccurrence);
    }
}
