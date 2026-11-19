using Tandem.App.ViewModels;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

public class NavigationHistoryTests
{
    private static VfsPath Mem(string path) => new("mem", path);

    [Fact]
    public void Empty_history_cannot_move()
    {
        var history = new NavigationHistory();

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
        Assert.Empty(history.Recent);
    }

    [Fact]
    public void Back_and_forward()
    {
        var history = new NavigationHistory();
        history.Visit(Mem("/a"));
        history.Visit(Mem("/b"));
        history.Visit(Mem("/c"));

        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Equal(Mem("/b"), history.Back());
        Assert.Equal(Mem("/a"), history.Back());
        Assert.False(history.CanGoBack);
        Assert.Null(history.Back());
        Assert.True(history.CanGoForward);
        Assert.Equal(Mem("/b"), history.Forward());
        Assert.Equal(Mem("/c"), history.Forward());
        Assert.False(history.CanGoForward);
        Assert.Null(history.Forward());
    }

    [Fact]
    public void New_visit_truncates_forward()
    {
        var history = new NavigationHistory();
        history.Visit(Mem("/a"));
        history.Visit(Mem("/b"));
        history.Visit(Mem("/c"));
        history.Back();
        history.Back();

        history.Visit(Mem("/x"));

        Assert.False(history.CanGoForward);
        Assert.Equal(Mem("/a"), history.Back());
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Consecutive_duplicate_ignored()
    {
        var history = new NavigationHistory();
        history.Visit(Mem("/a"));
        history.Visit(Mem("/b"));
        history.Visit(Mem("/b"));

        Assert.Equal(Mem("/a"), history.Back());
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Revisiting_the_current_entry_keeps_forward()
    {
        var history = new NavigationHistory();
        history.Visit(Mem("/a"));
        history.Visit(Mem("/b"));
        history.Back();

        history.Visit(Mem("/a"));

        Assert.True(history.CanGoForward);
        Assert.Equal(Mem("/b"), history.Forward());
    }

    [Fact]
    public void Caps_at_50()
    {
        var history = new NavigationHistory();
        for (var i = 0; i < 60; i++) history.Visit(Mem($"/d{i}"));

        var steps = 0;
        VfsPath? oldest = null;
        while (history.Back() is { } path)
        {
            oldest = path;
            steps++;
        }

        Assert.Equal(NavigationHistory.Capacity - 1, steps);
        Assert.Equal(50, NavigationHistory.Capacity);
        Assert.Equal(Mem("/d10"), oldest);
        Assert.Equal(50, history.Recent.Count);
    }

    [Fact]
    public void Recent_lists_distinct_folders_newest_first()
    {
        var history = new NavigationHistory();
        history.Visit(Mem("/a"));
        history.Visit(Mem("/b"));
        history.Visit(Mem("/a"));
        history.Visit(Mem("/c"));

        Assert.Equal([Mem("/c"), Mem("/a"), Mem("/b")], history.Recent);
    }
}
