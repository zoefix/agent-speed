using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

public class WidthHoldTests
{
    [Fact]
    public void FirstResolveUsesTheDesiredWidth()
    {
        var hold = new WidthHold();
        Assert.Equal(80, hold.Resolve(80, 0));
        Assert.Null(hold.PendingSince);
    }

    [Fact]
    public void GrowsImmediately()
    {
        var hold = new WidthHold();
        hold.Resolve(60, 0);
        Assert.Equal(90, hold.Resolve(90, 10));
        Assert.Equal(90, hold.Applied);
    }

    [Fact]
    public void ShrinksOnlyAfterTheShorterWidthHeldForTheDelay()
    {
        var hold = new WidthHold(6000);
        hold.Resolve(90, 0);
        Assert.Equal(90, hold.Resolve(60, 1000));
        Assert.Equal(1000, hold.PendingSince);
        Assert.Equal(7000, hold.DueAt);
        Assert.Equal(90, hold.Resolve(60, 6999));
        Assert.Equal(60, hold.Resolve(60, 7000));
        Assert.Null(hold.PendingSince);
    }

    [Fact]
    public void GoingBackUpCancelsThePendingShrink()
    {
        var hold = new WidthHold(6000);
        hold.Resolve(90, 0);
        hold.Resolve(60, 1000);
        Assert.Equal(90, hold.Resolve(90, 3000));
        Assert.Null(hold.PendingSince);

        Assert.Equal(90, hold.Resolve(60, 8000));
        Assert.Equal(8000, hold.PendingSince);
    }

    [Fact]
    public void ForceAppliesAtOnce()
    {
        var hold = new WidthHold(6000);
        hold.Resolve(90, 0);
        Assert.Equal(40, hold.Resolve(40, 1, force: true));
    }

    [Fact]
    public void PeekDoesNotChangeState()
    {
        var hold = new WidthHold(6000);
        hold.Resolve(90, 0);
        Assert.Equal(90, hold.Peek(60, 100));
        Assert.Null(hold.PendingSince);
        Assert.Equal(120, hold.Peek(120, 100));
        Assert.Equal(90, hold.Applied);
    }
}
