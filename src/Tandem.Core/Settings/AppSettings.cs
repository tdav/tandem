namespace Tandem.Core.Settings;

/// <summary>The colour scheme: the system's, or always light or dark.</summary>
public enum ThemeChoice { System, Light, Dark }

public enum SshConsolePlacement { SeparateWindow, PanelTab }

public enum SshConsoleSide { Right, Left }

/// <summary>A file-list column that may be hidden, moved or resized; "Name" is always shown, first.</summary>
public enum ColumnId { Extension, Size, Modified, Mode }

/// <summary>One column of a panel's file list: whether it is shown and how wide it is.</summary>
public sealed record ColumnSetting(ColumnId Column, bool Visible, double Width);

/// <summary>Replaces every key chord of the command <see cref="CommandId"/> with <see cref="Chords"/> (as the app
/// writes them, e.g. "Ctrl+Shift+F5"); an empty list leaves the command without chords.</summary>
public sealed record KeyBindingOverride(string CommandId, IReadOnlyList<string> Chords);

/// <summary>Files whose names match <see cref="Mask"/> (a file mask such as "*.jpg;*.png") are shown in
/// <see cref="Color"/>, one of <see cref="AppSettings.ColorNames"/>.</summary>
public sealed record FileColorRule(string Mask, string Color);

/// <summary>A button of the button bar: runs <see cref="Program"/> with <see cref="Arguments"/>.</summary>
public sealed record UserButton(string Title, string Program, string Arguments);

/// <summary>
/// What the user has set up in the settings window, kept in <c>settings.json</c> beside <c>state.json</c>.
/// <see cref="ExternalEditor"/> is the program F4 opens files with; null means the built-in editor.
/// </summary>
public sealed record AppSettings(int Version, ThemeChoice Theme, double FontSize, double RowHeight,
    IReadOnlyList<ColumnSetting> LeftColumns, IReadOnlyList<ColumnSetting> RightColumns,
    IReadOnlyList<FileColorRule> FileColors, IReadOnlyList<KeyBindingOverride> KeyBindings,
    IReadOnlyList<UserButton> Buttons, string? ExternalEditor)
{
    public SshConsolePlacement SshConsolePlacement { get; init; }
    public SshConsoleSide SshConsolePanel { get; init; }

    public const int CurrentVersion = 1;
    public const double DefaultFontSize = 14, MinFontSize = 10, MaxFontSize = 24;
    public const double DefaultRowHeight = 22, MinRowHeight = 16, MaxRowHeight = 48;
    public const double MinColumnWidth = 30, MaxColumnWidth = 400;

    /// <summary>How much taller than the font a row must be.</summary>
    public const double RowPadding = 6;

    /// <summary>The colours a <see cref="FileColorRule"/> may name; the app maps each to a theme resource.</summary>
    public static IReadOnlyList<string> ColorNames { get; } =
        ["Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Gray"];

    /// <summary>Every optional column, shown, in their usual order and widths.</summary>
    public static IReadOnlyList<ColumnSetting> DefaultColumns { get; } =
    [
        new(ColumnId.Extension, true, 56),
        new(ColumnId.Size, true, 84),
        new(ColumnId.Modified, true, 124),
        new(ColumnId.Mode, true, 84),
    ];

    /// <summary>A first-run setup: the system theme, the usual sizes and columns, nothing else set.</summary>
    public static AppSettings Default { get; } = new(CurrentVersion, ThemeChoice.System, DefaultFontSize,
        DefaultRowHeight, DefaultColumns, DefaultColumns, FileColors: [], KeyBindings: [], Buttons: [],
        ExternalEditor: null);

    /// <summary>
    /// These settings made usable, as read from a file someone may have edited: numbers clamped to their limits
    /// (a row at least <c>FontSize + 6</c> high; a missing, zero or negative number gets its default), missing lists empty, each column list holding every column once
    /// (repeats dropped, missing ones added from <see cref="DefaultColumns"/>, widths clamped), colour rules without a
    /// mask or with an unknown colour, buttons without a title or program and key overrides without a command or
    /// without a chord list dropped, and a blank <see cref="ExternalEditor"/> null.
    /// </summary>
    public AppSettings Normalized()
    {
        var fontSize = Clamp(FontSize, MinFontSize, MaxFontSize, DefaultFontSize);
        var rowHeight = Math.Max(Clamp(RowHeight, MinRowHeight, MaxRowHeight, DefaultRowHeight), fontSize + RowPadding);
        return this with
        {
            SshConsolePlacement = Enum.IsDefined(SshConsolePlacement) ? SshConsolePlacement : SshConsolePlacement.SeparateWindow,
            SshConsolePanel = Enum.IsDefined(SshConsolePanel) ? SshConsolePanel : SshConsoleSide.Right,
            FontSize = fontSize,
            RowHeight = rowHeight,
            LeftColumns = NormalizedColumns(LeftColumns),
            RightColumns = NormalizedColumns(RightColumns),
            FileColors = (FileColors ?? [])
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Mask) && ColorNames.Contains(r.Color))
                .ToArray(),
            KeyBindings = NormalizedKeyBindings(KeyBindings),
            Buttons = (Buttons ?? [])
                .Where(b => b is not null && !string.IsNullOrWhiteSpace(b.Title) && !string.IsNullOrWhiteSpace(b.Program))
                .Select(b => b with { Arguments = b.Arguments ?? "" })
                .ToArray(),
            ExternalEditor = string.IsNullOrWhiteSpace(ExternalEditor) ? null : ExternalEditor,
        };
    }

    /// <summary><paramref name="value"/> kept within its limits; NaN, zero or less (a number left out of the file
    /// reads as 0) gives <paramref name="fallback"/>, the usual value, not the smallest one.</summary>
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || value <= 0 ? fallback : Math.Clamp(value, min, max);

    private static ColumnSetting[] NormalizedColumns(IReadOnlyList<ColumnSetting>? columns)
    {
        var result = new List<ColumnSetting>(DefaultColumns.Count);
        foreach (var column in (columns ?? []).Concat(DefaultColumns))
        {
            if (column is null || !Enum.IsDefined(column.Column) || result.Any(c => c.Column == column.Column)) continue;
            var fallback = DefaultColumns.First(d => d.Column == column.Column).Width;
            result.Add(column with { Width = Clamp(column.Width, MinColumnWidth, MaxColumnWidth, fallback) });
        }
        return result.ToArray();
    }

    /// <summary>One override per command, the first one written; chords that are blank are dropped. An override
    /// without a chord list (left out or null) is dropped, so the command keeps its default chords; an empty list
    /// still leaves the command without chords.</summary>
    private static KeyBindingOverride[] NormalizedKeyBindings(IReadOnlyList<KeyBindingOverride>? overrides)
    {
        var result = new List<KeyBindingOverride>();
        foreach (var binding in overrides ?? [])
        {
            if (binding?.Chords is null || string.IsNullOrWhiteSpace(binding.CommandId) ||
                result.Any(b => b.CommandId == binding.CommandId)) continue;
            var chords = binding.Chords.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();
            result.Add(binding with { Chords = chords });
        }
        return result.ToArray();
    }
}
