namespace AgentSpeed.Core.Locations;

/// <summary>
/// Which database of a source reads a session (METRICS 2.1 Databases): the first that has it. A session id another database
/// of the same source already reads is skipped, so a copied or backed-up database adds no agents, sessions or tokens. Not
/// thread-safe: shared by one source's databases on the monitor thread.
/// </summary>
internal sealed class SessionClaims
{
    private readonly Dictionary<string, int> _owners = new(StringComparer.Ordinal);

    /// <summary>True when <paramref name="owner"/> may read the session: no other database reads it.</summary>
    public bool Claim(string session, int owner)
    {
        if (_owners.TryGetValue(session, out var current))
        {
            return current == owner;
        }

        _owners[session] = owner;
        return true;
    }

    public void Release(string session, int owner)
    {
        if (_owners.TryGetValue(session, out var current) && current == owner)
        {
            _owners.Remove(session);
        }
    }

    public void ReleaseAll(int owner)
    {
        foreach (var session in _owners.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToList())
        {
            _owners.Remove(session);
        }
    }
}
