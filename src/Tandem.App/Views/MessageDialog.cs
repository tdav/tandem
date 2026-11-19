using Avalonia.Controls;
using Avalonia.Layout;
using Tandem.App.Resources;

namespace Tandem.App.Views;

/// <summary>A small message box with an OK button.</summary>
public sealed class MessageDialog : Window
{
    public MessageDialog(string title, string message)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button
        {
            Content = Loc.Get("Button_OK"),
            IsDefault = true,
            IsCancel = true,
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 460 },
                ok,
            },
        };
    }

    /// <summary>Shows the message, modal to <paramref name="owner"/> when it is shown; completes when it is closed.</summary>
    public static Task ShowAsync(Window? owner, string title, string message)
    {
        var dialog = new MessageDialog(title, message);
        if (owner is { IsVisible: true }) return dialog.ShowDialog(owner);
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        return closed.Task;
    }
}
