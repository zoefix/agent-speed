using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The fixed particle and speed-line pools of DESIGN 4c.</summary>
public class HeroParticleTests
{
    private static readonly PointD At = new(30, 70);
    private static readonly PointD Back = new(-1, 0);

    [Fact]
    public void BurstIsCappedAndFillsFreeSlots()
    {
        var pool = new HeroParticlePool();
        var random = new HeroRandom(3);

        pool.Burst(HeroParticleKind.Puff, At, Back, 10, 6, random);
        Assert.Equal(6, pool.AliveCount);

        pool.Burst(HeroParticleKind.Puff, At, Back, 100, int.MaxValue, random);
        Assert.Equal(HeroParticlePool.Capacity, pool.AliveCount);

        pool.Clear();
        Assert.Equal(0, pool.AliveCount);
    }

    [Fact]
    public void PuffsGrowAndFadeWithinTheirLife()
    {
        var pool = new HeroParticlePool();
        pool.Burst(HeroParticleKind.Puff, At, Back, 6, 6, new HeroRandom(5));
        foreach (var p in pool.Items)
        {
            if (!p.Alive)
            {
                continue;
            }

            Assert.InRange(p.Life, 0.5, 0.85);
            Assert.InRange(p.Grow, 2.6, 4.2);
            Assert.InRange(p.Size, 0.8, 1.3);
            Assert.True(p.VelocityX < 0, "dust blows back");
            Assert.Equal(0, p.Progress);
        }

        for (var i = 0; i < 30; i++)
        {
            pool.Step(1.0 / 60, 60);
        }

        foreach (var p in pool.Items)
        {
            if (p.Alive)
            {
                Assert.InRange(p.Progress, 0.5 / 0.85, 1);
                Assert.True(p.X < At.X);
            }
        }

        for (var i = 0; i < 30; i++)
        {
            pool.Step(1.0 / 60, 60);
        }

        Assert.Equal(0, pool.AliveCount);
    }

    [Fact]
    public void ArcsWaitForTheirDelay()
    {
        var pool = new HeroParticlePool();
        pool.Burst(HeroParticleKind.Arc, At, default, 7, 7, new HeroRandom(9));
        var delayed = 0;
        foreach (var p in pool.Items)
        {
            if (p.Alive && p.Delay > 0)
            {
                Assert.Equal(-1, p.Progress);
                delayed++;
            }
        }

        Assert.Equal(6, delayed);
        pool.Step(0.1, 0);
        Assert.Equal(7, pool.AliveCount);
        pool.Step(0.4, 0);
        Assert.Equal(0, pool.AliveCount);
    }

    [Fact]
    public void StreamCarriesFractionsToTheNextFrame()
    {
        var pool = new HeroParticlePool();
        var random = new HeroRandom(11);
        for (var i = 0; i < 60; i++)
        {
            pool.Stream(At, Back, 30, 70, 0.5, 0, 10, 1.0 / 60, random);
        }

        Assert.Equal(30, pool.AliveCount);
        foreach (var p in pool.Items)
        {
            if (p.Alive)
            {
                Assert.Equal(HeroParticleKind.Spark, p.Kind);
                Assert.InRange(p.Life, 7, 13);
            }
        }
    }

    [Fact]
    public void StreamStopsAtTheCapacity()
    {
        var pool = new HeroParticlePool();
        var random = new HeroRandom(11);
        pool.Stream(At, Back, 10_000, 70, 0.5, 0, 10, 0.1, random);
        Assert.Equal(HeroParticlePool.Capacity, pool.AliveCount);
    }

    [Fact]
    public void SparksFallWithGravity()
    {
        var pool = new HeroParticlePool();
        pool.Stream(At, Back, 10, 120, 0, 260, 1, 0.1, new HeroRandom(2));
        var before = pool.Items.ToArray().Single(p => p.Alive);
        pool.Step(0.1, 0);
        var after = pool.Items.ToArray().Single(p => p.Alive);
        Assert.Equal(before.VelocityY + 26, after.VelocityY, 9);
        Assert.True(after.X < before.X);
    }

    [Fact]
    public void SpeedLinesPeelOffTheVehicleTail()
    {
        var car = HeroDriver.Art.Vehicles[HeroDriver.Art.IndexOf("car")];
        var pool = new HeroStreakPool();
        var random = new HeroRandom(4);
        for (var i = 0; i < HeroStreakPool.Capacity + 4; i++)
        {
            pool.Spawn(false, car, 0.5, 200, random);
        }

        Assert.Equal(HeroStreakPool.Capacity, pool.AliveCount);
        var bounds = car.Bounds;
        foreach (var s in pool.Items)
        {
            Assert.Equal(-1, s.DirectionX);
            Assert.Equal(0, s.DirectionY);
            Assert.InRange(s.Y, 16, 84);
            var crosses = s.Y > bounds.Y - 2 && s.Y < bounds.Bottom + 2;
            Assert.True(crosses ? s.X == bounds.X - 2 : s.X >= 102, $"y = {s.Y:F1}, x = {s.X:F1}");
            Assert.InRange(s.Speed, 1.25 * 200 * 0.85, 1.25 * 200 * 1.15);
            Assert.InRange(s.Length, (6 + 20 * 0.5) * 0.7, (6 + 20 * 0.5) * 1.3);
        }
    }

    [Fact]
    public void RocketSpeedLinesRunAgainstItsHeading()
    {
        var rocket = HeroDriver.Art.Vehicles[^1];
        var pool = new HeroStreakPool();
        pool.Spawn(true, rocket, 1, 900, new HeroRandom(4));
        var s = pool.Items.ToArray().Single(x => x.Alive);
        var heading = -48 * Math.PI / 180;

        Assert.True(s.Burst);
        Assert.Equal(-Math.Cos(heading), s.DirectionX, 12);
        Assert.Equal(-Math.Sin(heading), s.DirectionY, 12);
    }

    [Fact]
    public void SpeedLinesTopUpToTheCapAndLeaveTheDisc()
    {
        var car = HeroDriver.Art.Vehicles[HeroDriver.Art.IndexOf("car")];
        var pool = new HeroStreakPool();
        var random = new HeroRandom(4);
        var most = 0;
        for (var i = 0; i < 600; i++)
        {
            pool.Step(1.0 / 60, 3, car, 0.5, 200, random);
            Assert.InRange(pool.AliveCount, 0, 3);
            most = Math.Max(most, pool.AliveCount);
        }

        Assert.True(most >= 2, $"lines keep coming ({most})");
        for (var i = 0; i < 120; i++)
        {
            pool.Step(1.0 / 60, 0, car, 0.5, 200, random);
        }

        Assert.Equal(0, pool.AliveCount);
    }
}
