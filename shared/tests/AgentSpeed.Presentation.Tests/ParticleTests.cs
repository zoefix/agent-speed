using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Particles;

namespace AgentSpeed.Presentation.Tests;

public class ParticleTests
{
    private const double Dt = 1.0 / 60;

    private static EmissionParameters Emission(double sparkRate, double emberRate, double heat = 1, double overdrive = 0.5) =>
        new(heat, overdrive, sparkRate, emberRate, GaugeLayout.AngleForFraction(heat), GaugeLayout.StartDegrees);

    private static void AssertInsideBound(ParticleSystem system)
    {
        foreach (var p in system.Particles)
        {
            var distance = Math.Sqrt((double)p.X * p.X + (double)p.Y * p.Y);
            Assert.True(distance <= ParticleSystem.Bound + 1e-6, $"Particle escaped to {distance}");
            Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y));
        }
    }

    private static Particle[] Copy(ParticleSystem system) => system.Particles.ToArray();

    [Fact]
    public void PoolNeverExceedsCapacityUnderExtremeEmission()
    {
        var system = new ParticleSystem(seed: 7);
        Assert.Equal(ParticleSystem.DefaultCapacity, system.Capacity);

        for (var frame = 0; frame < 600; frame++)
        {
            system.Emit(Emission(50_000, 50_000), Dt);
            Assert.InRange(system.Count, 0, 400);
            system.Step(Dt);
            Assert.InRange(system.Count, 0, 400);
        }

        system.Emit(Emission(50_000, 50_000), Dt);
        Assert.Equal(400, system.Count);
    }

    [Fact]
    public void LiveParticlesStayWithinBoundAfterEveryStep()
    {
        var system = new ParticleSystem(seed: 11);
        for (var frame = 0; frame < 900; frame++)
        {
            system.Emit(Emission(192, 144), Dt);
            AssertInsideBound(system);
            system.Step(Dt);
            AssertInsideBound(system);
        }

        Assert.True(system.Count > 0);
    }

    [Fact]
    public void LargeStepsAreClampedAndStillRespectBound()
    {
        var system = new ParticleSystem(seed: 3);
        for (var frame = 0; frame < 50; frame++)
        {
            system.Emit(Emission(400, 200), 5);
            system.Step(5);
            AssertInsideBound(system);
        }
    }

    [Fact]
    public void SameSeedProducesIdenticalParticles()
    {
        var a = new ParticleSystem(seed: 42);
        var b = new ParticleSystem(seed: 42);
        var c = new ParticleSystem(seed: 43);
        for (var frame = 0; frame < 120; frame++)
        {
            a.Emit(Emission(120, 60), Dt);
            b.Emit(Emission(120, 60), Dt);
            c.Emit(Emission(120, 60), Dt);
            a.Step(Dt);
            b.Step(Dt);
            c.Step(Dt);
        }

        Assert.True(a.Count > 0);
        Assert.Equal(Copy(a), Copy(b));
        Assert.NotEqual(Copy(a), Copy(c));
    }

    [Fact]
    public void EmissionRateAccumulatesFractionalDebt()
    {
        var system = new ParticleSystem(seed: 1);
        system.Emit(Emission(30, 0), 1.0 / 64);
        Assert.Equal(0, system.Count);
        system.Emit(Emission(32, 0), 1.0 / 64);
        Assert.Equal(0, system.Count);
        system.Emit(Emission(32, 0), 1.0 / 64);
        Assert.Equal(1, system.Count);

        system.Clear();
        system.Emit(Emission(160, 0), 0.0625);
        Assert.Equal(10, system.Count);
    }

    [Fact]
    public void ZeroRateDropsAccumulatedDebt()
    {
        var system = new ParticleSystem(seed: 1);
        system.Emit(Emission(32, 0), 1.0 / 64);
        system.Emit(Emission(0, 0), 1.0 / 64);
        system.Emit(Emission(32, 0), 1.0 / 64);
        Assert.Equal(0, system.Count);
    }

    [Fact]
    public void ClearRemovesParticlesAndDebt()
    {
        var system = new ParticleSystem(seed: 5);
        system.Emit(Emission(100, 100), 0.05);
        system.Emit(Emission(32, 32), 1.0 / 64);
        Assert.True(system.Count > 0);

        system.Clear();
        Assert.Equal(0, system.Count);
        Assert.True(system.Particles.IsEmpty);

        system.Emit(Emission(32, 32), 1.0 / 64);
        Assert.Equal(0, system.Count);
    }

    [Fact]
    public void ParticlesDieAfterTheirLife()
    {
        var system = new ParticleSystem(seed: 9);
        system.Emit(Emission(160, 0), 0.0625);
        system.Emit(Emission(0, 160), 0.0625);
        Assert.Equal(20, system.Count);

        for (var frame = 0; frame < 18; frame++)
        {
            system.Step(Dt);
        }

        Assert.Equal(20, system.Count);
        foreach (var p in system.Particles)
        {
            Assert.InRange(p.Progress, 0.01f, 0.99f);
        }

        for (var frame = 0; frame < 60; frame++)
        {
            system.Step(Dt);
            foreach (var p in system.Particles)
            {
                Assert.True(p.Age < p.Life);
            }
        }

        Assert.All(Copy(system), p => Assert.Equal(ParticleKind.Ember, p.Kind));

        for (var frame = 0; frame < 60; frame++)
        {
            system.Step(Dt);
        }

        Assert.Equal(0, system.Count);
    }

    [Fact]
    public void EmittedParticlesHaveDesignedRanges()
    {
        var system = new ParticleSystem(seed: 21);
        system.Emit(Emission(1600, 0, heat: 0.8), 0.1);
        system.Emit(Emission(0, 1600, heat: 0.8), 0.1);
        Assert.Equal(320, system.Count);

        foreach (var p in system.Particles)
        {
            Assert.Equal(0, p.Age);
            Assert.InRange(p.Heat, 0f, 1f);
            Assert.InRange(p.Seed, 0f, 1f);
            if (p.Kind == ParticleKind.Spark)
            {
                Assert.InRange(p.Life, 0.5f, 1.2f);
                Assert.InRange(p.Size, 1.2f, 2.8f);
                var radius = Math.Sqrt((double)p.X * p.X + (double)p.Y * p.Y);
                Assert.InRange(radius, GaugeLayout.Radius - 0.016, GaugeLayout.Radius + 0.031);
            }
            else
            {
                Assert.InRange(p.Life, 0.8f, 1.6f);
                Assert.True(p.Y < 0, "Embers spawn around the upper half of the ring");
                Assert.True(p.VelocityY < 0, "Embers rise");
            }
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidTimeStepsAreIgnored(double dt)
    {
        var system = new ParticleSystem(seed: 2);
        system.Emit(Emission(1000, 1000), dt);
        Assert.Equal(0, system.Count);

        system.Emit(Emission(160, 0), 0.0625);
        var before = Copy(system);
        system.Step(dt);
        Assert.Equal(before, Copy(system));
    }

    [Fact]
    public void NegativeRatesEmitNothing()
    {
        var system = new ParticleSystem(seed: 2);
        system.Emit(Emission(-500, -500), 0.1);
        Assert.Equal(0, system.Count);
    }

    [Fact]
    public void TryAddRespectsCapacity()
    {
        var system = new ParticleSystem(capacity: 2, seed: 1);
        var particle = new Particle { Life = 1 };
        Assert.True(system.TryAdd(particle));
        Assert.True(system.TryAdd(particle));
        Assert.False(system.TryAdd(particle));
        Assert.Equal(2, system.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(401)]
    public void InvalidCapacityThrows(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleSystem(capacity));
    }

    [Fact]
    public void ProgressHandlesZeroLife()
    {
        Assert.Equal(1, new Particle { Life = 0, Age = 0 }.Progress);
        Assert.Equal(0.5f, new Particle { Life = 2, Age = 1 }.Progress);
        Assert.Equal(1, new Particle { Life = 1, Age = 5 }.Progress);
    }
}
