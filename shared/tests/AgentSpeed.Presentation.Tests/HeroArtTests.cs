using System.Text.Json.Nodes;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The embedded vehicle art and its geometry rules (spec/tools/vehicles.py parity).</summary>
public class HeroArtTests
{
    private const double Epsilon = 1e-6;
    private const int RigSamples = 48;

    private static VehicleArt Art => HeroDriver.Art;

    private static string SpecJson() => File.ReadAllText(RepositoryPaths.VehiclesFile());

    [Fact]
    public void EmbeddedArtLoads()
    {
        var art = VehicleArt.LoadEmbedded();
        Assert.Equal(13, art.Vehicles.Count);
        Assert.NotNull(VehicleArt.Shared);
        Assert.Null(VehicleArt.LoadError);
    }

    [Fact]
    public void EmbeddedResourceIsTheSpecFile()
    {
        using var stream = typeof(VehicleArt).Assembly.GetManifestResourceStream(VehicleArt.ResourceName);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(File.ReadAllBytes(RepositoryPaths.VehiclesFile()), memory.ToArray());

        var fromSpec = VehicleArtParser.Parse(SpecJson());
        var embedded = Art;
        Assert.Equal(embedded.Vehicles.Select(v => v.Key), fromSpec.Vehicles.Select(v => v.Key));
        Assert.Equal(embedded.Vehicles.Select(v => v.Parts.Count), fromSpec.Vehicles.Select(v => v.Parts.Count));
        Assert.Equal(embedded.Vehicles.Select(v => v.Emitters.Count), fromSpec.Vehicles.Select(v => v.Emitters.Count));
        Assert.Equal(
            embedded.Vehicles.SelectMany(v => v.Parts).Select(p => p.Path?.Segments.Count ?? -1),
            fromSpec.Vehicles.SelectMany(v => v.Parts).Select(p => p.Path?.Segments.Count ?? -1));
    }

    [Fact]
    public void StreamAndStringParsersAgree()
    {
        using var stream = File.OpenRead(RepositoryPaths.VehiclesFile());
        var fromStream = VehicleArtParser.Parse(stream);
        var fromString = VehicleArtParser.Parse(SpecJson());
        Assert.Equal(fromString.Vehicles.Select(v => (v.Key, v.Bounds, v.PitchPivot)), fromStream.Vehicles.Select(v => (v.Key, v.Bounds, v.PitchPivot)));
    }

    [Fact]
    public void CanvasAndMaskMatchTheSpec()
    {
        var art = Art;
        Assert.Equal(new CircleD(50, 50, 42), art.SafeCircle);
        Assert.Equal(new PointD(50, 50), art.MaskCenter);
        Assert.Equal(42, art.MaskSolid);
        Assert.Equal(49, art.MaskClear);
        Assert.Equal(2, art.StrokeWidth);
        Assert.Equal(0.72, art.OrbFraction);
        Assert.True(art.MaskClear / 100 * art.OrbFraction <= 0.3585, "The mask must stay clear of the agent dots and the gauge.");
        Assert.Equal(Enum.GetValues<PartRole>().Length, art.Roles.Count);
        Assert.True(art.Role(PartRole.Light).Hot);
        Assert.Equal(RoleFill.Heat, art.Role(PartRole.Flame).Fill);
        Assert.Equal(RoleFill.Hot, art.Role(PartRole.FlameCore).Fill);
        Assert.Equal(RoleFill.None, art.Role(PartRole.Body).Fill);
    }

    [Fact]
    public void VehiclesFollowTheTitleLadder()
    {
        var art = Art;
        Assert.Equal(SpeedTitles.Ladder.Count, art.Vehicles.Count);
        var previousTop = 0.0;
        for (var i = 0; i < art.Vehicles.Count; i++)
        {
            var vehicle = art.Vehicles[i];
            var title = SpeedTitles.Ladder[i].Title;
            Assert.Equal(VehicleArt.KeyFor(title), vehicle.Key);
            Assert.Equal(i, vehicle.Index);
            Assert.Equal(title, vehicle.Title);
            Assert.Equal(i, VehicleArt.IndexFor(title));
            Assert.Equal(i, art.IndexOf(vehicle.Key));
            Assert.True(vehicle.Motion.SpeedLow >= previousTop, $"{vehicle.Key}: an upgrade must never slow the world down");
            Assert.True(vehicle.Motion.SpeedLow < vehicle.Motion.SpeedHigh);
            previousTop = vehicle.Motion.SpeedHigh;
            Assert.False(vehicle.Bounds.IsEmpty, vehicle.Key);
        }

        Assert.Equal(
            ["walk", "bicycle", "ebike", "motorcycle", "car", "sportsCar", "bulletTrain", "raceCar", "maglev", "jetliner", "fighterJet",
                "hypersonic", "rocket"],
            art.Vehicles.Select(v => v.Key));
    }

    [Fact]
    public void IdleFallbackIsTheWalker()
    {
        Assert.Equal(Art.IndexOf("walk"), Art.IdleFallback);
        Assert.Equal(0, Art.IdleFallback);
    }

    [Fact]
    public void KeyForMapsTitlesToVehicleKeys()
    {
        Assert.Null(VehicleArt.KeyFor(SpeedTitle.Idle));
        Assert.Equal("walk", VehicleArt.KeyFor(SpeedTitle.Walk));
        Assert.Equal("ebike", VehicleArt.KeyFor(SpeedTitle.EBike));
        Assert.Equal("sportsCar", VehicleArt.KeyFor(SpeedTitle.SportsCar));
        Assert.Equal("rocket", VehicleArt.KeyFor(SpeedTitle.Rocket));
        Assert.Equal(-1, VehicleArt.IndexFor(SpeedTitle.Idle));
        Assert.Equal(12, VehicleArt.IndexFor(SpeedTitle.Rocket));
    }

    [Fact]
    public void NormalizeKeyAcceptsEveryLadderKey()
    {
        foreach (var step in SpeedTitles.Ladder)
        {
            var key = VehicleArt.KeyFor(step.Title);
            Assert.Equal(key, VehicleArt.NormalizeKey(key));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WALK")]
    [InlineData("SportsCar")]
    [InlineData("plane")]
    [InlineData("title.walk")]
    [InlineData(" walk")]
    [InlineData("walk ")]
    [InlineData("tier.idle")]
    public void NormalizeKeyRejectsUnknownKeys(string? key)
    {
        Assert.Null(VehicleArt.NormalizeKey(key));
        Assert.Equal(-1, Art.IndexOf(key));
    }

    [Fact]
    public void NormalizeKeyRejectsVeryLongValues()
    {
        Assert.Null(VehicleArt.NormalizeKey(new string('a', 33)));
        Assert.Null(VehicleArt.NormalizeKey(string.Concat(Enumerable.Repeat("walk", 1000))));
    }

    [Fact]
    public void GroundsHeadingsAndRigsMatchTheirScenery()
    {
        foreach (var vehicle in Art.Vehicles)
        {
            var grounded = vehicle.Scenery is SceneryKind.Road or SceneryKind.Rail or SceneryKind.Guideway;
            Assert.Equal(grounded, vehicle.GroundY.HasValue);
            if (vehicle.GroundY is { } ground)
            {
                Assert.InRange(ground, 50, 88);
            }

            Assert.Equal(vehicle.Key == "rocket", vehicle.Heading.HasValue);
            Assert.Equal(vehicle.Key == "walk", vehicle.Walk is not null);
            Assert.Equal(vehicle.Key is "bicycle" or "ebike", vehicle.Pedal is not null);
        }

        Assert.Equal(-48, Art.Vehicles[^1].Heading);
    }

    [Fact]
    public void EveryPartStaysInsideTheSafeCircle()
    {
        var art = Art;
        var safe = art.SafeCircle;
        foreach (var vehicle in art.Vehicles)
        {
            foreach (var part in vehicle.Parts)
            {
                var half = art.StrokeWidth * art.Role(part.Role).Weight / 2;
                var points = Points(part);
                if (part.Bleed)
                {
                    Assert.All(points, p =>
                    {
                        Assert.InRange(p.X, -0.01, 100.01);
                        Assert.InRange(p.Y, -0.01, 100.01);
                    });
                    if (part.Role == PartRole.Body)
                    {
                        var front = points.Where(p => p.X > 45).Select(p => Distance(p, safe.Center)).DefaultIfEmpty(-half).Max() + half;
                        Assert.True(front <= safe.Radius + Epsilon, $"{vehicle.Key}.{part.Id}: leading half leaves the safe circle ({front:F2})");
                    }
                }
                else
                {
                    var worst = points.Max(p => Distance(p, safe.Center)) + half;
                    Assert.True(worst <= safe.Radius + Epsilon, $"{vehicle.Key}.{part.Id}: outside the safe circle ({worst:F2})");
                }
            }
        }
    }

    [Fact]
    public void SpinningPartsSweepInsideTheSafeCircle()
    {
        var art = Art;
        var safe = art.SafeCircle;
        var spinning = 0;
        foreach (var vehicle in art.Vehicles)
        {
            foreach (var part in vehicle.Parts)
            {
                if (part.Spin is not { } spin)
                {
                    continue;
                }

                spinning++;
                var half = art.StrokeWidth * art.Role(part.Role).Weight / 2;
                var reach = Points(part).Max(p => Distance(p, spin.Pivot));
                var far = Distance(spin.Pivot, safe.Center) + reach + half;
                Assert.True(far <= safe.Radius + Epsilon, $"{vehicle.Key}.{part.Id}: the spin sweep leaves the safe circle ({far:F2})");
                Assert.True(spin.Radius > 0 && spin.Symmetry > 0);
            }
        }

        Assert.True(spinning >= 10, "Bicycles, motorcycles and cars all have spinning parts.");
    }

    [Fact]
    public void TiresTouchTheRoad()
    {
        var checkedTires = 0;
        foreach (var vehicle in Art.Vehicles.Where(v => v.Scenery == SceneryKind.Road && v.Key != "walk"))
        {
            var ground = vehicle.GroundY ?? throw new InvalidOperationException($"{vehicle.Key} has no road line");
            Assert.True(vehicle.HasWheels, vehicle.Key);
            foreach (var part in vehicle.Parts.Where(p => p.Role == PartRole.Wheel))
            {
                var tire = part.Circle ?? throw new InvalidOperationException($"{vehicle.Key}.{part.Id} is not a circle");
                Assert.True(Math.Abs(tire.Y + tire.Radius - ground) < 0.05, $"{vehicle.Key}.{part.Id}: bottom {tire.Y + tire.Radius:F2} vs road {ground:F2}");
                checkedTires++;
            }
        }

        Assert.True(checkedTires >= 12, "Six road vehicles with two tires each.");
    }

    [Fact]
    public void WalkerStandingPoseStaysInsideTheSafeCircle()
    {
        var walker = Art.Vehicles[0];
        var rig = walker.Walk ?? throw new InvalidOperationException("The walker has no walk rig.");
        var pose = new RigPose();
        pose.Walk(rig, 0, walker.GroundY ?? 79, stand: 1);
        AssertPoseInside(pose, "walk standing");
    }

    [Fact]
    public void WalkingCycleStaysInsideTheSafeCircleAndNeverOverextends()
    {
        var walker = Art.Vehicles[0];
        var rig = walker.Walk ?? throw new InvalidOperationException("The walker has no walk rig.");
        var ground = walker.GroundY ?? 79;
        var pose = new RigPose();
        for (var k = 0; k < RigSamples; k++)
        {
            pose.Walk(rig, rig.Cycle * k / RigSamples, ground, stand: 0);
            AssertPoseInside(pose, $"walk sample {k}");
            foreach (var leg in new[] { pose[0], pose[3] })
            {
                Assert.True(Distance(leg[0], leg[2]) <= rig.Thigh + rig.Shin - 1e-3, $"walk sample {k}: leg overextended");
            }
        }
    }

    [Theory]
    [InlineData("bicycle")]
    [InlineData("ebike")]
    public void PedallingStaysInsideTheSafeCircleAndNeverOverextends(string key)
    {
        var vehicle = Art.Vehicles[Art.IndexOf(key)];
        var rig = vehicle.Pedal ?? throw new InvalidOperationException($"{key} has no pedal rig.");
        var pose = new RigPose();
        for (var k = 0; k < RigSamples; k++)
        {
            pose.Pedal(rig, 2 * Math.PI * rig.CrankRadius * k / RigSamples, lift: 0);
            AssertPoseInside(pose, $"{key} sample {k}");
            foreach (var leg in new[] { pose[0], pose[3] })
            {
                Assert.True(Distance(leg[0], leg[2]) <= rig.Thigh + rig.Shin - 1e-3, $"{key} sample {k}: leg overextended");
            }
        }
    }

    [Fact]
    public void FlamesAreClosedPathsWithANozzle()
    {
        foreach (var part in Art.Vehicles.SelectMany(v => v.Parts).Where(p => p.Role is PartRole.Flame or PartRole.FlameCore))
        {
            var path = part.Path ?? throw new InvalidOperationException($"{part.Id} has no path");
            Assert.Equal(PathVerb.Close, path.Segments[^1].Verb);
            var nozzle = part.Nozzle ?? throw new InvalidOperationException($"{part.Id} has no nozzle");
            Assert.InRange(Distance(nozzle.Direction, default), 0.98, 1.02);
        }
    }

    [Fact]
    public void ParserRejectsAnotherVersion() =>
        AssertRejected(root => root["version"] = 2);

    [Fact]
    public void ParserRejectsAMissingVehicle() =>
        AssertRejected(root => Vehicles(root).RemoveAt(12));

    [Fact]
    public void ParserRejectsVehiclesOutOfLadderOrder() =>
        AssertRejected(root =>
        {
            var vehicles = Vehicles(root);
            var walk = vehicles[0]!.DeepClone();
            var bicycle = vehicles[1]!.DeepClone();
            vehicles[0] = bicycle;
            vehicles[1] = walk;
        });

    [Fact]
    public void ParserRejectsAnUnknownFallback() =>
        AssertRejected(root => root["idle"]!["fallback"] = "plane");

    [Fact]
    public void ParserRejectsAnUnknownRole() =>
        AssertRejected(root => FirstPathPart(root)["role"] = "chrome");

    [Theory]
    [InlineData("M 10 10  L 20 20")]
    [InlineData("M 10 10 A 5 5 0 0 1 20 20")]
    [InlineData("M 10.123 10 L 20 20")]
    [InlineData("L 10 10 L 20 20")]
    [InlineData(" M 10 10 L 20 20")]
    [InlineData("M 10 10 L 20")]
    [InlineData("M 10 10 l 20 20")]
    [InlineData("")]
    public void ParserRejectsMalformedPaths(string d) =>
        AssertRejected(root => FirstPathPart(root)["d"] = d);

    [Fact]
    public void ParserAcceptsTheUnmodifiedSpec()
    {
        var root = JsonNode.Parse(SpecJson())!;
        var art = VehicleArtParser.Parse(root.ToJsonString());
        Assert.Equal(13, art.Vehicles.Count);
    }

    private static void AssertRejected(Action<JsonNode> mutate)
    {
        var root = JsonNode.Parse(SpecJson())!;
        mutate(root);
        var json = root.ToJsonString();
        Assert.Throws<FormatException>(() => VehicleArtParser.Parse(json));
    }

    private static JsonArray Vehicles(JsonNode root) => root["vehicles"]!.AsArray();

    private static JsonNode FirstPathPart(JsonNode root)
    {
        foreach (var vehicle in Vehicles(root))
        {
            foreach (var part in vehicle!["parts"]!.AsArray())
            {
                if (part!["d"] is not null)
                {
                    return part;
                }
            }
        }

        throw new InvalidOperationException("No path part in the spec.");
    }

    private static List<PointD> Points(VehiclePart part)
    {
        if (part.Path is { } path)
        {
            return path.Sample().ToList();
        }

        var c = part.Circle ?? throw new InvalidOperationException($"{part.Id} has neither a path nor a circle");
        return Enumerable.Range(0, 36)
            .Select(a => new PointD(c.X + c.Radius * Math.Cos(a * Math.PI / 18), c.Y + c.Radius * Math.Sin(a * Math.PI / 18)))
            .ToList();
    }

    private static void AssertPoseInside(RigPose pose, string where)
    {
        var art = Art;
        var safe = art.SafeCircle;
        var half = art.StrokeWidth / 2;
        for (var i = 0; i < pose.Count; i++)
        {
            var limb = pose[i];
            var worst = 0.0;
            foreach (var point in limb.Points)
            {
                worst = Math.Max(worst, Distance(point, safe.Center));
            }

            Assert.True(worst + half <= safe.Radius + Epsilon, $"{where}: limb {i} leaves the safe circle ({worst + half:F2})");
        }

        if (pose.HasHead)
        {
            var head = pose.Head;
            var reach = Distance(head.Center, safe.Center) + head.Radius + half;
            Assert.True(reach <= safe.Radius + Epsilon, $"{where}: the head leaves the safe circle ({reach:F2})");
        }
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
