using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Tandem.App.Services;

/// <summary>The app's clipboard for text, written by the commands (Copy Path).</summary>
public interface ITextClipboard
{
    /// <summary>Puts <paramref name="text"/> on the clipboard in place of what was there.</summary>
    Task SetTextAsync(string text);
}

/// <summary>The system clipboard of <see cref="Window"/> (the main window); does nothing until it is set.</summary>
internal sealed class WindowTextClipboard : ITextClipboard
{
    public TopLevel? Window { get; set; }

    public Task SetTextAsync(string text) => Window?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
}
