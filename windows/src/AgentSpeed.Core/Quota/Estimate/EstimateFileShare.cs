using System.Buffers;
using System.Text.Json;
using AgentSpeed.Core.History;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// One analysed file's share of the spend ledger (QUOTAS 10.3): its responses within 8 days summed per minute (Codex's
/// whole) and its Codex token count lines with a window (10.4). Kept in the history store beside the file's days; numbers,
/// model ids and plan names only.
/// </summary>
public sealed record EstimateFileShare(IReadOnlyList<EstimateResponse> Responses, IReadOnlyList<CodexTokenLine> Lines)
{
    /// <summary>Bumped when the share's contents change: stored shares of another version are made again while their file is recent.</summary>
    public const int Version = 1;

    public static EstimateFileShare Empty { get; } = new([], []);

    public bool IsEmpty => Responses.Count == 0 && Lines.Count == 0;

    /// <summary>The share of <paramref name="file"/>: responses that ended within 8 days of <paramref name="nowMs"/>, summed per minute, and its lines.</summary>
    internal static EstimateFileShare Of(string file, IEnumerable<EstimateResponse> responses, IEnumerable<CodexTokenLine> lines, long nowMs)
    {
        var keep = nowMs - EstimateLimits.KeepMs;
        var spend = EstimateSpend.PerMinute(responses.Where(r => r.EndMs >= keep).Select(r => r.Source == SourceKind.Codex ? r with { File = file } : r));
        var kept = lines.Where(l => l.Ts >= keep && l.Windows.Count > 0).Select(l => l with { File = file }).ToList();
        return spend.Count == 0 && kept.Count == 0 ? Empty : new EstimateFileShare(spend, kept);
    }

    /// <summary>The share as stored: compact JSON, the file implied by its row.</summary>
    public byte[] Encode()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("r");
            foreach (var r in Responses)
            {
                w.WriteStartArray();
                w.WriteStringValue(SourceNames.Of(r.Source));
                w.WriteStringValue(r.Model);
                Number(w, r.StartMs);
                w.WriteNumberValue(r.EndMs);
                Number(w, r.CostMicroUsd);
                w.WriteNumberValue(r.Tokens);
                w.WriteNumberValue(r.Requests);
                Text(w, r.Provider);
                w.WriteBooleanValue(r.CloudRoute);
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteStartArray("l");
            foreach (var l in Lines)
            {
                w.WriteStartArray();
                w.WriteNumberValue(l.Ts);
                Text(w, l.PlanType);
                w.WriteStartArray();
                foreach (var q in l.Windows)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(EstimateNames.Kind(q.Kind));
                    Text(w, q.Name);
                    Number(w, q.Minutes);
                    w.WriteNumberValue(q.UsedPercent);
                    Number(w, q.ResetsAtMs);
                    w.WriteEndArray();
                }

                w.WriteEndArray();
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A stored share of <paramref name="file"/>; null when it does not parse.</summary>
    public static EstimateFileShare? Decode(ReadOnlySpan<byte> data, string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (data.IsEmpty)
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            var root = document.RootElement;
            var responses = new List<EstimateResponse>();
            foreach (var r in root.GetProperty("r").EnumerateArray())
            {
                var source = SourceNames.Parse(r[0].GetString()) ?? throw new FormatException();
                responses.Add(new EstimateResponse(source, source == SourceKind.Codex ? file : null, Long(r[2]), r[3].GetInt64(), r[1].GetString()!,
                    Long(r[4]), r[5].GetInt64(), r[7].GetString(), r[8].GetBoolean(), null, r[6].GetInt64()));
            }

            var lines = new List<CodexTokenLine>();
            foreach (var l in root.GetProperty("l").EnumerateArray())
            {
                var windows = new List<QuotaWindow>();
                foreach (var q in l[2].EnumerateArray())
                {
                    var kind = EstimateNames.ParseKind(q[0].GetString()) ?? throw new FormatException();
                    windows.Add(new QuotaWindow(kind, q[1].GetString(), q[2].ValueKind == JsonValueKind.Null ? null : q[2].GetInt32(), q[3].GetDouble(),
                        Long(q[4])));
                }

                lines.Add(new CodexTokenLine(file, l[0].GetInt64(), windows, l[1].GetString()));
            }

            return new EstimateFileShare(responses, lines);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static long? Long(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt64();

    private static void Number(Utf8JsonWriter w, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumberValue(v);
        }
        else
        {
            w.WriteNullValue();
        }
    }

    private static void Number(Utf8JsonWriter w, int? value) => Number(w, (long?)value);

    private static void Text(Utf8JsonWriter w, string? value)
    {
        if (value is null)
        {
            w.WriteNullValue();
        }
        else
        {
            w.WriteStringValue(value);
        }
    }
}

/// <summary>
/// The history's share of the ledger after a complete analysis (QUOTAS 10.3): every file's, and the minute before which
/// it holds the spend; the live engine's responses hold what ended later.
/// </summary>
public sealed record EstimateHistory(IReadOnlyDictionary<string, EstimateFileShare> Files, long CutMs)
{
    /// <summary>Two minutes before the analysis started, on a minute boundary.</summary>
    public static long Cut(long analysisStartMs) => SpendLedger.MinuteOf(analysisStartMs - EstimateLimits.LagMs);
}
