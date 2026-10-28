using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Tests;

public class HeroMathTests
{
    [Fact]
    public void EasingsHitTheirEndpointsAndClamp()
    {
        Func<double, double>[] easings = [HeroMath.Smooth, HeroMath.EaseInCubic, HeroMath.EaseOutCubic, HeroMath.EaseInOutSine];
        foreach (var ease in easings)
        {
            Assert.Equal(0, ease(0), 12);
            Assert.Equal(1, ease(1), 12);
            Assert.Equal(0, ease(-3), 12);
            Assert.Equal(1, ease(7), 12);
            Assert.Equal(0, ease(double.NaN), 12);
            Assert.Equal(0, ease(double.NegativeInfinity), 12);
            Assert.Equal(1, ease(double.PositiveInfinity), 12);
            var previous = 0.0;
            for (var t = 0.0; t <= 1; t += 0.01)
            {
                var value = ease(t);
                Assert.True(value >= previous - 1e-12, "Easings rise monotonically.");
                previous = value;
            }
        }
    }

    [Fact]
    public void EasingMidpoints()
    {
        Assert.Equal(0.5, HeroMath.Smooth(0.5), 12);
        Assert.Equal(0.125, HeroMath.EaseInCubic(0.5), 12);
        Assert.Equal(0.875, HeroMath.EaseOutCubic(0.5), 12);
        Assert.Equal(0.5, HeroMath.EaseInOutSine(0.5), 12);
        Assert.Equal(0.25 * 0.25 * (3 - 0.5), HeroMath.Smooth(0.25), 12);
        Assert.Equal(7.5, HeroMath.Lerp(5, 10, 0.5), 12);
    }

    [Theory]
    [InlineData(0, 0.18, 0.6)]
    [InlineData(0.12, 0.34, 0.8)]
    [InlineData(0.16, 0.2, 0.3)]
    public void BumpRisesToOneAtThePeakAndFallsBackToZero(double start, double peak, double end)
    {
        Assert.Equal(0, HeroMath.Bump(start - 0.05, start, peak, end), 12);
        Assert.Equal(0, HeroMath.Bump(start, start, peak, end), 12);
        Assert.Equal(0.5, HeroMath.Bump((start + peak) / 2, start, peak, end), 12);
        Assert.Equal(1, HeroMath.Bump(peak, start, peak, end), 12);
        Assert.Equal(0.5, HeroMath.Bump((peak + end) / 2, start, peak, end), 12);
        Assert.Equal(0, HeroMath.Bump(end, start, peak, end), 12);
        Assert.Equal(0, HeroMath.Bump(end + 5, start, peak, end), 12);
    }

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-0.5, 0)]
    [InlineData(-1.5, -1)]
    [InlineData(0.49, 0)]
    [InlineData(1.5, 2)]
    [InlineData(9.758, 10)]
    [InlineData(6, 6)]
    public void RoundHalfUpMatchesJavaScript(double value, int expected) => Assert.Equal(expected, HeroMath.RoundHalfUp(value));

    [Fact]
    public void SpringStartsAtFromAndSettlesAtTo()
    {
        Assert.Equal(-62, HeroMath.SpringAt(0, -62, 0, 140, 0.5, 0.8));
        Assert.Equal(-62, HeroMath.SpringAt(-1, -62, 0, 140, 0.5, 0.8));
        Assert.Equal(0, HeroMath.SpringAt(10, -62, 0, 140, 0.5, 0.8), 6);
        Assert.Equal(1, HeroMath.SpringAt(10, 0, 1, 9, 0.32, 0.55), 6);
        Assert.Equal(140, HeroMath.SpringVelocityAt(0, -62, 0, 140, 0.5, 0.8));
    }

    [Fact]
    public void UpgradeSwoopOvershootsByAboutOneUnit()
    {
        var max = double.MinValue;
        var at = 0.0;
        for (var t = 0.0; t < 3; t += 0.0005)
        {
            var x = HeroMath.SpringAt(t, -62, 0, 140, 0.5, 0.8);
            if (x > max)
            {
                max = x;
                at = t;
            }
        }

        Assert.InRange(max, 0.3, 3);
        Assert.InRange(at, 0.3, 0.5);
    }

    [Theory]
    [InlineData(-62, 0, 140, 0.5, 0.8)]
    [InlineData(0, 1, 9, 0.32, 0.55)]
    [InlineData(0, 1, 8, 0.36, 0.5)]
    public void SpringVelocityIsTheDerivativeOfTheSpring(double from, double to, double velocity, double response, double damping)
    {
        const double h = 1e-6;
        for (var t = 0.01; t < 1.5; t += 0.037)
        {
            var numeric = (HeroMath.SpringAt(t + h, from, to, velocity, response, damping)
                           - HeroMath.SpringAt(t - h, from, to, velocity, response, damping)) / (2 * h);
            var analytic = HeroMath.SpringVelocityAt(t, from, to, velocity, response, damping);
            Assert.True(Math.Abs(numeric - analytic) <= 1e-4 * Math.Max(1, Math.Abs(analytic)), $"t = {t}: {numeric} vs {analytic}");
        }
    }

    [Fact]
    public void SettleSpringUsesTheDesignOmega()
    {
        Assert.Equal(6.2 / 0.9, new SettleSpring(0.9).Omega, 12);
        Assert.Equal(6.2 / 0.5, new SettleSpring(0.5).Omega, 12);
        var spring = new SettleSpring(0.9, initial: 3);
        Assert.Equal(3, spring.Value);
        Assert.Equal(0, spring.Velocity);
    }

    [Fact]
    public void SettleSpringStepMatchesTheClosedForm()
    {
        var spring = new SettleSpring(0.9, initial: 10);
        var w = 6.2 / 0.9;

        // DESIGN 4c: c1 = G − G*, c2 = a + ω c1, G ← G* + (c1 + c2 Δt) e^(−ω Δt), a ← (c2 − ω (c1 + c2 Δt)) e^(−ω Δt)
        double value = 10, velocity = 0;
        foreach (var dt in new[] { 0.05, 0.2, 1.0 / 60 })
        {
            spring.Step(40, dt);
            var c1 = value - 40;
            var c2 = velocity + w * c1;
            var e = Math.Exp(-w * dt);
            value = 40 + (c1 + c2 * dt) * e;
            velocity = (c2 - w * (c1 + c2 * dt)) * e;

            Assert.Equal(value, spring.Value, 12);
            Assert.Equal(velocity, spring.Velocity, 12);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.37)]
    [InlineData(1.2)]
    public void SettleSpringIsExactForAnyFrameTime(double seconds)
    {
        var one = new SettleSpring(0.9);
        one.Step(1, seconds);

        var many = new SettleSpring(0.9);
        var steps = (int)Math.Round(seconds / 0.001);
        for (var i = 0; i < steps; i++)
        {
            many.Step(1, seconds / steps);
        }

        Assert.True(Math.Abs(one.Value - many.Value) < 1e-9, $"{one.Value} vs {many.Value}");
        Assert.True(Math.Abs(one.Velocity - many.Velocity) < 1e-9, $"{one.Velocity} vs {many.Velocity}");
    }

    [Fact]
    public void SettleSpringResidualAfterSettleIsTheFormulaResidual()
    {
        var spring = new SettleSpring(0.9);
        for (var i = 0; i < 54; i++)
        {
            spring.Step(1, 0.9 / 54);
        }

        // ω t = 6.2: the remaining fraction of a step is (1 + 6.2) e^−6.2 ≈ 1.46 %.
        var residual = 1 - spring.Value;
        Assert.Equal((1 + 6.2) * Math.Exp(-6.2), residual, 9);
        Assert.InRange(residual, 0.0145, 0.0147);
        Assert.True(spring.Velocity > 0);
    }

    [Fact]
    public void SettleSpringNeverOvershoots()
    {
        var spring = new SettleSpring(0.5);
        for (var i = 0; i < 300; i++)
        {
            spring.Step(1, 1.0 / 60);
            Assert.InRange(spring.Value, 0, 1);
        }

        Assert.True(spring.IsSettled(1, 1e-6));
    }

    [Fact]
    public void SettleSpringIgnoresInvalidSteps()
    {
        var spring = new SettleSpring(0.9, initial: 5);
        spring.Step(double.NaN, 0.1);
        spring.Step(10, 0);
        spring.Step(10, -1);
        spring.Step(10, double.PositiveInfinity);
        Assert.Equal(5, spring.Value);
        Assert.Equal(0, spring.Velocity);

        spring.Step(10, 0.1);
        spring.Snap(2);
        Assert.Equal(2, spring.Value);
        Assert.Equal(0, spring.Velocity);
        Assert.True(spring.IsSettled(2, 1e-9));
        Assert.False(spring.IsSettled(3, 0.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(-7)]
    [InlineData(6.283185307179586)]
    [InlineData(1e7)]
    [InlineData(-1e7)]
    [InlineData(123456.789)]
    public void WrapRadiansStaysInOneTurnAndKeepsTheAngle(double angle)
    {
        var wrapped = HeroMath.WrapRadians(angle);
        Assert.InRange(wrapped, 0, 2 * Math.PI);
        Assert.True(wrapped < 2 * Math.PI);
        Assert.Equal(Math.Cos(angle), Math.Cos(wrapped), 6);
        Assert.Equal(Math.Sin(angle), Math.Sin(wrapped), 6);
    }

    [Fact]
    public void RandomIsTheXorshiftOfThePrototype()
    {
        // rng(7) of the prototype's engine.js.
        var random = new HeroRandom(7);
        Assert.Equal(0.00044065131805837154, random.Next(), 15);
        Assert.Equal(0.10952103300951421, random.Next(), 15);
        Assert.Equal(0.9038964069914073, random.Next(), 15);
    }

    [Fact]
    public void RandomIsDeterministicAndInRange()
    {
        var a = new HeroRandom(42);
        var b = new HeroRandom(42);
        var c = new HeroRandom(43);
        var differs = false;
        for (var i = 0; i < 10_000; i++)
        {
            var x = a.Next();
            Assert.Equal(x, b.Next());
            Assert.InRange(x, 0, 1);
            Assert.True(x < 1);
            differs |= x != c.Next();
        }

        Assert.True(differs);

        var zero = new HeroRandom(0);
        var one = new HeroRandom(1);
        Assert.Equal(one.Next(), zero.Next());
    }
}
