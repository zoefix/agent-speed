namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>Codex history accounts (QUOTAS 10.1): window instances named by one token count line belong to one account (union-find).</summary>
public sealed class CodexChains
{
    private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

    public string Find(string x)
    {
        ArgumentNullException.ThrowIfNull(x);
        _parent.TryAdd(x, x);
        while (!string.Equals(_parent[x], x, StringComparison.Ordinal))
        {
            _parent[x] = _parent[_parent[x]];
            x = _parent[x];
        }

        return x;
    }

    public string Link(IReadOnlyList<string> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var roots = instances.Select(Find).ToList();
        foreach (var r in roots.Skip(1))
        {
            _parent[Find(r)] = Find(roots[0]);
        }

        return Find(roots[0]);
    }
}
