namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>One observed window of a source for the fingerprint: when it was seen, which window, its reset and length.</summary>
public readonly record struct WindowPoint(long T, string Window, long R, int Minutes);

/// <summary>
/// Whether two sources spend on one account (QUOTAS 10.1): the fingerprint of their windows, the Desktop merge rule and
/// equal remote keys. Every merge decision goes through <see cref="Shares"/>.
/// </summary>
public static class AccountMerge
{
    /// <summary>
    /// Comparable pairs are observations of one window whose instances overlap by more than a minute; resets within a minute
    /// confirm (true), further apart contradict (false). A pair is observed at the later of its times. The latest pair
    /// observed by <paramref name="at"/> decides (the earlier time next, a contradiction first on a tie); with none by then
    /// the first pair after it; null without any pair.
    /// </summary>
    public static bool? Fingerprint(IReadOnlyList<WindowPoint> a, IReadOnlyList<WindowPoint> b, long at) =>
        WindowPointSet.Fingerprint(new WindowPointSet(a), new WindowPointSet(b), at);

    /// <summary>
    /// Desktop spends on the CLI's account only when its organization key equals the CLI's, the CLI's plan is personal and
    /// the fingerprint confirms it: one organization has been seen on two accounts.
    /// </summary>
    public static bool DesktopSharesCli(string? desktopOrgKey, string? cliOrgKey, string? cliPlan, bool? fingerprint) =>
        Shares(MergeKind.DesktopWithCli, fingerprint, desktopOrgKey, cliOrgKey, cliPlan);

    /// <summary>Equal keys of two computers are one account unless the latest comparable pair contradicts it.</summary>
    public static bool KeysShare(bool? fingerprint) => Shares(MergeKind.EqualKeys, fingerprint, null, null, null);

    /// <summary>The one account-merge decision of QUOTAS 10.1.</summary>
    public static bool Shares(MergeKind kind, bool? fingerprint, string? desktopOrgKey, string? cliOrgKey, string? cliPlan) => kind switch
    {
        MergeKind.DesktopWithCli => !string.IsNullOrEmpty(desktopOrgKey)
            && string.Equals(desktopOrgKey, cliOrgKey, StringComparison.Ordinal)
            && AccountKeys.IsPersonalClaudePlan(cliPlan)
            && fingerprint == true,
        _ => fingerprint != false,
    };
}

/// <summary>The two merges of QUOTAS 10.1.</summary>
public enum MergeKind
{
    /// <summary>Claude Desktop with the Claude CLI of this computer.</summary>
    DesktopWithCli,

    /// <summary>A host's source and another source with an equal account key.</summary>
    EqualKeys,
}
