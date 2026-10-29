using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Perf;

namespace AgentSpeed.Probe;

/// <summary>
/// The performance rows of the dumps (METRICS 6.9 <i>Dumps</i>, 6.11) as <c>reference.py</c> writes them: <c>perf</c> per
/// agent and the <c>perfSets</c> row of <c>--dump-today</c>, and <c>perf</c> per history record of <c>--dump-history</c>.
/// Numbers are written in full (round-trip), so <c>compare_dumps.py</c> compares them within 1e-9.
/// </summary>
internal static class PerfDumpJson
{
    public static void WriteAgent(Utf8JsonWriter w, PerfAgentView agent, long nowMs)
    {
        var value = agent.Value;
        w.WriteStartObject();
        w.WriteString("agentId", agent.Id);
        w.WriteString("source", JsonLine.SourceName(agent.Source));
        w.WriteString("phase", PhaseName(PerfSetView.EffectivePhase(agent.Phase, agent.LastLineMs, nowMs)));
        WriteNumber(w, "lastLineMs", agent.LastLineMs);
        w.WriteString("parentId", agent.ParentId);
        w.WriteString("form", FormName(value.Form));
        WriteNumber(w, "score", value.Score);
        WriteNumber(w, "shown", value.Shown);
        w.WriteBoolean("provisional", value.Provisional);
        w.WriteNumber("n", value.N);
        w.WriteNumber("tools", value.Tools);
        w.WriteNumber("activeMs", value.ActiveMs);
        WriteNumber(w, "spanStartMs", value.SpanStartMs);
        w.WriteString("model", value.Model);
        WriteComponents(w, value.Components);
        w.WriteEndObject();
    }

    public static void WriteSets(Utf8JsonWriter w, PerfSetView view)
    {
        w.WriteStartObject();
        w.WriteStartArray("listed");
        foreach (var id in view.Listed)
        {
            w.WriteStringValue(id);
        }

        w.WriteEndArray();
        w.WriteStartArray("sets");
        foreach (var set in view.Sets)
        {
            w.WriteStartObject();
            if (set.Sources is { } sources)
            {
                w.WriteStartArray("sources");
                foreach (var source in sources)
                {
                    w.WriteStringValue(JsonLine.SourceName(source));
                }

                w.WriteEndArray();
            }
            else
            {
                w.WriteNull("sources");
            }

            WriteValue(w, set.Value);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartObject("groups");
        foreach (var group in view.Groups)
        {
            w.WriteStartObject(group.Root);
            WriteValue(w, group.Value);
            w.WriteStartArray("members");
            foreach (var member in group.Members)
            {
                w.WriteStringValue(member);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    public static void WriteRecord(Utf8JsonWriter w, PerfRecord record)
    {
        var s = record.Sums;
        w.WriteStartObject();
        w.WriteString("day", record.Day);
        w.WriteString("source", JsonLine.SourceName(record.Source));
        w.WriteString("model", record.Model);
        w.WriteNumber("n", s.N);
        w.WriteNumber("outputSum", s.OutputSum);
        w.WriteNumber("durationMsSum", s.DurationMsSum);
        w.WriteStartArray("latencyBins");
        foreach (var bin in s.Bins)
        {
            w.WriteNumberValue(bin);
        }

        w.WriteEndArray();
        w.WriteNumber("reasonN", s.ReasonN);
        w.WriteNumber("reasonOutput", s.ReasonOutput);
        w.WriteNumber("reasonDurationMsSum", s.ReasonDurationMsSum);
        w.WriteNumber("cacheN", s.CacheN);
        w.WriteNumber("cacheInput", s.CacheInput);
        w.WriteNumber("cacheCached", s.CacheCached);
        w.WriteNumber("toolN", s.ToolN);
        w.WriteNumber("toolLnSum", s.ToolLnSum);
        w.WriteNumber("toolFailed", s.ToolFailed);
        w.WriteNumber("activeMs", s.ActiveMs);
        w.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter w, PerfSetValue value)
    {
        w.WriteString("form", FormName(value.Form));
        WriteNumber(w, "score", value.Score);
        WriteNumber(w, "shown", value.Shown);
        w.WriteString("agent", value.AgentId);
    }

    private static void WriteComponents(Utf8JsonWriter w, IReadOnlyList<PerfComponent> components)
    {
        w.WriteStartArray("components");
        foreach (var c in components)
        {
            w.WriteStartObject();
            w.WriteString("id", c.Id.ToString());
            w.WriteBoolean("present", c.Present);
            w.WriteString("reason", c.Reason switch
            {
                PerfAbsence.NotRecorded => "notRecorded",
                PerfAbsence.FewSamples => "fewSamples",
                _ => null,
            });
            w.WriteNumber("samples", c.Samples);
            WriteNumber(w, "measure", c.Measure);
            WriteNumber(w, "ratio", c.Ratio);
            WriteNumber(w, "subScore", c.SubScore);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static string FormName(PerfForm form) => form switch
    {
        PerfForm.Live => "live",
        PerfForm.Resting => "resting",
        _ => "none",
    };

    private static string PhaseName(AgentPhase phase) => phase switch
    {
        AgentPhase.Generating => "generating",
        AgentPhase.Tools => "tools",
        _ => "idle",
    };

    private static void WriteNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v))
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }
}
