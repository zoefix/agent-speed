namespace AgentSpeed.Core.Quota.Network;

/// <summary>
/// When a vendor request may run (QUOTAS 9.1, NETWORK 9): at most every 5 minutes, 15 minutes after a failure, never
/// again after 401/403 until the credential changes; a new credential may ask at once.
/// </summary>
public sealed class QuotaCadence
{
    public const long IntervalMs = 5 * 60_000;
    public const long FailureWaitMs = 15 * 60_000;

    public enum Outcome
    {
        Success,
        Failure,
        Denied,
    }

    public long? LastRequestMs { get; private set; }

    public Outcome? LastOutcome { get; private set; }

    public QuotaFingerprint? Credential { get; private set; }

    /// <summary>The credential was refused and has not changed since.</summary>
    public bool IsDenied(QuotaFingerprint fingerprint) => LastOutcome == Outcome.Denied && Credential == fingerprint;

    public bool IsDue(long nowMs, QuotaFingerprint fingerprint)
    {
        if (Credential != fingerprint || LastRequestMs is not { } last)
        {
            return true;
        }

        return LastOutcome switch
        {
            Outcome.Denied => false,
            Outcome.Failure => nowMs - last >= FailureWaitMs,
            _ => nowMs - last >= IntervalMs,
        };
    }

    /// <summary>The wait after the last request, whatever its credential (routes that read their credential only when due).</summary>
    public bool Waits(long nowMs) =>
        LastRequestMs is { } last && nowMs - last < (LastOutcome == Outcome.Success ? IntervalMs : FailureWaitMs);

    public void Record(Outcome outcome, long nowMs, QuotaFingerprint fingerprint)
    {
        LastRequestMs = nowMs;
        LastOutcome = outcome;
        Credential = fingerprint;
    }
}
