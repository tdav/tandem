namespace Tandem.Core.Listing;

/// <summary>The column a listing is sorted by. Saved by name, so values may be added but never renamed.</summary>
public enum SortColumn { Name, Extension, Size, Modified, Mode }

/// <summary>Which column a listing is sorted by, and in which direction.</summary>
public readonly record struct SortSpec(SortColumn Column, bool Descending = false);
