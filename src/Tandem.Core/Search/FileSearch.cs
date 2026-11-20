using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.Core.Search;

/// <summary>
/// Finds entries under a folder by name, size and date. The walk is depth-first in the order the provider lists
/// entries, and every result is given out as soon as it is found. Links are never followed: a link may be a result,
/// but the search does not go into it, so link loops cannot make it spin.
/// </summary>
public sealed class FileSearch(ProviderRegistry providers)
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private const long ProgressIntervalMs = 100;

    /// <summary>Checks that <paramref name="query"/> can be run.</summary>
    /// <exception cref="SearchQueryException">The name regular expression is invalid (<see cref="SearchField.Name"/>);
    /// or the content query is (see <see cref="ContentMatcher(ContentQuery)"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="SearchQuery.MaxDepth"/> is negative.</exception>
    public static void Validate(SearchQuery query) => Prepare(query);

    /// <summary>Checks <paramref name="query"/> as <see cref="Validate"/> does and gives what the check built: the name
    /// regular expression and the content matcher, when the query has them.</summary>
    private static (Regex? Name, ContentMatcher? Content) Prepare(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaxDepth is < 0)
            throw new ArgumentOutOfRangeException(nameof(query), query.MaxDepth, "MaxDepth must not be negative.");
        var name = query.NameIsRegex ? NameRegex(query.NameMasks) : null;
        var content = query.Content is null ? null : new ContentMatcher(query.Content);
        return (name, content);
    }

    /// <summary>Runs <paramref name="query"/>. Contents are read only for plain files (<see cref="EntryKind.File"/>)
    /// that meet every other condition. A folder or such a file that cannot be read (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>) is skipped and counted; <paramref name="progress"/> hears at most
    /// ten times a second while the search runs, and once more when it ends: that final report (with a null
    /// <see cref="SearchProgress.CurrentFolder"/>) is sent whether the search finishes, is cancelled or fails.</summary>
    /// <exception cref="SearchQueryException">See <see cref="Validate"/>.</exception>
    /// <exception cref="IOException">The root cannot be read.</exception>
    public async IAsyncEnumerable<FileEntry> RunAsync(SearchQuery query, IProgress<SearchProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (nameRegex, content) = Prepare(query);
        var filter = new Filter(query, nameRegex);
        var excluded = HasParts(query.ExcludedFolders) ? new FileMask(query.ExcludedFolders) : null;
        var foldersAreResults = query.Content is null && query.MinSize is null && query.MaxSize is null;
        var provider = providers.For(query.Root);

        long foldersScanned = 0, filesChecked = 0, found = 0, foldersSkipped = 0, filesSkipped = 0;
        var lastReport = Environment.TickCount64;
        var stack = new Stack<Folder>();
        stack.Push(new Folder(query.Root, 0));
        try
        {
            while (stack.TryPeek(out var folder))
            {
                ct.ThrowIfCancellationRequested();
                if (progress is not null && Environment.TickCount64 - lastReport >= ProgressIntervalMs)
                {
                    lastReport = Environment.TickCount64;
                    progress.Report(new SearchProgress(foldersScanned, filesChecked, found, foldersSkipped, filesSkipped,
                        providers.DisplayPath(folder.Path)));
                }

                var step = await NextAsync(provider, folder, ct).ConfigureAwait(false);
                if (step is not { } entry)
                {
                    if (folder.Failed) foldersSkipped++;
                    else foldersScanned++;
                    stack.Pop();
                    await folder.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                if (entry.IsHidden && !query.IncludeHidden) continue;
                if (entry.IsDirectoryLike)
                {
                    if (excluded is not null && excluded.IsMatch(entry.Name)) continue;
                    if (foldersAreResults && filter.Matches(entry, isFile: false))
                    {
                        found++;
                        yield return entry;
                    }
                    if (entry.Kind == EntryKind.Directory && (query.MaxDepth is null || folder.Depth < query.MaxDepth))
                        stack.Push(new Folder(entry.Path, folder.Depth + 1));
                }
                else
                {
                    filesChecked++;
                    if (!filter.Matches(entry, isFile: true)) continue;
                    if (content is not null)
                    {
                        if (entry.Kind != EntryKind.File) continue;
                        var matched = await ContentMatchesAsync(provider, entry, content, ct).ConfigureAwait(false);
                        if (matched is null) filesSkipped++;
                        if (matched is not true) continue;
                    }
                    found++;
                    yield return entry;
                }
            }
        }
        finally
        {
            while (stack.TryPop(out var open)) await open.DisposeAsync().ConfigureAwait(false);
            progress?.Report(new SearchProgress(foldersScanned, filesChecked, found, foldersSkipped, filesSkipped, null));
        }
    }

    /// <summary>The folder's next entry; null at its end, or when it cannot be read (then
    /// <see cref="Folder.Failed"/>). The root's read errors are thrown.</summary>
    private static async Task<FileEntry?> NextAsync(IFileSystemProvider provider, Folder folder, CancellationToken ct)
    {
        try
        {
            folder.Entries ??= provider.EnumerateAsync(folder.Path, ct).GetAsyncEnumerator(ct);
            return await folder.Entries.MoveNextAsync().ConfigureAwait(false) ? folder.Entries.Current : null;
        }
        catch (Exception e) when (folder.Depth > 0 && e is IOException or UnauthorizedAccessException)
        {
            folder.Failed = true;
            return null;
        }
    }

    /// <summary>Whether the file's contents match; null when it cannot be opened or read to the end.</summary>
    private static async Task<bool?> ContentMatchesAsync(IFileSystemProvider provider, FileEntry file,
        ContentMatcher content, CancellationToken ct)
    {
        try
        {
            var stream = await provider.OpenReadAsync(file.Path, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                return await content.IsMatchAsync(stream, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasParts(string mask) =>
        mask.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length > 0;

    private static Regex NameRegex(string pattern)
    {
        try
        {
            return new Regex(pattern.Normalize(NormalizationForm.FormC),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException e)
        {
            throw new SearchQueryException(SearchField.Name, e.Message);
        }
    }

    /// <summary>A folder being read, <see cref="Depth"/> levels below the root.</summary>
    private sealed class Folder(VfsPath path, int depth) : IAsyncDisposable
    {
        public VfsPath Path { get; } = path;
        public int Depth { get; } = depth;
        public IAsyncEnumerator<FileEntry>? Entries { get; set; }
        public bool Failed { get; set; }

        public ValueTask DisposeAsync()
        {
            var entries = Entries;
            Entries = null;
            return entries?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }

    /// <summary>The name, size and date parts of a query; <paramref name="regex"/> is the name regular expression
    /// when <see cref="SearchQuery.NameIsRegex"/>.</summary>
    private sealed class Filter(SearchQuery query, Regex? regex)
    {
        private readonly FileMask? _mask = query.NameIsRegex ? null : new FileMask(query.NameMasks);
        private readonly Regex? _regex = regex;

        public bool Matches(FileEntry entry, bool isFile) =>
            (!isFile || (entry.Size >= (query.MinSize ?? long.MinValue) && entry.Size <= (query.MaxSize ?? long.MaxValue))) &&
            (query.ModifiedFrom is not { } from || entry.Modified >= from) &&
            (query.ModifiedTo is not { } to || entry.Modified <= to) &&
            NameMatches(entry.Name);

        private bool NameMatches(string name)
        {
            if (_mask is not null) return _mask.IsMatch(name);
            try
            {
                return _regex!.IsMatch(name.Normalize(NormalizationForm.FormC));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
    }
}
