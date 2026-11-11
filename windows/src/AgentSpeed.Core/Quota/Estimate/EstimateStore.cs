using System.Buffers;
using System.Text.Json;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// <c>&lt;data&gt;/quota/estimate.json</c> (QUOTAS 10.4): written with a temporary file that replaces the old one. A file that
/// does not parse reads as nothing and is replaced by the next write. Account keys are stored, ids never.
/// </summary>
public static class EstimateStore
{
    private const int MaxText = 256;
    private const long MaxFileBytes = 16L * 1024 * 1024;

    public static EstimateStoreData? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string path, EstimateStoreData data)
    {
        ArgumentNullException.ThrowIfNull(path);
        AtomicFile.Write(path, Serialize(data));
    }

    public static EstimateStoreData? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static EstimateStoreData? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || !v.TryGetInt32(out var version))
        {
            return null;
        }

        var observations = new List<QuotaObservation>();
        var instances = new List<StoredInstance>();
        var keys = new List<StoredKey>();
        foreach (var o in Items(root, "observations"))
        {
            if (Observation(o) is { } observation)
            {
                observations.Add(observation);
            }
        }

        foreach (var i in Items(root, "instances"))
        {
            if (Instance(i) is { } instance)
            {
                instances.Add(instance);
            }
        }

        foreach (var k in Items(root, "keys"))
        {
            if (Key(k) is { } key)
            {
                keys.Add(key);
            }
        }

        return new EstimateStoreData(version, observations, instances, keys);
    }

    public static byte[] Serialize(EstimateStoreData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", data.Version);
            w.WriteStartArray("observations");
            foreach (var o in data.Observations)
            {
                w.WriteStartObject();
                w.WriteString("account", o.Account);
                w.WriteString("source", EstimateNames.Source(o.Source));
                w.WriteString("kind", EstimateNames.Kind(o.Kind));
                StringOrNull(w, "name", o.Name);
                w.WriteNumber("minutes", o.Minutes);
                w.WriteNumber("t", o.T);
                w.WriteNumber("tLo", o.TLo);
                w.WriteNumber("p", o.P);
                w.WriteNumber("R", o.R);
                StringOrNull(w, "plan", o.Plan);
                StringOrNull(w, "tier", o.Tier);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("instances");
            foreach (var s in data.Instances)
            {
                w.WriteStartObject();
                w.WriteStartArray("class");
                foreach (var part in s.Class.Parts)
                {
                    if (part is null)
                    {
                        w.WriteNullValue();
                    }
                    else
                    {
                        w.WriteStringValue(part);
                    }
                }

                w.WriteEndArray();
                w.WriteString("account", s.Account);
                w.WriteNumber("R", s.R);
                w.WriteNumber("lo", s.Lo);
                if (s.Hi is { } hi)
                {
                    w.WriteNumber("hi", hi);
                }
                else
                {
                    w.WriteNull("hi");
                }

                w.WriteNumber("pMax", s.PMax);
                w.WriteNumber("tLast", s.TLast);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("keys");
            foreach (var k in data.Keys)
            {
                w.WriteStartObject();
                w.WriteString("source", EstimateNames.Source(k.Source));
                w.WriteNumber("at", k.At);
                w.WriteString("key", k.Key);
                StringOrNull(w, "plan", k.Plan);
                StringOrNull(w, "tier", k.Tier);
                if (k.OrgKey is { } org)
                {
                    w.WriteString("orgKey", org);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void StringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static QuotaObservation? Observation(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object
            || Text(o, "account") is not { } account
            || EstimateNames.ParseSource(Text(o, "source")) is not { } source
            || EstimateNames.ParseKind(Text(o, "kind")) is not { } kind
            || Integer(o, "minutes") is not ({ } minutes and > 0 and <= 527_040)
            || Integer(o, "t") is not { } t || Integer(o, "tLo") is not { } tLo || Integer(o, "R") is not { } r
            || Number(o, "p") is not { } p)
        {
            return null;
        }

        return new QuotaObservation(account, source, kind, Text(o, "name"), (int)minutes, p, t, tLo, r,
            AccountKeys.CleanPlan(Text(o, "plan")), AccountKeys.CleanPlan(Text(o, "tier")));
    }

    private static StoredInstance? Instance(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("class", out var c) || c.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string?>();
        foreach (var part in c.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Null)
            {
                parts.Add(null);
            }
            else if (part.ValueKind == JsonValueKind.String && part.GetString() is { Length: <= MaxText } text)
            {
                parts.Add(text);
            }
            else
            {
                return null;
            }
        }

        if (CapClass.FromParts(parts) is not { } cls || Text(s, "account") is not { } account || Integer(s, "R") is not { } r
            || Number(s, "lo") is not { } lo || Number(s, "pMax") is not { } pMax || Integer(s, "tLast") is not { } tLast)
        {
            return null;
        }

        return new StoredInstance(cls, account, r, lo, Number(s, "hi"), pMax, tLast);
    }

    private static StoredKey? Key(JsonElement k)
    {
        if (k.ValueKind != JsonValueKind.Object
            || EstimateNames.ParseSource(Text(k, "source")) is not { } source
            || Integer(k, "at") is not { } at
            || Text(k, "key") is not { } key)
        {
            return null;
        }

        return new StoredKey(source, at, key, AccountKeys.CleanPlan(Text(k, "plan")), AccountKeys.CleanPlan(Text(k, "tier")), Text(k, "orgKey"));
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= MaxText } s ? s : null;

    private static long? Integer(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static double? Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
}
