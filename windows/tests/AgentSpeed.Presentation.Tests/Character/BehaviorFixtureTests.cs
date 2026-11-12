using System.Text.Json;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>
/// The scripted session of spec/fixtures/characters/behavior.json: the inputs at a time, as the fixture's <c>about</c>
/// says (characters/tests/make_fixtures.py, behavior_cases).
/// </summary>
internal sealed class BehaviorSession(JsonElement doc)
{
    private static IEnumerable<JsonElement> Rows(JsonElement doc, string key) =>
        doc.TryGetProperty(key, out var rows) ? rows.EnumerateArray() : [];

    public double Seconds => doc.GetProperty("seconds").GetDouble();

    public int Level(double t)
    {
        var level = 0;
        foreach (var row in Rows(doc, "levels"))
        {
            if (t >= row[0].GetDouble())
            {
                level = row[1].GetInt32();
            }
        }

        return level;
    }

    public BehaviorInputs Inputs(double t, double dt)
    {
        var phase = "idle";
        var idleAt = 0.0;
        var since = 0.0;
        foreach (var row in Rows(doc, "phases"))
        {
            if (t >= row[0].GetDouble())
            {
                phase = row[1].GetString()!;
                idleAt = row[2].GetDouble();
                since = row[0].GetDouble();
            }
        }

        double? wait = null;
        foreach (var row in Rows(doc, "requests"))
        {
            if (row[0].GetDouble() <= t && t < row[1].GetDouble())
            {
                wait = t - row[0].GetDouble();
                break;
            }
        }

        var idle = 0.0;
        if (phase == "idle")
        {
            var jumps = 0.0;
            foreach (var row in Rows(doc, "idleJumps"))
            {
                if (t >= row[0].GetDouble())
                {
                    jumps += row[1].GetDouble();
                }
            }

            idle = idleAt + t - since + jumps;
        }

        var keys = Rows(doc, "cursor").ToArray();
        (double X, double Y)? cursor = null;
        for (var i = 0; i + 1 < keys.Length; i++)
        {
            double t0 = keys[i][0].GetDouble(), t1 = keys[i + 1][0].GetDouble();
            if (t0 <= t && t <= t1)
            {
                var k = (t - t0) / (t1 - t0);
                cursor = (keys[i][1].GetDouble() + (keys[i + 1][1].GetDouble() - keys[i][1].GetDouble()) * k,
                    keys[i][2].GetDouble() + (keys[i + 1][2].GetDouble() - keys[i][2].GetDouble()) * k);
                break;
            }
        }

        var box = CharacterFixture.Doubles(doc.GetProperty("over"));
        var over = cursor is { } c && box[0] <= c.X && c.X <= box[1] && box[2] <= c.Y && c.Y <= box[3];
        bool Fires(double at) => t - dt < at && at <= t;
        var events = Rows(doc, "events").Where(row => Fires(row[0].GetDouble())).Select(row => row[1].GetString()).ToArray();
        var click = over && Rows(doc, "clicks").Any(at => Fires(at.GetDouble()));
        return new BehaviorInputs
        {
            Phase = phase switch
            {
                "generating" => AgentActivity.Generating,
                "tools" => AgentActivity.Tools,
                _ => AgentActivity.Idle,
            },
            Wait = wait,
            Idle = idle,
            Hour = doc.GetProperty("hour").GetInt32(),
            NewPeak = events.Contains("newPeak"),
            Trouble = events.Contains("trouble"),
            Cursor = cursor,
            Over = over,
            Click = click,
        };
    }
}

/// <summary>spec/fixtures/characters/behavior.json: a living director through a scripted session (spec/CHARACTERS.md, Behavior).</summary>
public class BehaviorFixtureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BehaviorMatchesTheReference(int index)
    {
        var doc = CharacterFixture.Json("behavior.json");
        var session = new BehaviorSession(doc.GetProperty("session"));
        var c = doc.GetProperty("cases")[index];
        var variants = c.GetProperty("variants").EnumerateArray().Select(RigDocument.DecodeVariant).ToArray();
        var parameters = doc.GetProperty("params").EnumerateArray().Select(CharacterParam.Decode).ToList();
        foreach (var v in variants)
        {
            parameters.Add(new CharacterParam(v.Param, 0, 1, 0));
            if (v.Kind == "arm")
            {
                parameters.Add(new CharacterParam(v.Arm!, -1, 1, 0));
                parameters.Add(new CharacterParam(v.Forearm!, -1, 1, 0));
            }
        }

        var d = new CharacterDirector(parameters, (ulong)c.GetProperty("seed").GetInt64(), CharacterSides.Parse(c.GetProperty("armOut").GetString())!.Value,
            CharacterSides.Parse(c.GetProperty("armBook").GetString())!.Value, c.GetProperty("orb").GetDouble(), celebrate: false, variants: variants,
            states: c.GetProperty("states").EnumerateArray().Select(s => s.GetString()!).ToArray());
        Assert.True(d.IsLiving);
        var frames = c.GetProperty("frames").EnumerateArray().ToArray();
        const double dt = 1.0 / 60;
        var next = 0;
        var mismatches = new List<string>();
        for (var n = 0; n < (int)(session.Seconds * 60); n++)
        {
            var t = n / 60.0;
            d.SetLevel(session.Level(t));
            d.Update(dt, session.Inputs(t, dt));
            if (next >= frames.Length || frames[next].GetProperty("n").GetInt32() != n)
            {
                continue;
            }

            var frame = frames[next++];
            string? pose = d.PoseWanted() switch
            {
                { IsKeep: true } => "keep",
                { State: var state } => state,
                null => null,
            };
            (string Key, string? Value)[] mine =
            [
                ("mood", d.Mood?.Key()), ("pose", pose), ("face", d.Expression), ("gaze", d.GazePatch), ("mouth", d.MouthShape),
                ("gesture", d.ActiveGesture),
            ];
            foreach (var (key, value) in mine)
            {
                var expected = frame.GetProperty(key).ValueKind == JsonValueKind.Null ? null : frame.GetProperty(key).GetString();
                if (value != expected)
                {
                    mismatches.Add($"n={n} {key} {value ?? "-"} != {expected ?? "-"}");
                }
            }

            foreach (var p in frame.GetProperty("values").EnumerateObject())
            {
                var value = d.Value(p.Name);
                if (value is null || Math.Abs(value.Value - p.Value.GetDouble()) > 1e-4)
                {
                    mismatches.Add($"n={n} {p.Name} {value} != {p.Value.GetDouble()}");
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

        Assert.True(mismatches.Count == 0, c.GetProperty("name").GetString() + ": " + string.Join("\n", mismatches.Take(20)));
    }

    [Fact]
    public void VariantsSkipTheItemsTheyCannotRead()
    {
        using var doc = JsonDocument.Parse("""
            [{"id": "wave", "kind": "arm", "param": "Var.wave", "side": "right", "front": true, "arm": "Arm.right@wave",
              "forearm": "Forearm.right@wave", "lead": {"Lift.right": 0.2}, "approach": {"Arm.right@wave": -0.25}},
             {"id": "odd", "kind": "arm", "param": "Var.odd", "front": "yes"},
             {"kind": "patch", "param": "Var.expr-smile"},
             7,
             {"id": "expr-smile", "kind": "patch", "param": "Var.expr-smile", "region": "face"}]
            """);
        var variants = RigDocument.DecodeVariants(doc.RootElement);
        Assert.Equal(["wave", "expr-smile"], variants.Select(v => v.Id));
        Assert.Equal(-0.25, variants[0].Approach!["Arm.right@wave"]);
        Assert.True(variants[0].Front);
        Assert.Equal("face", variants[1].Region);
        using var notAList = JsonDocument.Parse("""{"id": "wave"}""");
        Assert.Empty(RigDocument.DecodeVariants(notAList.RootElement));
    }
}
