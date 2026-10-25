using System.Collections.Immutable;
using System.Text.Json;

namespace AgentSpeed.Core.Quota;

public sealed record CodexQuotaWindow(double UsedPercent, int DurationMinutes, DateTimeOffset? ResetsAt)
{
    // Never round a nearly exhausted window up to a more generous percentage.
    public int RemainingPercent => (int)Math.Floor(Math.Clamp(100 - UsedPercent, 0, 100));
}

public sealed record CodexResetCredit(DateTimeOffset? ExpiresAt);

/// <summary>Account quota from Codex services, independent of the locally counted tokens.</summary>
public sealed record CodexQuotaSnapshot(CodexQuotaWindow? Primary, CodexQuotaWindow? Secondary, DateTimeOffset FetchedAt)
{
    public int? AvailableResetCreditCount { get; init; }

    public ImmutableArray<CodexResetCredit> ResetCredits { get; init; } = [];

    public IEnumerable<CodexQuotaWindow> Windows => new[] { Primary, Secondary }.OfType<CodexQuotaWindow>();

    public CodexQuotaWindow LimitingWindow => Windows.OrderBy(w => w.RemainingPercent).First();

    /// <summary>Quota carried by a token_count response in the existing Codex transcript.</summary>
    public static CodexQuotaSnapshot? FromTranscript(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.TryGetProperty("limit_id", out var id) && id.ValueKind != JsonValueKind.Null
                && (id.ValueKind != JsonValueKind.String || id.GetString() != "codex"))
        {
            return null;
        }

        var primary = Window(value, "primary", transcript: true);
        var secondary = Window(value, "secondary", transcript: true);
        return primary is null && secondary is null ? null : new(primary, secondary, DateTimeOffset.MinValue);
    }

    public CodexQuotaSnapshot WithResetCreditsFrom(CodexQuotaSnapshot? previous) => AvailableResetCreditCount is not null || previous is null
        ? this : this with { AvailableResetCreditCount = previous.AvailableResetCreditCount, ResetCredits = previous.ResetCredits };

    public DateTimeOffset? NextResetCreditExpiry(DateTimeOffset now) => ResetCredits
        .Where(c => c.ExpiresAt is { } at && at > now).Select(c => c.ExpiresAt).Min();

    /// <summary>Expire only the cards whose exact expiry was supplied. Unknown details remain unknown.</summary>
    public CodexQuotaSnapshot WithoutExpiredResetCredits(DateTimeOffset now)
    {
        var expired = ResetCredits.Count(c => c.ExpiresAt is { } at && at <= now);
        if (expired == 0) { return this; }
        return this with
        {
            AvailableResetCreditCount = AvailableResetCreditCount is { } count ? Math.Max(0, count - expired) : null,
            ResetCredits = [.. ResetCredits.Where(c => c.ExpiresAt is not { } at || at > now)],
        };
    }

    public bool RecoveredSince(CodexQuotaSnapshot previous)
    {
        static bool Recovered(CodexQuotaWindow? next, CodexQuotaWindow? old) => next is not null && old is not null
            && (next.UsedPercent < old.UsedPercent || next.ResetsAt is { } n && old.ResetsAt is { } o && n > o);
        return Recovered(Primary, previous.Primary) || Recovered(Secondary, previous.Secondary);
    }

    public static CodexQuotaSnapshot Parse(JsonElement result, DateTimeOffset fetchedAt)
    {
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets)
            && buckets.ValueKind == JsonValueKind.Object
            && buckets.TryGetProperty("codex", out var codex)
            && codex.ValueKind == JsonValueKind.Object)
        {
            bucket = codex;
        }
        else if (result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object
            && (!legacy.TryGetProperty("limitId", out var id) || id.ValueKind == JsonValueKind.Null
                || id.ValueKind == JsonValueKind.String && id.GetString() == "codex"))
        {
            bucket = legacy;
        }
        else
        {
            throw new InvalidDataException("Codex account quota is unavailable.");
        }

        var primary = Window(bucket, "primary");
        var secondary = Window(bucket, "secondary");
        if (primary is null && secondary is null)
        {
            throw new InvalidDataException("Codex returned no measured quota window.");
        }

        int? count = null;
        var credits = ImmutableArray.CreateBuilder<CodexResetCredit>();
        if (result.TryGetProperty("rateLimitResetCredits", out var cards) && cards.ValueKind == JsonValueKind.Object)
        {
            if (cards.TryGetProperty("availableCount", out var available) && available.ValueKind == JsonValueKind.Number
                && available.TryGetInt32(out var n) && n >= 0)
            {
                count = n;
            }

            if (cards.TryGetProperty("credits", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in details.EnumerateArray())
                {
                    if (card.ValueKind == JsonValueKind.Object && card.TryGetProperty("status", out var status)
                        && status.ValueKind == JsonValueKind.String && status.GetString() == "available")
                    {
                        credits.Add(new(Timestamp(card, "expiresAt")));
                    }
                }
            }
        }

        return new(primary, secondary, fetchedAt) { AvailableResetCreditCount = count, ResetCredits = credits.ToImmutable() };
    }

    private static CodexQuotaWindow? Window(JsonElement bucket, string key, bool transcript = false)
    {
        if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty(transcript ? "used_percent" : "usedPercent", out var used) || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out var percent) || !double.IsFinite(percent))
        {
            return null;
        }

        var duration = window.TryGetProperty(transcript ? "window_minutes" : "windowDurationMins", out var mins) && mins.ValueKind == JsonValueKind.Number
            && mins.TryGetInt32(out var value) && value > 0 ? value : 0;
        return new(Math.Clamp(percent, 0, 100), duration, Timestamp(window, transcript ? "resets_at" : "resetsAt"));
    }

    private static DateTimeOffset? Timestamp(JsonElement element, string key)
    {
        if (element.TryGetProperty(key, out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var unix))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch (ArgumentOutOfRangeException) { }
        }

        return null;
    }
}
