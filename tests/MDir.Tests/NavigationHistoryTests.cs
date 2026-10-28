using MDir.Panes;

namespace MDir.Tests;

public class NavigationHistoryTests
{
    [Fact]
    public void Visit_TracksBackHistoryAndClearsForward()
    {
        var history = new NavigationHistory<string>();
        history.Visit("a");
        history.Visit("b");
        history.Visit("c");
        history.MoveBack();

        Assert.Equal("b", history.Current);
        Assert.True(history.CanGoForward);

        history.Visit("d");

        Assert.Equal("d", history.Current);
        Assert.False(history.CanGoForward);
        Assert.Equal("b", history.PeekBack());
    }

    [Fact]
    public void MoveBackAndForward_RoundTrip()
    {
        var history = new NavigationHistory<string>();
        history.Visit("a");
        history.Visit("b");

        history.MoveBack();
        Assert.Equal("a", history.Current);
        Assert.False(history.CanGoBack);
        Assert.Equal("b", history.PeekForward());

        history.MoveForward();
        Assert.Equal("b", history.Current);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void Visit_SameLocationTwice_DoesNotAddEntry()
    {
        var history = new NavigationHistory<string>();
        history.Visit("a");
        history.Visit("a");

        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Visit_DropsOldestBeyondCapacity()
    {
        var history = new NavigationHistory<string>(capacity: 2);
        foreach (string entry in new[] { "a", "b", "c", "d" })
            history.Visit(entry);

        history.MoveBack();
        history.MoveBack();

        Assert.Equal("b", history.Current);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void MoveBack_WithoutHistory_Throws()
    {
        var history = new NavigationHistory<string>();
        Assert.Throws<InvalidOperationException>(history.MoveBack);
    }
}
