using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Tools;
using Tandem.Core.Tools.Compare;

namespace Tandem.App.Views.Tools;

public sealed partial class FileCompareWindow : Window
{
    public FileCompareWindow()
    {
        InitializeComponent();
        ComparisonRows.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            DiffRow row => BuildTextRow(row),
            BinaryRangeRow binary => BuildBinaryRow(binary),
            _ => new Border(),
        });
        AddHandler(KeyDownEvent, HandleKey, RoutingStrategies.Tunnel);
    }

    public FileCompareWindow(FileCompareViewModel model) : this() => DataContext = model;

    private static Control BuildTextRow(DiffRow row)
    {
        var left = Cell(row.LeftNumber, row.LeftText, row.LeftChanged);
        var right = Cell(row.RightNumber, row.RightText, row.RightChanged);
        var grid = Pair(left, right);
        string? resource = row.Kind switch
        {
            DiffKind.Changed => "Tandem.DiffChangedBackground",
            DiffKind.LeftOnly => "Tandem.DiffRemovedBackground",
            DiffKind.RightOnly => "Tandem.DiffAddedBackground",
            _ => null,
        };
        if (resource is not null) grid.Bind(BackgroundProperty, grid.GetResourceObservable(resource));
        return grid;
    }

    private static Control BuildBinaryRow(BinaryRangeRow row)
    {
        var left = Mono(row.LeftHex);
        var right = Mono(row.RightHex);
        ToolTip.SetTip(left, Loc.Format("Compare_BinaryRange", row.OffsetText, row.Length));
        ToolTip.SetTip(right, Loc.Format("Compare_BinaryRange", row.OffsetText, row.Length));
        return Pair(left, right);
    }

    private static Grid Pair(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static Control Cell(int? line, string? text, IReadOnlyList<DiffSpan> spans)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*"), ColumnSpacing = 6 };
        var number = Mono(line?.ToString(Loc.Culture));
        number.Classes.Add("secondary");
        number.TextAlignment = TextAlignment.Right;
        var content = Mono(null);
        Grid.SetColumn(content, 1);
        grid.Children.Add(number);
        grid.Children.Add(content);
        if (text is null) return grid;

        var at = 0;
        foreach (var span in spans)
        {
            if (span.Start > at) content.Inlines!.Add(new Run(text[at..span.Start]));
            at = span.Start + span.Length;
            var changed = new Run(text[span.Start..at]);
            changed.Bind(TextElement.BackgroundProperty, content.GetResourceObservable("Tandem.DiffInlineBackground"));
            content.Inlines!.Add(changed);
        }
        if (at < text.Length) content.Inlines!.Add(new Run(text[at..]));
        ToolTip.SetTip(content, text);
        return grid;
    }

    private static TextBlock Mono(string? text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private void HandleKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileCompareViewModel model) return;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Escape, KeyModifiers.None): Close(); break;
            case (Key.Up, KeyModifiers.Alt): Move(previous: true); break;
            case (Key.Down, KeyModifiers.Alt): Move(previous: false); break;
            case (Key.D1, KeyModifiers.Meta): model.Mode = CompareMode.Text; break;
            case (Key.D2, KeyModifiers.Meta): model.Mode = CompareMode.Binary; break;
            default: return;
        }
        e.Handled = true;
    }

    private void Previous(object? sender, RoutedEventArgs e) => Move(previous: true);
    private void Next(object? sender, RoutedEventArgs e) => Move(previous: false);

    private void Move(bool previous)
    {
        if (DataContext is not FileCompareViewModel model) return;
        if (previous) model.PreviousDifference();
        else model.NextDifference();
        if (model.CurrentRow >= 0) ComparisonRows.ScrollIntoView(model.CurrentRow);
    }
}
