using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Tests;

public class SpringTests
{
    private const double Dt = 1.0 / 60;

    private static List<double> Run(ref CriticalSpring spring, double target, double seconds, double dt = Dt)
    {
        var values = new List<double>();
        var steps = (int)Math.Round(seconds / dt);
        for (var i = 0; i < steps; i++)
        {
            spring.Step(target, dt);
            values.Add(spring.Value);
        }

        return values;
    }

    private static List<double> Run(ref DampedSpring spring, double target, double seconds, double dt = Dt)
    {
        var values = new List<double>();
        var steps = (int)Math.Round(seconds / dt);
        for (var i = 0; i < steps; i++)
        {
            spring.Step(target, dt);
            values.Add(spring.Value);
        }

        return values;
    }

    [Fact]
    public void CriticalSpringSettlesWithinTwoPercentInAboutItsSettleTime()
    {
        var spring = new CriticalSpring(0.6);
        var values = Run(ref spring, 1, 0.6);

        // The settle factor targets 2 % at exactly 0.6 s; allow a hair of slack for the constant's rounding.
        Assert.InRange(1 - values[^1], 0, 0.0201);

        Run(ref spring, 1, 0.05);
        Assert.InRange(1 - spring.Value, 0, 0.02);
    }

    [Fact]
    public void CriticalSpringIsNotSettledEarly()
    {
        var spring = new CriticalSpring(0.6);
        Run(ref spring, 1, 0.3);
        Assert.True(1 - spring.Value > 0.1, "A 0.6 s spring should still be travelling at 0.3 s");
    }

    [Fact]
    public void CriticalSpringNeverOvershootsAndRisesMonotonically()
    {
        var spring = new CriticalSpring(0.6);
        var values = Run(ref spring, 1, 3);
        var previous = 0.0;
        foreach (var v in values)
        {
            Assert.True(v <= 1 + 1e-12, $"Overshoot: {v}");
            Assert.True(v >= previous - 1e-12, "Value went backwards");
            previous = v;
        }

        Assert.True(spring.IsSettled(1));
    }

    [Fact]
    public void CriticalSpringResultDoesNotDependOnStepSize()
    {
        var fine = new CriticalSpring(0.6);
        var coarse = new CriticalSpring(0.6);
        Run(ref fine, 1, 0.5, 1.0 / 240);
        Run(ref coarse, 1, 0.5, 0.125);
        Assert.Equal(fine.Value, coarse.Value, 9);
        Assert.Equal(fine.Velocity, coarse.Velocity, 9);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(1_000_000)]
    public void CriticalSpringIsStableForHugeSteps(double dt)
    {
        var spring = new CriticalSpring(0.6, initial: -3);
        spring.Step(2, dt);
        Assert.True(double.IsFinite(spring.Value));
        Assert.True(double.IsFinite(spring.Velocity));
        Assert.Equal(2, spring.Value, 9);
        Assert.Equal(0, spring.Velocity, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void CriticalSpringIgnoresInvalidSteps(double dt)
    {
        var spring = new CriticalSpring(0.6, initial: 0.25);
        spring.Step(1, dt);
        Assert.Equal(0.25, spring.Value);
        Assert.Equal(0, spring.Velocity);
    }

    [Fact]
    public void CriticalSpringIgnoresNonFiniteTarget()
    {
        var spring = new CriticalSpring(0.6, initial: 0.25);
        spring.Step(double.NaN, Dt);
        spring.Step(double.PositiveInfinity, Dt);
        Assert.Equal(0.25, spring.Value);
    }

    [Fact]
    public void CriticalSpringSnapStopsMotion()
    {
        var spring = new CriticalSpring(0.6);
        Run(ref spring, 1, 0.1);
        Assert.NotEqual(0, spring.Velocity);
        spring.Snap(0.4);
        Assert.Equal(0.4, spring.Value);
        Assert.Equal(0, spring.Velocity);
        Assert.True(spring.IsSettled(0.4));
        Assert.False(spring.IsSettled(0.5));
    }

    [Fact]
    public void CriticalSpringGuardsAgainstZeroSettleTime()
    {
        var spring = new CriticalSpring(0);
        Assert.True(double.IsFinite(spring.Omega));
        spring.Step(1, Dt);
        Assert.Equal(1, spring.Value, 6);
    }

    [Fact]
    public void LayoutSpringUsesDesignParameters()
    {
        var spring = DampedSpring.Layout(0.5);
        Assert.Equal(2 * Math.PI / 0.45, spring.Omega, 12);
        Assert.Equal(0.82, spring.Damping);
        Assert.Equal(0.5, spring.Value);
    }

    [Fact]
    public void UnderdampedSpringConvergesWithSlightOvershoot()
    {
        var spring = new DampedSpring(0.45, 0.82);
        var values = Run(ref spring, 1, 3);
        var max = values.Max();
        Assert.True(max > 1, "Damping 0.82 should overshoot a little");
        Assert.True(max < 1.05, $"Overshoot too large: {max}");
        Assert.True(spring.IsSettled(1));
        Assert.Equal(1, values[(int)Math.Round(1.5 / Dt)], 2);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void CriticalAndOverdampedSpringsConvergeWithoutOvershoot(double damping)
    {
        var spring = new DampedSpring(0.45, damping);
        var values = Run(ref spring, 1, 8);
        Assert.All(values, v => Assert.True(v <= 1 + 1e-9, $"Overshoot: {v}"));
        Assert.Equal(1, spring.Value, 3);
    }

    [Fact]
    public void DampedSpringConvergesDownwardsToo()
    {
        var spring = new DampedSpring(0.45, 0.82, initial: 1);
        Run(ref spring, -2, 4);
        Assert.Equal(-2, spring.Value, 4);
    }

    [Theory]
    [InlineData(0.82)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void DampedSpringIsStableForHugeSteps(double damping)
    {
        var spring = new DampedSpring(0.45, damping, initial: 5);
        spring.Kick(40);
        spring.Step(1, 1_000);
        Assert.True(double.IsFinite(spring.Value));
        Assert.Equal(1, spring.Value, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void DampedSpringIgnoresInvalidSteps(double dt)
    {
        var spring = new DampedSpring(0.45, 0.82, initial: 0.3);
        spring.Step(1, dt);
        spring.Step(double.NaN, Dt);
        Assert.Equal(0.3, spring.Value);
    }

    [Fact]
    public void KickAddsVelocityAndSnapClearsIt()
    {
        var spring = new DampedSpring(0.45, 0.82);
        spring.Kick(3);
        Assert.Equal(3, spring.Velocity);
        spring.Step(0, Dt);
        Assert.True(spring.Value > 0);
        spring.Snap(0);
        Assert.Equal(0, spring.Velocity);
        Assert.True(spring.IsSettled(0));
    }

    [Fact]
    public void NegativeDampingIsTreatedAsZero()
    {
        var spring = new DampedSpring(0.45, -1);
        Assert.Equal(0, spring.Damping);
    }
}
