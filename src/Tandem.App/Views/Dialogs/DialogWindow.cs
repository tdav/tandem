using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tandem.App.Resources;

namespace Tandem.App.Views.Dialogs;

/// <summary>
/// The frame every file-command dialog shares: a fixed-size window centred on its owner, a column of content and a
/// row of buttons on the right. Enter presses the default button and Esc the cancel button (Avalonia's
/// <see cref="Button.IsDefault"/> and <see cref="Button.IsCancel"/>); the default button has the focus when the
/// dialog opens, unless a text field asks for it. All logic lives in the dialog's view model.
/// </summary>
public abstract class DialogWindow : Window
{
    private Control? _initialFocus;

    protected DialogWindow(string title)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Opened += (_, _) => _initialFocus?.Focus();
    }

    /// <summary>The body above the buttons; <paramref name="initialFocus"/> gets the focus when the dialog opens.</summary>
    protected void SetContent(Control initialFocus, IEnumerable<Control> body, params Button[] buttons)
    {
        _initialFocus = initialFocus;
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12, MinWidth = 380, MaxWidth = 560 };
        foreach (var control in body) panel.Children.Add(control);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var button in buttons) row.Children.Add(button);
        panel.Children.Add(row);
        Content = panel;
    }

    protected static Button MakeButton(string text, bool isDefault = false, bool isCancel = false, string? name = null)
    {
        var button = new Button
        {
            Content = text,
            IsDefault = isDefault,
            IsCancel = isCancel,
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (name is not null) button.Name = name;
        return button;
    }

    protected static Button CancelButton(bool isDefault = false) =>
        MakeButton(Loc.Get("Button_Cancel"), isDefault: isDefault, isCancel: true, name: "Cancel");

    protected static TextBlock Text(string? text, bool secondary = false, string? name = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        if (secondary) block.Classes.Add("secondary");
        if (name is not null) block.Name = name;
        return block;
    }

    /// <summary>A line in the error colour, hidden while <paramref name="text"/> is empty.</summary>
    protected static TextBlock ErrorText(string name)
    {
        var block = Text(null, name: name);
        block.IsVisible = false;
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("Tandem.ErrorForeground"));
        return block;
    }

    protected static void ShowError(TextBlock block, string? error)
    {
        block.Text = error;
        block.IsVisible = !string.IsNullOrEmpty(error);
    }
}
