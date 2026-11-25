using Tandem.App.Views.Net;

namespace Tandem.App.Tests;

public sealed class TerminalCursorBlinkTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void Focused_cursor_toggles_every_500_ms()
    {
        var clock = new ManualClock();
        var blink = new TerminalCursorBlink(clock) { Focused = true };
        Assert.True(blink.IsBlinking);
        var states = new List<bool>();
        for (var i = 0; i < 4; i++)
        {
            states.Add(blink.IsVisible);
            clock.Advance(499 * Ms);
            Assert.Equal(states[^1], blink.IsVisible);
            clock.Advance(Ms);
        }
        Assert.Equal([true, false, true, false], states);
    }

    [Fact]
    public void Input_resets_phase_to_visible()
    {
        var clock = new ManualClock();
        var blink = new TerminalCursorBlink(clock) { Focused = true };
        clock.Advance(700 * Ms);
        Assert.False(blink.IsVisible);
        blink.Reset();
        Assert.True(blink.IsVisible);
        Assert.Equal(500 * Ms, blink.UntilNextToggle);
        clock.Advance(499 * Ms);
        Assert.True(blink.IsVisible);
        Assert.Equal(Ms, blink.UntilNextToggle);
    }

    [Fact]
    public void Unfocused_or_steady_shape_never_hides()
    {
        var clock = new ManualClock();
        var blink = new TerminalCursorBlink(clock);
        Assert.False(blink.IsBlinking);
        Assert.Null(blink.UntilNextToggle);
        clock.Advance(700 * Ms);
        Assert.True(blink.IsVisible);

        blink.Focused = true;
        blink.Blinks = false;
        clock.Advance(700 * Ms);
        Assert.False(blink.IsBlinking);
        Assert.True(blink.IsVisible);
    }

    [Fact]
    public void Regaining_focus_starts_visible()
    {
        var clock = new ManualClock();
        var blink = new TerminalCursorBlink(clock) { Focused = true };
        clock.Advance(600 * Ms);
        blink.Focused = false;
        clock.Advance(300 * Ms);
        blink.Focused = true;
        Assert.True(blink.IsVisible);
    }
}
