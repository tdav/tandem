using Avalonia.Controls;

namespace Tandem.App.Views;

/// <summary>The file list's column widths and row height, shared by the column headers and the rows so they line
/// up. The name column takes the rest of the width.</summary>
public static class FileListColumns
{
    public const double RowHeightValue = 22;

    /// <summary>Horizontal padding inside a row, which the headers repeat.</summary>
    public const double RowPaddingValue = 6;

    public static readonly GridLength Name = new(1, GridUnitType.Star);
    public static readonly GridLength Extension = new(56);
    public static readonly GridLength Size = new(84);
    public static readonly GridLength Date = new(124);
    public static readonly GridLength Mode = new(84);

    public static readonly double RowHeight = RowHeightValue;
}
