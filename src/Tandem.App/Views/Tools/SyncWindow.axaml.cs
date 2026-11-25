using Tandem.App.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tandem.App.ViewModels.Tools;
using Tandem.Core.Tools.Sync;

namespace Tandem.App.Views.Tools;

public sealed partial class SyncWindow : Window
{
    public SyncWindow()
    {
        InitializeComponent();
        SyncRows.AddHandler(KeyDownEvent, OnRowsKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, HandleEscape, RoutingStrategies.Tunnel);
    }

    public SyncWindow(SyncViewModel model) : this() => DataContext = model;

    private void HandleEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        Close();
    }

    private void OnRowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SyncViewModel { CanEdit: true }) return;
        if (e.Key == Key.A && e.KeyModifiers == PlatformKeys.Command)
        {
            SyncRows.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Right or Key.Left or Key.Space or Key.Delete))
            return;
        foreach (var row in SyncRows.SelectedItems!.OfType<SyncRowViewModel>().ToArray())
        {
            row.Action = e.Key switch
            {
                Key.Right => SyncAction.CopyToRight,
                Key.Left => SyncAction.CopyToLeft,
                Key.Delete when row.Item.State == SyncState.LeftOnly => SyncAction.DeleteLeft,
                Key.Delete when row.Item.State == SyncState.RightOnly => SyncAction.DeleteRight,
                Key.Space => NextCopyChoice(row),
                _ => row.Action,
            };
        }
        e.Handled = true;
    }

    private static SyncAction NextCopyChoice(SyncRowViewModel row)
    {
        var choices = row.Item.AllowedActions.Where(action =>
            action is not (SyncAction.DeleteLeft or SyncAction.DeleteRight)).ToArray();
        var at = Array.IndexOf(choices, row.Action);
        return choices[(at + 1) % choices.Length];
    }
}
