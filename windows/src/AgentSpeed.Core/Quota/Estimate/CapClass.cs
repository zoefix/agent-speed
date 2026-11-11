using System.Text;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// A cap class (QUOTAS 10.2): every account of one <c>(vendor/plan, tier, window)</c>, or one account's window when the
/// plan is unknown.
/// </summary>
public readonly record struct CapClass(string? Plan, string? Tier, string? Account, string Window)
{
    public bool IsPlan => Plan is not null;

    /// <param name="vendorPlan"><c>&lt;vendor&gt;/&lt;plan&gt;</c>, or null without a plan.</param>
    public static CapClass Of(string account, string? vendorPlan, string? tier, string window) =>
        vendorPlan is not null ? new CapClass(vendorPlan, tier, null, window) : new CapClass(null, null, account, window);

    /// <summary>The parts as stored: <c>["plan", vendorPlan, tier, window]</c> or <c>["account", account, window]</c>.</summary>
    public IReadOnlyList<string?> Parts => IsPlan ? ["plan", Plan, Tier, Window] : ["account", Account, Window];

    public static CapClass? FromParts(IReadOnlyList<string?> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts switch
        {
            ["plan", { } plan, var tier, { } window] => new CapClass(plan, tier, null, window),
            ["account", { } account, { } window] => new CapClass(null, null, account, window),
            _ => null,
        };
    }

    /// <summary>The parts joined with <c>|</c>, null as <c>None</c> (the fixtures' estimate keys).</summary>
    public string Key => string.Join('|', Parts.Select(p => p ?? "None"));

    public int? Minutes => EstimateNames.MinutesOf(Window);

    /// <summary>The reference's sort key: Python's text of the tuple.</summary>
    internal string SortText
    {
        get
        {
            var text = new StringBuilder("(");
            var first = true;
            foreach (var part in Parts)
            {
                text.Append(first ? "" : ", ").Append(part is null ? "None" : "'" + part + "'");
                first = false;
            }

            return text.Append(')').ToString();
        }
    }
}
