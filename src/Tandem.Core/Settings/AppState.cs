using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.Core.Settings;

/// <summary>One tab of a panel: where it points and how its listing is sorted.</summary>
public sealed record TabState(string ProviderId, string Path, SortColumn SortColumn, bool SortDescending, bool Pinned);

/// <summary>The tabs of one panel; <see cref="ActiveTab"/> is an index into <see cref="Tabs"/>.</summary>
public sealed record PanelState(IReadOnlyList<TabState> Tabs, int ActiveTab);

/// <summary>Which optional parts of the main window are shown. A file written before <see cref="ButtonBar"/> existed
/// reads it as shown.</summary>
public sealed record ViewState(bool Sidebar = false, bool VolumeBars = true, bool CommandLine = true, bool FKeyBar = true,
    bool ButtonBar = true);

/// <summary>Main window placement; <see cref="SplitRatio"/> is the left panel's share of the width.</summary>
public sealed record WindowState(double X, double Y, double Width, double Height, bool Maximized, double SplitRatio);

/// <summary>Everything the app restores at startup. <see cref="Window"/> and <see cref="Language"/> are null when unknown.</summary>
public sealed record AppState(int Version, PanelState Left, PanelState Right, bool LeftIsActive,
    ViewState View, WindowState? Window, IReadOnlyList<string> Favorites, bool ShowHidden, string? Language)
{
    public const int CurrentVersion = 1;

    /// <summary>A first-run state: one tab on the home folder in each panel, sorted by name.</summary>
    public static AppState Default(string homeDirectory)
    {
        var panel = new PanelState([new TabState(VfsPath.LocalProviderId, homeDirectory, SortColumn.Name, false, false)], 0);
        return new AppState(CurrentVersion, panel, panel, LeftIsActive: true, new ViewState(), Window: null,
            Favorites: [], ShowHidden: false, Language: null);
    }
}
