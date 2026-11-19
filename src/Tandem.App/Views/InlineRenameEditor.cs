using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;

namespace Tandem.App.Views;

/// <summary>
/// The name editor shown inside a row while <see cref="EntryRowViewModel.Rename"/> is set (its data context is the
/// <see cref="InlineRenameViewModel"/>). It takes the focus with the name's part before the extension selected.
/// Enter commits, Esc cancels, losing the focus cancels; a refused name keeps it open with the reason as a tool tip.
/// Being a <see cref="TextBox"/>, the window's focus rule treats it as a text editor.
/// </summary>
public sealed class InlineRenameEditor : TextBox
{
    private InlineRenameViewModel? _session;

    public InlineRenameEditor()
    {
        Name = "RenameEditor";
        MinHeight = 0;
        Padding = new Thickness(2, 0);
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        TextChanged += (_, _) =>
        {
            if (_session is { IsOpen: true } session) session.Text = Text ?? "";
        };
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_session is not null) _session.PropertyChanged -= OnSessionPropertyChanged;
        _session = DataContext as InlineRenameViewModel;
        if (_session is not { } session) return;
        session.PropertyChanged += OnSessionPropertyChanged;
        Text = session.Text;
        ShowError(null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (_session is not { IsOpen: true } session || !IsAttachedToVisualTree()) return;
            Focus();
            SelectionStart = 0;
            SelectionEnd = session.SelectionLength;
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ToolTip.SetIsOpen(this, false);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session is { } session && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = CommitAsync(session);
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                var list = this.FindAncestorOfType<FileListView>();
                session.Cancel();
                list?.FocusList();
                return;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _session?.Cancel(); // nothing happens if it was committed or cancelled already
    }

    private async Task CommitAsync(InlineRenameViewModel session)
    {
        var list = this.FindAncestorOfType<FileListView>();
        if (await session.CommitAsync()) list?.FocusList();
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InlineRenameViewModel.Error)) ShowError(_session?.Error);
    }

    private void ShowError(string? error)
    {
        ToolTip.SetTip(this, error);
        ToolTip.SetIsOpen(this, error is not null && IsAttachedToVisualTree());
        Classes.Set("error", error is not null);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;
}
