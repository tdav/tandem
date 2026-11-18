namespace Tandem.Core.Vfs;

/// <summary>
/// An absolute path inside a file-system provider. The path is normalized on construction:
/// '/' separators, repeated '/' collapsed, no trailing '/' except for the root "/".
/// </summary>
public readonly record struct VfsPath(string ProviderId, string Path)
{
    public const string LocalProviderId = "local";

    public string Path { get; init => field = Normalize(value); } = Normalize(Path);

    public static VfsPath Local(string path) => new(LocalProviderId, path);

    /// <summary>The last segment; "" for the root.</summary>
    public string Name => IsRoot ? "" : Path[(Path.LastIndexOf('/') + 1)..];

    public bool IsRoot => Path == "/";

    /// <summary>The containing directory; null for the root.</summary>
    public VfsPath? Parent
    {
        get
        {
            if (IsRoot) return null;
            var lastSlash = Path.LastIndexOf('/');
            return this with { Path = lastSlash == 0 ? "/" : Path[..lastSlash] };
        }
    }

    /// <summary>Appends a single path segment.</summary>
    public VfsPath Combine(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name == "." || name == ".." || name.Contains('/'))
            throw new ArgumentException($"'{name}' is not a single path segment.", nameof(name));
        return this with { Path = IsRoot ? "/" + name : Path + "/" + name };
    }

    /// <summary>True when this path is <paramref name="other"/> or lies inside it (same provider, whole segments).</summary>
    public bool IsSameOrDescendantOf(VfsPath other) =>
        string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal) &&
        (string.Equals(Path, other.Path, StringComparison.Ordinal) ||
         other.IsRoot ||
         Path.StartsWith(other.Path + "/", StringComparison.Ordinal));

    private static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('/'))
            throw new ArgumentException($"Path must be absolute (start with '/'): '{path}'.", nameof(path));

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segments);
    }
}
