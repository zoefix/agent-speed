namespace AgentSpeed.Linux.UI;

/// <summary>Prevents a late async response from applying after a newer pairing operation.</summary>
internal sealed class PairingRequestVersion
{
    private readonly object _gate = new();
    private long _version;

    public long Current
    {
        get { lock (_gate) return _version; }
    }

    public long Next()
    {
        lock (_gate) return ++_version;
    }

    public bool TryApply(long version, Action apply)
    {
        lock (_gate)
        {
            if (_version != version) return false;
            apply();
            return true;
        }
    }
}
