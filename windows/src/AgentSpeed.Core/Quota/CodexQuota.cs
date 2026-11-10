using System.Text.Json;

namespace AgentSpeed.Core.Quota;

/// <summary>One of <c>rate_limits.primary</c> / <c>.secondary</c> as written, before the line's time is known.</summary>
internal readonly record struct CodexRateSlot(double? UsedPercent, double? WindowMinutes, double? ResetsAt, double? ResetsInSeconds);

/// <summary>The <c>payload.rate_limits</c> object of a Codex <c>token_count</c> line (QUOTAS 3).</summary>
internal sealed class CodexRateLimits
{
    /// <summary><c>limit_id</c> is <c>"codex"</c>, null or absent; other buckets (<c>premium</c>) are ignored.</summary>
    public bool Counts { get; private init; } = true;

    public CodexRateSlot? Primary { get; private init; }

    public CodexRateSlot? Secondary { get; private init; }

    /// <summary>Reads the value at the reader's current token; null when it is not an object.</summary>
    public static CodexRateLimits? Read(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.TrySkip();
            return null;
        }

        var counts = true;
        CodexRateSlot? primary = null, secondary = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("limit_id"u8))
            {
                reader.Read();
                counts = reader.TokenType == JsonTokenType.Null
                    || (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("codex"u8));
                reader.TrySkip();
            }
            else if (reader.ValueTextEquals("primary"u8))
            {
                reader.Read();
                primary = ReadSlot(ref reader);
            }
            else if (reader.ValueTextEquals("secondary"u8))
            {
                reader.Read();
                secondary = ReadSlot(ref reader);
            }
            else
            {
                reader.Read();
                reader.TrySkip();
            }
        }

        return new CodexRateLimits { Counts = counts, Primary = primary, Secondary = secondary };
    }

    private static CodexRateSlot? ReadSlot(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.TrySkip();
            return null;
        }

        double? used = null, minutes = null, resetsAt = null, resetsIn = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            ref double? target = ref used;
            var known = true;
            if (reader.ValueTextEquals("used_percent"u8))
            {
                target = ref used;
            }
            else if (reader.ValueTextEquals("window_minutes"u8))
            {
                target = ref minutes;
            }
            else if (reader.ValueTextEquals("resets_at"u8))
            {
                target = ref resetsAt;
            }
            else if (reader.ValueTextEquals("resets_in_seconds"u8))
            {
                target = ref resetsIn;
            }
            else
            {
                known = false;
            }

            reader.Read();
            if (known && reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) && double.IsFinite(value))
            {
                target = value;
            }
            else
            {
                reader.TrySkip();
            }
        }

        return new CodexRateSlot(used, minutes, resetsAt, resetsIn);
    }

    /// <summary>
    /// The windows of a line written at <paramref name="timestampMs"/>: each slot with a numeric percent and a length above
    /// 0 minutes, by length (290–310 → 5 h, 10000–10160 → 7 d, else other); of two of the same kind the first stays.
    /// </summary>
    public IReadOnlyList<QuotaWindow> Windows(long timestampMs)
    {
        if (!Counts)
        {
            return [];
        }

        var found = new List<QuotaWindow>(2);
        foreach (var slot in (ReadOnlySpan<CodexRateSlot?>)[Primary, Secondary])
        {
            if (slot is not { UsedPercent: { } used, WindowMinutes: { } length })
            {
                continue;
            }

            var minutes = (long)Math.Clamp(Math.Truncate(length), int.MinValue, int.MaxValue);
            if (minutes <= 0)
            {
                continue;
            }

            var kind = minutes is >= 290 and <= 310 ? QuotaWindowKind.FiveHour
                : minutes is >= 10000 and <= 10160 ? QuotaWindowKind.SevenDay
                : QuotaWindowKind.Other;
            long? resets = slot.Value.ResetsAt is { } at ? Seconds(at) * 1000
                : slot.Value.ResetsInSeconds is { } inSeconds ? timestampMs + (long)Math.Truncate(inSeconds * 1000)
                : null;
            if (found.Any(w => w.Kind == kind && (kind != QuotaWindowKind.Other || w.Minutes == minutes)))
            {
                continue;
            }

            found.Add(QuotaWindow.Of(kind, used, resets, minutes: kind == QuotaWindowKind.Other ? (int)minutes : null));
        }

        return found;
    }

    private static long Seconds(double value) => (long)Math.Clamp(Math.Truncate(value), -1e15, 1e15);
}

/// <summary>
/// The newest Codex snapshot across every Codex location (QUOTAS 3): fed by the Codex parsers of this computer's files,
/// read by the app. A token count with no usable window replaces nothing. Thread-safe.
/// </summary>
public sealed class CodexQuotaCollector
{
    private readonly object _gate = new();
    private QuotaSnapshot? _snapshot;

    public QuotaSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Increases whenever <see cref="Snapshot"/> changes.</summary>
    public int Version { get; private set; }

    internal void Observe(CodexRateLimits limits, long timestampMs) => Observe(limits.Windows(timestampMs), timestampMs);

    internal void Observe(IReadOnlyList<QuotaWindow> windows, long timestampMs)
    {
        if (windows.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_snapshot is { } current && timestampMs <= current.ObservedMs)
            {
                return;
            }

            _snapshot = new QuotaSnapshot(SourceKind.Codex, timestampMs, windows);
            Version++;
        }
    }
}
