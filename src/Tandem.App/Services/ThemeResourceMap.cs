namespace Tandem.App.Services;

/// <summary>
/// Which resource key stands on which design system colour token: the views' own keys, Fluent's control brushes and
/// colours, and the file list icons. <see cref="ThemeService"/> writes them from the applied theme into both theme
/// variants. Keys with no token (marked files, errors, drag and drop, the comparison, file colours, hidden icons)
/// stay in App.axaml.
/// </summary>
internal static class ThemeResourceMap
{
    /// <summary>The views' semantic brush keys and their token.</summary>
    public static readonly IReadOnlyList<(string Key, string Token)> Semantic =
    [
        ("Tandem.BarBackground", "bar"),
        ("Tandem.HeaderBackground", "surface-raised"),
        ("Tandem.DividerBrush", "line"),
        ("Tandem.SecondaryForeground", "ink-muted"),
        ("Tandem.PathBackground", "bar"),
        ("Tandem.PathForeground", "ink"),
        ("Tandem.ActivePathBackground", "accent"),
        ("Tandem.ActivePathForeground", "on-accent"),
        ("Tandem.CursorBackground", "cursor"),
        ("Tandem.CursorBorder", "edge"),
        ("Tandem.TabActiveBackground", "surface"),
        ("Tandem.ViewerBackground", "surface-raised"),
        ("Tandem.ViewerForeground", "ink"),
        ("Tandem.ViewerSelection", "focus"),
    ];

    /// <summary>Fluent's control brush keys and their token.</summary>
    public static readonly IReadOnlyList<(string Key, string Token)> FluentBrushes =
    [
        // Buttons and F-keys.
        ("ButtonBackground", "bar"), ("ButtonBackgroundPointerOver", "bar-hover"),
        ("ButtonBackgroundPressed", "bar-pressed"), ("ButtonBackgroundDisabled", "surface-raised"),
        ("ButtonForeground", "ink"), ("ButtonForegroundPointerOver", "ink"), ("ButtonForegroundPressed", "ink"),
        ("ButtonForegroundDisabled", "ink-disabled"),
        ("ToggleButtonBackground", "bar"), ("ToggleButtonBackgroundPointerOver", "bar-hover"),
        ("ToggleButtonBackgroundPressed", "bar-pressed"), ("ToggleButtonBackgroundDisabled", "surface-raised"),
        ("ToggleButtonForeground", "ink"), ("ToggleButtonForegroundPointerOver", "ink"),
        ("ToggleButtonForegroundPressed", "ink"), ("ToggleButtonForegroundDisabled", "ink-disabled"),
        ("ToggleButtonBackgroundChecked", "focus"), ("ToggleButtonBackgroundCheckedPointerOver", "focus"),
        ("ToggleButtonBackgroundCheckedPressed", "focus"), ("ToggleButtonForegroundChecked", "on-focus"),
        ("ToggleButtonForegroundCheckedPointerOver", "on-focus"), ("ToggleButtonForegroundCheckedPressed", "on-focus"),

        // Text fields (the command line, dialogs, rename, path editor).
        ("TextControlBackground", "field"), ("TextControlBackgroundPointerOver", "field"),
        ("TextControlBackgroundFocused", "field"),
        ("TextControlBorderBrush", "line"), ("TextControlBorderBrushPointerOver", "line"),
        ("TextControlBorderBrushFocused", "focus"),
        ("TextControlForeground", "ink"), ("TextControlForegroundPointerOver", "ink"),
        ("TextControlForegroundFocused", "ink"),
        ("TextControlPlaceholderForeground", "ink-muted"), ("TextControlPlaceholderForegroundPointerOver", "ink-muted"),
        ("TextControlPlaceholderForegroundFocused", "ink-muted"),
        ("TextControlButtonForeground", "ink-muted"),
        ("TextControlSelectionHighlightColor", "focus"),

        // Check boxes.
        ("CheckBoxCheckBackgroundFillUnchecked", "field"), ("CheckBoxCheckBackgroundFillUncheckedPointerOver", "field"),
        ("CheckBoxCheckBackgroundFillUncheckedPressed", "field"),
        ("CheckBoxCheckBackgroundStrokeUnchecked", "edge"), ("CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "edge"),
        ("CheckBoxCheckBackgroundStrokeUncheckedPressed", "edge"),
        ("CheckBoxCheckBackgroundFillChecked", "focus"), ("CheckBoxCheckBackgroundFillCheckedPointerOver", "focus"),
        ("CheckBoxCheckBackgroundFillCheckedPressed", "focus"),
        ("CheckBoxCheckBackgroundStrokeCheckedPointerOver", "focus"), ("CheckBoxCheckBackgroundStrokeCheckedPressed", "focus"),
        ("CheckBoxCheckGlyphForegroundChecked", "on-focus"), ("CheckBoxCheckGlyphForegroundCheckedPointerOver", "on-focus"),
        ("CheckBoxCheckGlyphForegroundCheckedPressed", "on-focus"),
        ("CheckBoxCheckBackgroundFillIndeterminate", "focus"), ("CheckBoxCheckBackgroundFillIndeterminatePointerOver", "focus"),
        ("CheckBoxCheckBackgroundFillIndeterminatePressed", "focus"),
        ("CheckBoxCheckBackgroundStrokeIndeterminate", "focus"),
        ("CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "focus"),
        ("CheckBoxCheckBackgroundStrokeIndeterminatePressed", "focus"),
        ("CheckBoxCheckGlyphForegroundIndeterminate", "on-focus"),
        ("CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "on-focus"),
        ("CheckBoxCheckGlyphForegroundIndeterminatePressed", "on-focus"),

        // Drop-down lists.
        ("ComboBoxBackground", "field"), ("ComboBoxBackgroundPointerOver", "field"), ("ComboBoxBackgroundPressed", "field"),
        ("ComboBoxBackgroundUnfocused", "field"),
        ("ComboBoxBorderBrush", "edge"), ("ComboBoxBorderBrushPointerOver", "edge"), ("ComboBoxBorderBrushPressed", "edge"),
        ("ComboBoxBackgroundBorderBrushFocused", "focus"), ("ComboBoxBackgroundBorderBrushUnfocused", "edge"),
        ("ComboBoxForeground", "ink"), ("ComboBoxPlaceHolderForeground", "ink-muted"),
        ("ComboBoxDropDownGlyphForeground", "ink-muted"),
        ("ComboBoxDropDownBackground", "field"), ("ComboBoxDropDownBorderBrush", "edge"),
        ("ComboBoxItemBackgroundSelected", "cursor"), ("ComboBoxItemBackgroundSelectedPointerOver", "cursor"),
        ("ComboBoxItemBackgroundSelectedPressed", "cursor"),
        ("ComboBoxItemForegroundSelected", "ink"), ("ComboBoxItemForegroundSelectedPointerOver", "ink"),
        ("ComboBoxItemForegroundSelectedPressed", "ink"),

        // Windows and their main text. The palette's RegionColor/BaseHigh set these at start, but Fluent does not
        // rebuild them when the palette changes later, so a theme applied with windows open writes them itself.
        ("SystemRegionBrush", "surface"), ("SystemControlForegroundBaseHighBrush", "ink"),
        // The text of selected list items (the cursor row, the selected tab): ink on the cursor.
        ("SystemControlHighlightAltBaseHighBrush", "ink"),

        // Lists: the file list and the other list boxes lie on the surface.
        ("SystemControlBackgroundChromeMediumLowBrush", "surface"),

        // Selected list items (the shortcut list, the tabs; the file list sets its own cursor).
        ("SystemControlHighlightListAccentLowBrush", "cursor"), ("SystemControlHighlightListAccentMediumBrush", "cursor"),
        ("SystemControlHighlightListAccentHighBrush", "cursor"),

        // Settings tabs: the selected one is underlined in the input colour.
        ("TabItemHeaderSelectedPipeFill", "focus"),
        ("TabItemHeaderForegroundSelected", "ink"), ("TabItemHeaderForegroundSelectedPointerOver", "ink"),
        ("TabItemHeaderForegroundSelectedPressed", "ink"),
        ("TabItemHeaderForegroundUnselected", "ink-muted"), ("TabItemHeaderForegroundUnselectedPointerOver", "ink"),
        ("TabItemHeaderForegroundUnselectedPressed", "ink"),

        // Menus and context menus (no token of their own: a window, items like buttons).
        ("MenuFlyoutPresenterBackground", "surface"), ("MenuFlyoutPresenterBorderBrush", "line"),
        ("MenuFlyoutItemBackgroundPointerOver", "bar-hover"), ("MenuFlyoutItemBackgroundPressed", "bar-pressed"),
        ("MenuFlyoutItemForeground", "ink"), ("MenuFlyoutItemForegroundPointerOver", "ink"),
        ("MenuFlyoutItemForegroundPressed", "ink"),
        ("MenuFlyoutItemKeyboardAcceleratorTextForeground", "ink-muted"),
        ("MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "ink-muted"),
        ("MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "ink-muted"),

        // Tool tips (a raised area, like the headers).
        ("ToolTipBackground", "surface-raised"), ("ToolTipForeground", "ink"), ("ToolTipBorderBrush", "line"),

        // Scroll bars: the thumb.
        ("ScrollBarThumbFillPointerOver", "edge"), ("ScrollBarThumbFillPressed", "edge"),

        // Keyboard focus outline.
        ("SystemControlFocusVisualPrimaryBrush", "focus"),
    ];

    /// <summary>Fluent's keys that hold a <see cref="Avalonia.Media.Color"/> rather than a brush, and their
    /// token.</summary>
    public static readonly IReadOnlyList<(string Key, string Token)> FluentColors =
    [
        ("ScrollBarThumbBackgroundColor", "line"),
        ("ScrollBarPanningThumbBackgroundColor", "line"),
    ];

    /// <summary>The 16×16 design box every icon is drawn in (filled transparent, so the image keeps its size).</summary>
    public const string IconBox = "M0,0 H16 V16 H0 Z";

    private const string FolderShape =
        "M1.5 12.5v-9a1 1 0 0 1 1-1h3.25l1.5 2h6.25a1 1 0 0 1 1 1v7a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1Z";

    /// <summary>The file list icons: one-colour silhouettes (the shapes of folder.svg, parent.svg, file.svg; holes
    /// are even-odd) filled with a token.</summary>
    public static readonly IReadOnlyList<(string Key, string Token, string Geometry)> Icons =
    [
        ("Tandem.FolderIcon", "folder", FolderShape),
        ("Tandem.ParentIcon", "folder",
            FolderShape + " M7.4 12V8.7L6.3 9.8 5.45 8.95 8 6.4l2.55 2.55-.85.85L8.6 8.7V12Z"),
        ("Tandem.FileIcon", "file",
            "M4.5 1.5h5l3 3v9a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1Z M9.5 2.2v2.3h2.3Z " +
            "M5.5 8h5v1h-5Z M5.5 10.5h4v1h-4Z"),
    ];

    /// <summary>The CSS generic family names a theme's font list may end with, and the concrete families they
    /// stand for here (Avalonia knows no generic names; an empty list drops the name and leaves the platform's
    /// default font as the last resort).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> GenericFamilies =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["system-ui"] = [],
            ["sans-serif"] = [],
            ["ui-sans-serif"] = [],
            ["serif"] = [],
            ["monospace"] = ["Menlo", "Monaco", "Courier New"],
            ["ui-monospace"] = ["Menlo", "Monaco", "Courier New"],
        };

    /// <summary>The Pascal-case form of a token name: "surface-raised" → "SurfaceRaised", "space-2" →
    /// "Space2".</summary>
    public static string Pascal(string token) =>
        string.Concat(token.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
