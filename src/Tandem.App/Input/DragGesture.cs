using Avalonia;

namespace Tandem.App.Input;

/// <summary>
/// Tells a press-and-move that should start a drag from a plain click: after <see cref="Press"/>, the first
/// <see cref="Move"/> that goes more than the threshold away from the press point (in either direction) starts the
/// drag, once; a <see cref="Release"/> before that was a click. Pure state, no input of its own.
/// </summary>
public sealed class DragGesture(double threshold = DragGesture.DefaultThreshold)
{
    /// <summary>How far, in device-independent pixels, the pointer must move with the button held.</summary>
    public const double DefaultThreshold = 4;

    private Point _start;

    /// <summary>A press is being followed (not released or cancelled yet).</summary>
    public bool IsPressed { get; private set; }

    /// <summary>The press has turned into a drag.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>The button went down at <paramref name="point"/>; starts over.</summary>
    public void Press(Point point)
    {
        _start = point;
        IsPressed = true;
        IsDragging = false;
    }

    /// <summary>The pointer moved with the button held. True exactly once: when this move starts the drag.</summary>
    public bool Move(Point point)
    {
        if (!IsPressed || IsDragging) return false;
        if (Math.Abs(point.X - _start.X) <= threshold && Math.Abs(point.Y - _start.Y) <= threshold) return false;
        IsDragging = true;
        return true;
    }

    /// <summary>The button went up. True when this ends a click (a press that never became a drag).</summary>
    public bool Release()
    {
        var click = IsPressed && !IsDragging;
        Cancel();
        return click;
    }

    /// <summary>Stops following the press (capture lost, the drag ended).</summary>
    public void Cancel()
    {
        IsPressed = false;
        IsDragging = false;
    }
}
