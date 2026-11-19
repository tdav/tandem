using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>Renders a <see cref="ContextMenuItem"/> list as a <see cref="ContextMenu"/>: labels, shortcuts (shown
/// only; the window's key handling runs them), disabled items, and submenus filled when they open.</summary>
public static class ContextMenuView
{
    public static ContextMenu Build(IReadOnlyList<ContextMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var menu = new ContextMenu();
        foreach (var item in items) menu.Items.Add(ToMenuItem(item));
        return menu;
    }

    private static MenuItem ToMenuItem(ContextMenuItem item)
    {
        var menuItem = new MenuItem
        {
            Header = item.Label,
            IsEnabled = item.IsEnabled,
            Tag = item.CommandId,
        };
        if (item.Chord is { } chord) menuItem.InputGesture = new KeyGesture(chord.Key, chord.Modifiers);
        if (item.HasChildren)
        {
            // A stand-in keeps the item a submenu; the real items are asked for only when it opens.
            menuItem.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
            var filled = false;
            menuItem.SubmenuOpened += (_, e) =>
            {
                if (filled || !ReferenceEquals(e.Source, menuItem)) return;
                filled = true;
                menuItem.Items.Clear();
                foreach (var child in item.GetChildren()) menuItem.Items.Add(ToMenuItem(child));
            };
        }
        else
        {
            menuItem.Click += OnClick;
        }
        return menuItem;

        void OnClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _ = item.ExecuteAsync(); // never throws: failures are reported by what it runs
        }
    }
}
