namespace Tandem.Archives;

/// <summary>Which names the archive writers keep as given. The zip and tar writers cut a name up to its first ':'
/// and read '\' as a folder separator, so "Report 11:21.txt" would be stored as "21.txt" and "back\slash.txt" as a
/// file in a folder "back". A name with either character is never written: packing leaves the item out (in the
/// summary), editing a zip refuses the change, and a zip already holding such a name cannot be changed.</summary>
public static class ArchiveEntryNames
{
    private static readonly char[] Altered = [':', '\\'];

    /// <summary>Whether one name (a segment of a path, without '/') is stored as it is.</summary>
    public static bool IsStorable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length > 0 && name.IndexOfAny(Altered) < 0;
    }
}
