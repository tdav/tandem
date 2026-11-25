using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Tandem.App.Services;

/// <summary>The app's clipboard for text, written by the commands (Copy Path).</summary>
public interface ITextClipboard
{
    /// <summary>Puts <paramref name="text"/> on the clipboard in place of what was there; false when there is no
    /// clipboard to put it on (nothing was copied).</summary>
    Task<bool> SetTextAsync(string text);
}

/// <summary>The system clipboard of <see cref="Window"/> (the main window); reports failure until it is set.</summary>
public sealed class WindowTextClipboard : ITextClipboard
{
    public TopLevel? Window { get; set; }

    public async Task<bool> SetTextAsync(string text)
    {
        if (Window?.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }
}
