using Tandem.Core.Vfs;

namespace Tandem.Core.Search;

/// <summary>What to look for inside files.</summary>
/// <param name="EncodingId">The text encoding to read files in; null to detect it per file.</param>
public sealed record ContentQuery(string Text, bool IsRegex = false, bool CaseSensitive = false, string? EncodingId = null);

/// <summary>A search under <paramref name="Root"/> (never a result itself).</summary>
/// <param name="NameMasks">A <see cref="Listing.FileMask"/> ("*.txt;*.md"; empty matches every name), or a regular
/// expression when <paramref name="NameIsRegex"/>.</param>
/// <param name="MinSize">Smallest file size in bytes, inclusive. Any size filter leaves folders out of the results.</param>
/// <param name="MaxSize">Largest file size in bytes, inclusive.</param>
/// <param name="ModifiedFrom">Earliest modification time, inclusive.</param>
/// <param name="ModifiedTo">Latest modification time, inclusive.</param>
/// <param name="MaxDepth">0 searches only the entries of <paramref name="Root"/>; n goes at most n folder levels below
/// it; null has no limit.</param>
/// <param name="ExcludedFolders">A <see cref="Listing.FileMask"/> of folder names that are neither results nor
/// searched; empty excludes nothing.</param>
/// <param name="IncludeHidden">Whether hidden entries are results and hidden folders are searched.</param>
public sealed record SearchQuery(VfsPath Root, string NameMasks = "", bool NameIsRegex = false, ContentQuery? Content = null,
    long? MinSize = null, long? MaxSize = null, DateTimeOffset? ModifiedFrom = null, DateTimeOffset? ModifiedTo = null,
    int? MaxDepth = null, string ExcludedFolders = "", bool IncludeHidden = false);

/// <summary>How far a search has got.</summary>
/// <param name="FoldersScanned">Folders read to the end, the root included.</param>
/// <param name="FilesChecked">Files (entries that are not folders) compared with the query.</param>
/// <param name="Found">Results given out.</param>
/// <param name="FoldersSkipped">Folders that could not be read.</param>
/// <param name="FilesSkipped">Files that could not be read.</param>
/// <param name="CurrentFolder">The display path of the folder being read; null in the final report.</param>
public sealed record SearchProgress(long FoldersScanned, long FilesChecked, long Found, long FoldersSkipped, long FilesSkipped,
    string? CurrentFolder);

/// <summary>The part of a <see cref="SearchQuery"/> a <see cref="SearchQueryException"/> is about.</summary>
public enum SearchField { Name, Content, Encoding }

/// <summary>A <see cref="SearchQuery"/> that cannot be run; <see cref="Field"/> says which part is wrong.</summary>
public sealed class SearchQueryException(SearchField field, string message) : Exception(message)
{
    public SearchField Field { get; } = field;
}
