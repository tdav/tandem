namespace Tandem.Core.Platform;

/// <summary>The system's quick preview of files.</summary>
public interface IQuickLook
{
    /// <summary>Whether the preview is showing; it becomes false when the preview closes by itself.</summary>
    bool IsOpen { get; }

    /// <summary>Closes the preview when it is open; otherwise opens it for <paramref name="paths"/> (nothing happens
    /// for an empty list).</summary>
    /// <exception cref="IOException">The preview could not be started.</exception>
    void Toggle(IReadOnlyList<string> paths);

    /// <summary>Closes the preview; does nothing when it is not open.</summary>
    void Close();
}
