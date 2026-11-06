using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Tests;

public class MotionFramesTests
{
    private const double Frame = 1 / 72.0;

    [Fact]
    public void StartingRunningFramesKeepsTheStep()
    {
        var frames = new MotionFrames();
        Assert.True(frames.Start(0));
        var now = 0.0;
        for (var frame = 1; frame <= 20; frame++)
        {
            // A 1000 Hz mouse restarts the frames on every move between two frames.
            for (var move = now + 0.001; move < frame * Frame; move += 0.001)
            {
                Assert.False(frames.Start(move));
            }

            now = frame * Frame;
            Assert.Equal(Frame, frames.Step(now), 9);
        }
    }

    [Fact]
    public void FollowSpringSettlesInItsTimeWhateverThePointerRate()
    {
        foreach (var moveHz in new[] { 125.0, 1000.0 })
        {
            var frames = new MotionFrames();
            var spring = new CriticalSpring(0.12);
            frames.Start(0);
            var now = 0.0;
            var lastMove = 0.0;
            while (now < 0.15)
            {
                now += Frame;
                while (lastMove + 1 / moveHz <= now)
                {
                    lastMove += 1 / moveHz;
                    frames.Start(lastMove);
                }

                spring.Step(100, frames.Step(now));
            }

            Assert.InRange(spring.Value, 98, 100);
        }
    }

    [Fact]
    public void StoppedFramesRestartFromTheNewTime()
    {
        var frames = new MotionFrames();
        frames.Start(0);
        frames.Step(Frame);
        frames.Stop();
        Assert.False(frames.Running);
        Assert.True(frames.Start(5));
        Assert.Equal(Frame, frames.Step(5 + Frame), 9);
    }

    [Fact]
    public void StepsAreClampedAfterAStall()
    {
        var frames = new MotionFrames();
        frames.Start(1);
        Assert.Equal(0.05, frames.Step(3), 9);
        Assert.Equal(0, frames.Step(2), 9);
    }
}
