using System.Text.Json;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// A scripted session for the frame harness (<c>AGENT_SPEED_ORB_SESSION=&lt;file.json&gt;</c>): the agents' phases, waits,
/// idle time, events, clicks and the cursor of a living character (CHARACTERS 5, <i>Behavior</i>), in the format of the
/// <c>session</c> of spec/fixtures/characters/behavior.json, plus <c>speed</c>: <c>[[t, tok/s], …]</c> keys, linear
/// between them (held after the last), and <c>shots</c>: the times to capture. Times are seconds from the session's
/// start; the cursor is in her heights relative to her face, linear between its keys and absent outside them.
/// </summary>
public sealed class CharacterSessionScript
{
    private readonly List<(double Start, AgentActivity Phase, double IdleAt)> _phases = [];
    private readonly List<(double Start, double First)> _requests = [];
    private readonly List<(double At, double By)> _idleJumps = [];
    private readonly List<(double At, string Name)> _events = [];
    private readonly List<double> _clicks = [];
    private readonly List<(double T, double X, double Y)> _cursor = [];
    private readonly List<(double T, double Value)> _speed = [];
    private readonly List<double> _shots = [];

    private CharacterSessionScript()
    {
    }

    public int Hour { get; private set; } = 12;

    /// <summary>The session's length; null when the script does not say.</summary>
    public double? Seconds { get; private set; }

    /// <summary>The capture times, in order; empty when the script names none.</summary>
    public IReadOnlyList<double> Shots => _shots;

    /// <summary>The script in <paramref name="json"/>, or null when it is not a JSON object.</summary>
    public static CharacterSessionScript? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? From(doc.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The script of the file at <paramref name="path"/>, or null when it is missing or unreadable.</summary>
    public static CharacterSessionScript? Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CharacterSessionScript From(JsonElement root)
    {
        var script = new CharacterSessionScript();
        if (root.TryGetProperty("hour", out var hour) && hour.TryGetInt32(out var h))
        {
            script.Hour = h;
        }

        if (root.TryGetProperty("seconds", out var seconds) && seconds.TryGetDouble(out var s))
        {
            script.Seconds = s;
        }

        foreach (var row in Rows(root, "phases", 2))
        {
            if (Number(row[0]) is { } start && Phase(row[1]) is { } phase)
            {
                script._phases.Add((start, phase, row.Length > 2 ? Number(row[2]) ?? 0 : 0));
            }
        }

        foreach (var row in Rows(root, "requests", 2))
        {
            if (Number(row[0]) is { } a && Number(row[1]) is { } b)
            {
                script._requests.Add((a, b));
            }
        }

        foreach (var row in Rows(root, "idleJumps", 2))
        {
            if (Number(row[0]) is { } a && Number(row[1]) is { } b)
            {
                script._idleJumps.Add((a, b));
            }
        }

        foreach (var row in Rows(root, "events", 2))
        {
            if (Number(row[0]) is { } at && row[1].ValueKind == JsonValueKind.String)
            {
                script._events.Add((at, row[1].GetString()!));
            }
        }

        foreach (var row in Rows(root, "cursor", 3))
        {
            if (Number(row[0]) is { } t && Number(row[1]) is { } x && Number(row[2]) is { } y)
            {
                script._cursor.Add((t, x, y));
            }
        }

        foreach (var row in Rows(root, "speed", 2))
        {
            if (Number(row[0]) is { } t && Number(row[1]) is { } v)
            {
                script._speed.Add((t, v));
            }
        }

        script._clicks.AddRange(Numbers(root, "clicks"));
        script._shots.AddRange(Numbers(root, "shots").Order());
        return script;
    }

    private static IEnumerable<JsonElement[]> Rows(JsonElement root, string key, int min)
    {
        if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= min)
            {
                yield return row.EnumerateArray().ToArray();
            }
        }
    }

    private static IEnumerable<double> Numbers(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (Number(item) is { } v)
            {
                yield return v;
            }
        }
    }

    private static double? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : null;

    private static AgentActivity? Phase(JsonElement e) => e.ValueKind != JsonValueKind.String ? null : e.GetString() switch
    {
        "generating" => AgentActivity.Generating,
        "tools" => AgentActivity.Tools,
        "idle" => AgentActivity.Idle,
        _ => null,
    };

    /// <summary>The phase at <paramref name="t"/>, the idle time it started with, and when it started.</summary>
    public (AgentActivity Phase, double IdleAt, double Since) PhaseAt(double t)
    {
        var output = (AgentActivity.Idle, 0.0, 0.0);
        foreach (var p in _phases)
        {
            if (t >= p.Start)
            {
                output = (p.Phase, p.IdleAt, p.Start);
            }
        }

        return output;
    }

    /// <summary>How long the request has waited for its first output at <paramref name="t"/>; null when none waits.</summary>
    public double? WaitAt(double t)
    {
        foreach (var r in _requests)
        {
            if (r.Start <= t && t < r.First)
            {
                return t - r.Start;
            }
        }

        return null;
    }

    /// <summary>The idle time at <paramref name="t"/> (0 outside an idle phase).</summary>
    public double IdleAt(double t)
    {
        var p = PhaseAt(t);
        if (p.Phase != AgentActivity.Idle)
        {
            return 0;
        }

        var jumps = 0.0;
        foreach (var j in _idleJumps)
        {
            if (t >= j.At)
            {
                jumps += j.By;
            }
        }

        return p.IdleAt + t - p.Since + jumps;
    }

    /// <summary>The scripted cursor at <paramref name="t"/>, or null outside its keys.</summary>
    public (double X, double Y)? CursorAt(double t)
    {
        for (var i = 0; i + 1 < _cursor.Count; i++)
        {
            var (a, b) = (_cursor[i], _cursor[i + 1]);
            if (a.T <= t && t <= b.T)
            {
                var k = (t - a.T) / Math.Max(b.T - a.T, 1e-9);
                return (a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
            }
        }

        return null;
    }

    /// <summary>The scripted speed at <paramref name="t"/> (tok/s); null when the script has none.</summary>
    public double? SpeedAt(double t)
    {
        if (_speed.Count == 0)
        {
            return null;
        }

        if (t <= _speed[0].T)
        {
            return _speed[0].Value;
        }

        for (var i = 0; i + 1 < _speed.Count; i++)
        {
            var (a, b) = (_speed[i], _speed[i + 1]);
            if (t <= b.T)
            {
                return a.Value + (b.Value - a.Value) * (t - a.T) / Math.Max(b.T - a.T, 1e-9);
            }
        }

        return _speed[^1].Value;
    }

    /// <summary>
    /// Feeds the step that ends at <paramref name="t"/> (of <paramref name="dt"/>) to her live inputs, on the shared clock
    /// <paramref name="now"/>; true when a <c>newPeak</c> fires in it (the harness celebrates it on the orb too).
    /// </summary>
    public bool Apply(double t, double dt, double now, OrbCharacterLive live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var p = PhaseAt(t);
        live.Scripted = true;
        live.Phase = p.Phase;
        live.WaitingSince = p.Phase == AgentActivity.Generating && WaitAt(t) is { } wait ? now - wait : null;
        live.IdleSince = now - IdleAt(t);
        live.Hour = Hour;
        live.ScriptedCursor = CursorAt(t);
        var peak = false;
        foreach (var e in _events)
        {
            if (t - dt < e.At && e.At <= t)
            {
                if (e.Name == "newPeak")
                {
                    live.NotePeak();
                    peak = true;
                }
                else if (e.Name == "trouble")
                {
                    live.NoteTrouble();
                }
            }
        }

        foreach (var c in _clicks)
        {
            if (t - dt < c && c <= t)
            {
                live.ScriptedClick();
                break;
            }
        }

        return peak;
    }
}
