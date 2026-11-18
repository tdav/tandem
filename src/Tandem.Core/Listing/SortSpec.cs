namespace Tandem.Core.Listing;

public enum SortColumn { Name, Extension, Size, Modified }

/// <summary>Which column a listing is sorted by, and in which direction.</summary>
public readonly record struct SortSpec(SortColumn Column, bool Descending = false);
