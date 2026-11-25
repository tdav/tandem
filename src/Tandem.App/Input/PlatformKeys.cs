using Avalonia.Input;

namespace Tandem.App.Input;

/// <summary>The modifier that carries an app's commands: ⌘ (<see cref="KeyModifiers.Meta"/>) on macOS, Ctrl on
/// Windows, where Meta is the Windows key and belongs to the system.</summary>
public static class PlatformKeys
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();

    /// <summary>The command modifier of the running system.</summary>
    public static KeyModifiers Command { get; } = IsWindows ? KeyModifiers.Control : KeyModifiers.Meta;

    /// <summary>True when the event's modifiers hold the command modifier.</summary>
    public static bool HasCommand(KeyModifiers modifiers) => modifiers.HasFlag(Command);
}
