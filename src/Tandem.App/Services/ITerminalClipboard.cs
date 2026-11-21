using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Tandem.App.Services;

public interface ITerminalClipboard
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}

internal sealed class WindowTerminalClipboard : ITerminalClipboard
{
    public TopLevel? Window { get; set; }
    public Task<string?> GetTextAsync() => Window?.Clipboard?.TryGetTextAsync() ?? Task.FromResult<string?>(null);
    public Task SetTextAsync(string text) => Window?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
}
