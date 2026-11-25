namespace Tandem.App.Views.Net;

/// <summary>
/// Blink phase of a terminal cursor: visible for <see cref="HalfPeriod"/>, hidden for as long, so it blinks once a
/// second. It blinks only while the terminal is focused and the cursor shape blinks; otherwise it is steady. Any
/// keystroke or output restarts the phase from "visible" so the cursor never vanishes while typing.
/// </summary>
public sealed class TerminalCursorBlink(TimeProvider clock)
{
    public static readonly TimeSpan HalfPeriod = TimeSpan.FromMilliseconds(500);

    private long _phaseStart = clock.GetTimestamp();
    private bool _focused;
    private bool _blinks = true;

    public TerminalCursorBlink() : this(TimeProvider.System) { }

    public bool Focused
    {
        get => _focused;
        set { if (_focused != value) { _focused = value; Reset(); } }
    }

    /// <summary>Whether the cursor shape blinks (DECSCUSR odd codes and the default).</summary>
    public bool Blinks
    {
        get => _blinks;
        set { if (_blinks != value) { _blinks = value; Reset(); } }
    }

    /// <summary>True when a timer is needed to animate the cursor.</summary>
    public bool IsBlinking => _focused && _blinks;

    public bool IsVisible => !IsBlinking || clock.GetElapsedTime(_phaseStart) / HalfPeriod % 2 < 1;

    /// <summary>Time until <see cref="IsVisible"/> next flips, or null while the cursor is steady.</summary>
    public TimeSpan? UntilNextToggle
    {
        get
        {
            if (!IsBlinking) return null;
            var elapsed = clock.GetElapsedTime(_phaseStart).Ticks % HalfPeriod.Ticks;
            return TimeSpan.FromTicks(HalfPeriod.Ticks - elapsed);
        }
    }

    /// <summary>Restarts the phase with the cursor visible (on keystrokes and output).</summary>
    public void Reset() => _phaseStart = clock.GetTimestamp();
}
