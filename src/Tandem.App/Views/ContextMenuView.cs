using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.Resources;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>Renders a <see cref="ContextMenuItem"/> list as a <see cref="ContextMenu"/>: labels, shortcuts (shown
/// only; the window's key handling runs them), disabled items, and submenus filled when they open. A literal label (an
/// application's name) is shown exactly as it is: a "_" in it never marks an access key.</summary>
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
            // A string header goes through AccessText, which takes "_x" as an access key (and "__" as the key "_").
            Header = item.IsLiteralLabel ? new TextBlock { Text = item.Label } : item.Label,
            IsEnabled = item.IsEnabled,
            Tag = item.CommandId,
        };
        if (item.Chord is { } chord) menuItem.InputGesture = new KeyGesture(chord.Key, chord.Modifiers);
        if (item.HasChildren)
        {
            // A stand-in keeps the item a submenu; the real items are asked for only when it opens, and replace the
            // stand-in when they arrive.
            menuItem.Items.Add(new MenuItem { Header = Loc.Get("Context_Loading"), IsEnabled = false });
            var asked = false;
            menuItem.SubmenuOpened += async (_, e) =>
            {
                if (asked || !ReferenceEquals(e.Source, menuItem)) return;
                asked = true;
                var children = await item.GetChildrenAsync(); // never throws: failures become no items
                menuItem.Items.Clear();
                foreach (var child in children) menuItem.Items.Add(ToMenuItem(child));
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
