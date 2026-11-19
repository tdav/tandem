using Avalonia;
using Tandem.App.Input;

namespace Tandem.App.Tests.Input;

/// <summary>When a press-and-move becomes a drag: only past the threshold with the button held, once per press; a
/// release before that is a click.</summary>
public class DragGestureTests
{
    [Fact]
    public void A_move_below_the_threshold_is_not_a_drag_and_the_release_is_a_click()
    {
        var gesture = new DragGesture();
        gesture.Press(new Point(10, 10));

        Assert.False(gesture.Move(new Point(13, 12)));
        Assert.False(gesture.Move(new Point(14, 6)));
        Assert.False(gesture.IsDragging);
        Assert.True(gesture.Release());
        Assert.False(gesture.IsPressed);
    }

    [Theory]
    [InlineData(15, 10)]
    [InlineData(10, 5)]
    [InlineData(5, 15)]
    public void A_move_past_the_threshold_starts_the_drag_once(double x, double y)
    {
        var gesture = new DragGesture();
        gesture.Press(new Point(10, 10));

        Assert.True(gesture.Move(new Point(x, y)));
        Assert.True(gesture.IsDragging);
        Assert.False(gesture.Move(new Point(x + 20, y + 20))); // already started
        Assert.False(gesture.Release()); // not a click
    }

    [Fact]
    public void Moves_without_a_press_or_after_a_release_or_cancel_start_nothing()
    {
        var gesture = new DragGesture();
        Assert.False(gesture.Move(new Point(100, 100)));
        Assert.False(gesture.Release());

        gesture.Press(new Point(0, 0));
        gesture.Release();
        Assert.False(gesture.Move(new Point(50, 50)));

        gesture.Press(new Point(0, 0));
        gesture.Cancel();
        Assert.False(gesture.IsPressed);
        Assert.False(gesture.Move(new Point(50, 50)));
    }

    [Fact]
    public void A_new_press_starts_over()
    {
        var gesture = new DragGesture();
        gesture.Press(new Point(0, 0));
        Assert.True(gesture.Move(new Point(10, 0)));

        gesture.Press(new Point(100, 100));
        Assert.False(gesture.IsDragging);
        Assert.False(gesture.Move(new Point(102, 100)));
        Assert.True(gesture.Move(new Point(105, 100)));
    }

    [Fact]
    public void The_threshold_is_about_four_pixels()
    {
        Assert.Equal(4, DragGesture.DefaultThreshold);
        var gesture = new DragGesture();
        gesture.Press(new Point(0, 0));
        Assert.False(gesture.Move(new Point(4, 4))); // exactly at the threshold: not yet
        Assert.True(gesture.Move(new Point(4.5, 0)));
    }
}
