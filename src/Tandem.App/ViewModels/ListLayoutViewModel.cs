using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.Core.Settings;

namespace Tandem.App.ViewModels;

/// <summary>
/// How one panel's file list is laid out: the font size, the (fixed) row height and the columns after "Name". The
/// column headers and every row use the same grid: cell 0 is "Name", taking the rest of the width; cells 1 to 4 hold
/// the other columns in the order of <see cref="Columns"/>, a hidden column with width 0 (and its cells hidden).
/// </summary>
/// <remarks>UI thread only. Starts with <see cref="AppSettings.DefaultColumns"/> and the default sizes.</remarks>
public sealed partial class ListLayoutViewModel : ObservableObject
{
    private static readonly ColumnId[] AllColumns = Enum.GetValues<ColumnId>();

    public ListLayoutViewModel() => Apply(AppSettings.DefaultColumns, AppSettings.DefaultFontSize,
        AppSettings.DefaultRowHeight);

    [ObservableProperty]
    public partial double FontSize { get; private set; }

    [ObservableProperty]
    public partial double RowHeight { get; private set; }

    /// <summary>Every column but "Name", once each, in the order shown.</summary>
    public IReadOnlyList<ColumnSetting> Columns { get; private set; } = [];

    [ObservableProperty] public partial int ExtensionCell { get; private set; }
    [ObservableProperty] public partial int SizeCell { get; private set; }
    [ObservableProperty] public partial int ModifiedCell { get; private set; }
    [ObservableProperty] public partial int ModeCell { get; private set; }

    [ObservableProperty] public partial bool ExtensionVisible { get; private set; }
    [ObservableProperty] public partial bool SizeVisible { get; private set; }
    [ObservableProperty] public partial bool ModifiedVisible { get; private set; }
    [ObservableProperty] public partial bool ModeVisible { get; private set; }

    [ObservableProperty] public partial GridLength Cell1Width { get; private set; }
    [ObservableProperty] public partial GridLength Cell2Width { get; private set; }
    [ObservableProperty] public partial GridLength Cell3Width { get; private set; }
    [ObservableProperty] public partial GridLength Cell4Width { get; private set; }

    /// <summary>Takes the layout of the settings (already normalized: <paramref name="columns"/> holds every column
    /// once); raises PropertyChanged only for what changed.</summary>
    public void Apply(IReadOnlyList<ColumnSetting> columns, double fontSize, double rowHeight)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count != AllColumns.Length || AllColumns.Any(id => columns.Count(c => c?.Column == id) != 1))
            throw new ArgumentException("Every column must be listed once.", nameof(columns));

        FontSize = fontSize;
        RowHeight = rowHeight;
        if (!Columns.SequenceEqual(columns))
        {
            Columns = columns.ToArray();
            OnPropertyChanged(nameof(Columns));
        }

        int CellOf(ColumnId id) => 1 + Columns.ToList().FindIndex(c => c.Column == id);
        bool VisibleOf(ColumnId id) => Columns.First(c => c.Column == id).Visible;
        ExtensionCell = CellOf(ColumnId.Extension);
        SizeCell = CellOf(ColumnId.Size);
        ModifiedCell = CellOf(ColumnId.Modified);
        ModeCell = CellOf(ColumnId.Mode);
        ExtensionVisible = VisibleOf(ColumnId.Extension);
        SizeVisible = VisibleOf(ColumnId.Size);
        ModifiedVisible = VisibleOf(ColumnId.Modified);
        ModeVisible = VisibleOf(ColumnId.Mode);

        GridLength WidthOf(int index) => new(Columns[index].Visible ? Columns[index].Width : 0);
        Cell1Width = WidthOf(0);
        Cell2Width = WidthOf(1);
        Cell3Width = WidthOf(2);
        Cell4Width = WidthOf(3);
    }
}
