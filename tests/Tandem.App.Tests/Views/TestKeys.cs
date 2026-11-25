using Avalonia.Input;
using Avalonia.Input.Raw;

namespace Tandem.App.Tests.Views;

/// <summary>Key presses written as on the Mac, made for the running system: on Windows Ctrl takes ⌘'s part (and
/// a press with both stays as it is).</summary>
internal static class TestKeys
{
    public static RawInputModifiers Platform(RawInputModifiers modifiers) =>
        OperatingSystem.IsWindows() && modifiers.HasFlag(RawInputModifiers.Meta) &&
        !modifiers.HasFlag(RawInputModifiers.Control)
            ? (modifiers & ~RawInputModifiers.Meta) | RawInputModifiers.Control
            : modifiers;
}
