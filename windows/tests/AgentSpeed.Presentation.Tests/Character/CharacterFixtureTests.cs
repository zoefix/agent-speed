using System.Globalization;
using System.Text.Json;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>The shared fixtures of spec/CHARACTERS.md, section 9.</summary>
internal static class CharacterFixture
{
    public static JsonElement Json(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(RepositoryPaths.FixtureFile("characters/" + name)));
        return doc.RootElement.Clone();
    }

    public static double[] Doubles(JsonElement e) => e.EnumerateArray().Select(v => v.GetDouble()).ToArray();

    public static CurveKey[] Keys(JsonElement e) => e.EnumerateArray().Select(k => new CurveKey(k[0].GetDouble(), k[1].GetDouble())).ToArray();

    public static bool Close(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance * Math.Max(1, Math.Abs(b));
}

public class CharacterFixtureTests
{
    [Fact]
    public void RandomNumbersMatchSplitMix64()
    {
        foreach (var c in CharacterFixture.Json("rng.json").GetProperty("cases").EnumerateArray())
        {
            var seed = ulong.Parse(c.GetProperty("seed").GetString()!, CultureInfo.InvariantCulture);
            var g = new Rng(seed);
            var expected = c.GetProperty("u64").EnumerateArray().Select(v => ulong.Parse(v.GetString()!, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(expected, Enumerable.Range(0, 5).Select(_ => g.NextUInt64()).ToArray());
            g = new Rng(seed);
            foreach (var r in CharacterFixture.Doubles(c.GetProperty("random")))
            {
                Assert.True(CharacterFixture.Close(g.Random(), r, 1e-15));
            }

            g = new Rng(seed);
            Assert.True(CharacterFixture.Close(g.Uniform(2, 5), c.GetProperty("uniform_2_5").GetDouble(), 1e-15));
            g = new Rng(seed);
            var items = Enumerable.Range(0, 10).ToList();
            g.Shuffle(items);
            Assert.Equal(c.GetProperty("shuffle10").EnumerateArray().Select(v => v.GetInt32()), items);
            g = new Rng(seed);
            Assert.Equal(c.GetProperty("sample_abcde_3").EnumerateArray().Select(v => v.GetString()!), g.Sample(["a", "b", "c", "d", "e"], 3));
        }
    }

    [Fact]
    public void CurvesMatchTheReference()
    {
        foreach (var c in CharacterFixture.Json("curves.json").GetProperty("cases").EnumerateArray())
        {
            var curve = new Curve(CharacterFixture.Keys(c.GetProperty("keys")), c.GetProperty("periodic").GetBoolean());
            var xs = CharacterFixture.Doubles(c.GetProperty("x"));
            var ys = CharacterFixture.Doubles(c.GetProperty("y"));
            for (var i = 0; i < xs.Length; i++)
            {
                Assert.True(CharacterFixture.Close(curve.Evaluate(xs[i]), ys[i]), $"x={xs[i]}");
            }
        }
    }

    [Fact]
    public void SpringsMatchTheReference()
    {
        foreach (var c in CharacterFixture.Json("springs.json").GetProperty("cases").EnumerateArray())
        {
            double D(string k) => c.GetProperty(k).GetDouble();
            var s = new SecondOrder(D("f"), D("z"), D("r"), D("x0"));
            var inputs = CharacterFixture.Doubles(c.GetProperty("inputs"));
            var outputs = CharacterFixture.Doubles(c.GetProperty("outputs"));
            for (var i = 0; i < inputs.Length; i++)
            {
                Assert.True(CharacterFixture.Close(s.Update(D("dt"), inputs[i]), outputs[i]), $"step {i}");
            }
        }
    }

    [Fact]
    public void OrbitsAndKeplerMatchTheReference()
    {
        var doc = CharacterFixture.Json("orbits.json");
        foreach (var c in doc.GetProperty("orbits").EnumerateArray())
        {
            double D(string k) => c.GetProperty(k).GetDouble();
            var o = new Orbit(D("a"), D("e"), D("period"), D("phase"), D("omega"), D("height"), D("tilt"), D("precess"), D("seed"));
            var center = CharacterFixture.Doubles(c.GetProperty("center"));
            var positions = c.GetProperty("xyDepth").EnumerateArray().Select(CharacterFixture.Doubles).ToArray();
            var spins = CharacterFixture.Doubles(c.GetProperty("spin"));
            var ts = CharacterFixture.Doubles(c.GetProperty("t"));
            for (var i = 0; i < ts.Length; i++)
            {
                var p = o.At(ts[i], center[0], center[1]);
                var e = positions[i];
                Assert.True(CharacterFixture.Close(p.X, e[0]) && CharacterFixture.Close(p.Y, e[1]) && CharacterFixture.Close(p.Depth, e[2]), $"t={ts[i]}");
                Assert.True(CharacterFixture.Close(o.Spin(ts[i]), spins[i]));
            }
        }

        foreach (var k in doc.GetProperty("kepler").EnumerateArray())
        {
            Assert.True(CharacterFixture.Close(Orbit.EccentricAnomaly(k.GetProperty("mean").GetDouble(), k.GetProperty("e").GetDouble()),
                k.GetProperty("E").GetDouble()));
        }
    }

    [Fact]
    public void RigMatchesTheReference()
    {
        var doc = CharacterFixture.Json("rig.json");
        var rig = new CharacterRig(RigDocument.Decode(doc.GetProperty("rig")));
        var world = Array.Empty<Affine2>();
        var opacity = Array.Empty<double>();
        var skinned = Array.Empty<double>();
        var cases = 0;
        foreach (var c in doc.GetProperty("cases").EnumerateArray())
        {
            var given = c.GetProperty("values");
            var values = rig.Params.Select(p => given.TryGetProperty(p.Id, out var v) ? v.GetDouble() : p.Default).ToArray();
            rig.WorldTransforms(values, ref world);
            var expectedWorld = c.GetProperty("world").EnumerateArray().Select(CharacterFixture.Doubles).ToArray();
            Assert.Equal(expectedWorld.Length, world.Length);
            for (var i = 0; i < world.Length; i++)
            {
                var m = world[i];
                var e = expectedWorld[i];
                Assert.True(CharacterFixture.Close(m.A, e[0]) && CharacterFixture.Close(m.B, e[1]) && CharacterFixture.Close(m.Tx, e[2]));
                Assert.True(CharacterFixture.Close(m.C, e[3]) && CharacterFixture.Close(m.D, e[4]) && CharacterFixture.Close(m.Ty, e[5]));
            }

            var vertices = c.GetProperty("vertices").EnumerateArray().Select(CharacterFixture.Doubles).ToArray();
            for (var i = 0; i < vertices.Length; i++)
            {
                rig.Skin(i, world, ref skinned);
                Assert.Equal(vertices[i].Length, skinned.Length);
                Assert.True(skinned.Zip(vertices[i]).All(p => CharacterFixture.Close(p.First, p.Second)), $"part {i}");
            }

            rig.Opacities(values, ref opacity);
            Assert.True(opacity.Zip(CharacterFixture.Doubles(c.GetProperty("opacity"))).All(p => CharacterFixture.Close(p.First, p.Second)));
            cases++;
        }

        Assert.True(cases > 0);
    }

    [Fact]
    public void PhysicsMatchesTheReference()
    {
        var doc = CharacterFixture.Json("physics.json");
        var spec = RigDocument.DecodeChain(doc.GetProperty("spec"));
        string[] names = ["Sway", "Lift", "Wind", "H1", "H2"];
        foreach (var c in doc.GetProperty("cases").EnumerateArray())
        {
            var physics = new CharacterPhysics([spec], n => Array.IndexOf(names, n) is var i and >= 0 ? i : null, c.GetProperty("windSign").GetDouble());
            var values = new double[names.Length];
            physics.Reset(values);
            var dt = c.GetProperty("dt").GetDouble();
            foreach (var frame in c.GetProperty("frames").EnumerateArray())
            {
                foreach (var p in frame.GetProperty("in").EnumerateObject())
                {
                    values[Array.IndexOf(names, p.Name)] = p.Value.GetDouble();
                }

                physics.Step(dt, values);
                physics.Write(values);
                var output = frame.GetProperty("out");
                Assert.True(CharacterFixture.Close(values[3], output.GetProperty("H1").GetDouble())
                    && CharacterFixture.Close(values[4], output.GetProperty("H2").GetDouble()));
            }
        }
    }

    [Fact]
    public void OdometerMatchesTheReference()
    {
        var doc = CharacterFixture.Json("odometer.json");
        var dt = doc.GetProperty("dt").GetDouble();
        var odo = new Odometer();
        var targets = CharacterFixture.Doubles(doc.GetProperty("targets"));
        var frames = doc.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(targets.Length, frames.Length);
        for (var n = 0; n < frames.Length; n++)
        {
            var text = odo.Update(dt, targets[n]);
            Assert.Equal(frames[n].GetProperty("text").GetString(), text);
            var digits = frames[n].GetProperty("digits").EnumerateArray().ToArray();
            Assert.Equal(digits.Length, odo.Digits.Count);
            for (var i = 0; i < digits.Length; i++)
            {
                var d = odo.Digits[i];
                Assert.Equal(digits[i][0].ValueKind == JsonValueKind.Null ? null : digits[i][0].GetString(), d.Current?.ToString());
                Assert.Equal(digits[i][1].ValueKind == JsonValueKind.Null ? null : digits[i][1].GetString(), d.Previous?.ToString());
                Assert.True(CharacterFixture.Close(d.T, digits[i][2].GetDouble()));
            }
        }
    }

    [Fact]
    public void PoseSwitchMatchesTheReference()
    {
        var doc = CharacterFixture.Json("switch.json");
        var dt = doc.GetProperty("dt").GetDouble();
        var s = new PoseSwitch();
        s.Request(1);
        foreach (var frame in doc.GetProperty("frames").EnumerateArray())
        {
            var (swap, output, _) = s.Update(dt);
            var expected = frame.GetProperty("values");
            Assert.Equal(frame.GetProperty("swap").GetBoolean(), swap is not null);
            if (!expected.EnumerateObject().Any())
            {
                Assert.Null(output);
                continue;
            }

            var o = Assert.NotNull(output);
            Assert.True(CharacterFixture.Close(o.Spin, expected.GetProperty("Spin").GetDouble()));
            Assert.True(CharacterFixture.Close(o.Squash, expected.GetProperty("Squash").GetDouble()));
            Assert.True(CharacterFixture.Close(o.PosY, expected.GetProperty("PosY").GetDouble()));
            Assert.True(CharacterFixture.Close(o.SkirtFlare, expected.GetProperty("SkirtFlare").GetDouble()));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DirectorMatchesTheReference(int index)
    {
        var doc = CharacterFixture.Json("director.json");
        var parameters = doc.GetProperty("params").EnumerateArray().Select(CharacterParam.Decode).ToArray();
        var schedule = doc.GetProperty("schedule").EnumerateArray().Select(s => (T: s[0].GetDouble(), Level: s[1].GetInt32())).ToArray();
        var c = doc.GetProperty("cases")[index];
        var d = new CharacterDirector(parameters, (ulong)c.GetProperty("seed").GetInt64(), CharacterSides.Parse(c.GetProperty("armOut").GetString())!.Value,
            CharacterSides.Parse(c.GetProperty("armBook").GetString())!.Value, c.GetProperty("orb").GetDouble(), celebrate: false);
        var mismatches = Run(d, schedule, c, null);
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DirectorProfilesMatchTheReference(int index)
    {
        var doc = CharacterFixture.Json("director-profiles.json");
        var parameters = doc.GetProperty("params").EnumerateArray().Select(CharacterParam.Decode).ToArray();
        var schedule = doc.GetProperty("schedule").EnumerateArray().Select(s => (T: s[0].GetDouble(), Level: s[1].GetInt32())).ToArray();
        var sides = doc.GetProperty("poseSides").EnumerateArray().Select(s => (T: s[0].GetDouble(), Band: SpeedBands.Parse(s[1].GetString())!.Value)).ToList();
        var c = doc.GetProperty("cases")[index];
        var profile = MotionProfile.Decode(c.GetProperty("profile"));
        var output = CharacterSides.Parse(c.GetProperty("armOut").GetString())!.Value;
        var book = CharacterSides.Parse(c.GetProperty("armBook").GetString())!.Value;
        var d = new CharacterDirector(parameters, (ulong)c.GetProperty("seed").GetInt64(), output, book, c.GetProperty("orb").GetDouble(), celebrate: false,
            profile: profile);
        var mismatches = Run(d, schedule, c, t =>
        {
            if (sides.Count > 0 && t >= sides[0].T)
            {
                d.SetSides(output, book, sides[0].Band);
                sides.RemoveAt(0);
            }
        });
        Assert.True(mismatches.Count == 0, c.GetProperty("name").GetString() + ": " + string.Join("\n", mismatches.Take(20)));
    }

    private static List<string> Run(CharacterDirector d, (double T, int Level)[] schedule, JsonElement c, Action<double>? beforeUpdate)
    {
        var frames = c.GetProperty("frames").EnumerateArray().ToArray();
        var next = 0;
        var mismatches = new List<string>();
        for (var n = 0; n < 60 * 42; n++)
        {
            var t = n / 60.0;
            var level = schedule.Last(s => t >= s.T).Level;
            d.SetLevel(level);
            beforeUpdate?.Invoke(t);
            d.Update(1.0 / 60);
            if (next >= frames.Length || frames[next].GetProperty("n").GetInt32() != n)
            {
                continue;
            }

            var frame = frames[next++];
            var gesture = frame.GetProperty("gesture").ValueKind == JsonValueKind.Null ? null : frame.GetProperty("gesture").GetString();
            if (d.ActiveGesture != gesture)
            {
                mismatches.Add($"n={n} gesture {d.ActiveGesture ?? "-"} != {gesture ?? "-"}");
            }

            foreach (var p in frame.GetProperty("values").EnumerateObject())
            {
                var mine = d.Value(p.Name);
                if (mine is null || Math.Abs(mine.Value - p.Value.GetDouble()) > 1e-4)
                {
                    mismatches.Add($"n={n} {p.Name} {mine} != {p.Value.GetDouble()}");
                }
            }

            if (mismatches.Count > 20)
            {
                break;
            }
        }

        if (next != frames.Length && mismatches.Count <= 20)
        {
            mismatches.Add($"checked {next} of {frames.Length} frames");
        }

        return mismatches;
    }
}
