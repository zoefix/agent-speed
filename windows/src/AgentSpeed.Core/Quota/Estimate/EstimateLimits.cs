namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>The constants of QUOTAS 10 and REPORTING-EXTENSIONS 11.</summary>
public static class EstimateLimits
{
    public const long Minute = 60_000;
    public const long Hour = 60 * Minute;
    public const long Day = 24 * Hour;

    /// <summary>λ: the vendor's lag between spend and its percent.</summary>
    public const long LagMs = 2 * Minute;

    /// <summary>Spend, observations and key changes are kept for 8 days.</summary>
    public const long KeepMs = 8 * Day;

    public const double HiPenalty = 0.25;
    public const double FullStep = 20.0;
    public const double MinWeight = 0.05;
    public const int SegmentsPerInstance = 64;
    public const int InstancesPerClass = 64;
    public const double IncrementStep = 10.0;
    public const long InstanceJoinMs = 2 * Minute;
    public const long FingerprintMs = Minute;
    public const long HourRoundedSlackMs = Hour;
    public const long SendEveryMs = 60_000;
    public const double PrecisionFloor = 0.02;
    public const double HighWidth = 0.08;

    /// <summary>A Codex token count line reports the response that ended at most this long before it.</summary>
    public const long LineMatchMs = 5_000;

    public const int ObservationsPerInstance = 256;
    public const int MaxEntries = 16;
    public const int StoreVersion = 1;
}
