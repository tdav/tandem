using Tandem.Core.Platform;

namespace Tandem.Platform.Windows;

/// <summary>Windows has no system quick preview: nothing ever opens (the app's own quick view stays).</summary>
public sealed class WinQuickLook : IQuickLook
{
    public bool IsOpen => false;

    public void Toggle(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths) WinPath.RequireAbsolute(path, nameof(paths));
    }

    public void Close() { }
}
