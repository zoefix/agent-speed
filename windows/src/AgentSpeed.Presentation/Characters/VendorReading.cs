using AgentSpeed.Core;
using AgentSpeed.Core.Pricing;

namespace AgentSpeed.Presentation.Characters;

/// <summary>Live output per model vendor and the fastest model of a set of agents (CHARACTERS 7).</summary>
/// <param name="Rates">Summed live output tok/s per vendor (<c>providers</c> keys of the price table).</param>
/// <param name="FastestModel">The fastest generating agent's model: its price-table name, else the raw id.</param>
public sealed record VendorReading(IReadOnlyDictionary<string, double> Rates, string? FastestModel = null, string? FastestVendor = null)
{
    public static VendorReading Empty { get; } = new(new Dictionary<string, double>());

    public static VendorReading Of(IEnumerable<AgentStatus> agents, PriceTable? prices)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var rates = new Dictionary<string, double>(StringComparer.Ordinal);
        (double Rate, string? Name, string? Vendor)? best = null;
        foreach (var agent in agents)
        {
            var rate = agent.Live.Output;
            if (!(rate > 0) || !double.IsFinite(rate))
            {
                continue;
            }

            var (vendor, name) = CharacterVendors.Resolve(agent.Model, agent.Source, prices);
            if (vendor is not null)
            {
                rates[vendor] = rates.GetValueOrDefault(vendor) + rate;
            }

            if (best is null || rate > best.Value.Rate)
            {
                best = (rate, name, vendor);
            }
        }

        return new VendorReading(rates, best?.Name, best?.Vendor);
    }

    public bool Equals(VendorReading? other) =>
        other is not null && FastestModel == other.FastestModel && FastestVendor == other.FastestVendor && Rates.Count == other.Rates.Count
        && Rates.All(r => other.Rates.TryGetValue(r.Key, out var v) && v == r.Value);

    public override int GetHashCode() => HashCode.Combine(FastestModel, FastestVendor, Rates.Count);
}

/// <summary>The model vendor a source stands for when its model id does not resolve to a priced vendor (CHARACTERS 7).</summary>
public static class CharacterVendors
{
    /// <summary>Resellers whose models belong to another vendor's character; the source decides instead.</summary>
    private static readonly HashSet<string> Resellers = new(StringComparer.Ordinal) { "opencode" };

    /// <summary>Model families the price table does not name a vendor for, by the start of the id's last path segment.</summary>
    private static readonly (string Prefix, string Vendor)[] Families =
    [
        ("doubao", "bytedance"), ("seed-", "bytedance"), ("ui-tars", "bytedance"), ("llama", "meta"),
        ("sonar", "perplexity"), ("pplx", "perplexity"), ("r1-1776", "perplexity"), ("ernie", "baidu"),
        ("hunyuan", "tencent"), ("rwkv", "rwkv"),
    ];

    /// <summary>
    /// The vendor and display name of a model: the price table's provider and name when the id resolves, else the vendor
    /// of its model family, else the source's vendor, with the raw id.
    /// </summary>
    public static (string? Vendor, string? Name) Resolve(string? model, SourceKind source, PriceTable? prices)
    {
        if (prices is not null && prices.Resolver.Resolve(model, null) is { Kind: ResolutionKind.Priced } resolution
            && resolution.Index >= 0 && resolution.Index < prices.Models.Count)
        {
            var entry = prices.Models[resolution.Index];
            var vendor = Resellers.Contains(entry.Provider) ? Family(model) ?? Fallback(source) : entry.Provider;
            return (vendor, entry.Name);
        }

        var raw = model?.Trim();
        return (Family(raw) ?? Fallback(source), string.IsNullOrEmpty(raw) ? null : raw);
    }

    /// <summary><c>meta-llama/Llama-3.3-70B</c> → <c>meta</c>, <c>doubao-seed-1.6</c> → <c>bytedance</c>; null for any other id.</summary>
    public static string? Family(string? model)
    {
        if (model is null || model.Split('/', StringSplitOptions.RemoveEmptyEntries) is not { Length: > 0 } parts)
        {
            return null;
        }

        var id = parts[^1].Trim().ToLowerInvariant();
        foreach (var (prefix, vendor) in Families)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return vendor;
            }
        }

        return null;
    }

    public static string? Fallback(SourceKind source) => source switch
    {
        SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => "anthropic",
        SourceKind.Codex => "openai",
        SourceKind.Gemini or SourceKind.Antigravity => "google",
        SourceKind.DeepSeek => "deepseek",
        SourceKind.Kimi => "moonshot",
        SourceKind.GrokBuild => "xai",
        SourceKind.ZCode => "zhipu",
        SourceKind.WorkBuddy or SourceKind.WorkBuddyAi => "tencent",
        _ => null,
    };
}
