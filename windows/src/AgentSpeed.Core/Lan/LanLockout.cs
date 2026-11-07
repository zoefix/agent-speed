namespace AgentSpeed.Core.Lan;

/// <summary>
/// The access key lockout (LAN 4.4) on a monotonic millisecond clock: the 10th missing or wrong key of an address within 60 s
/// locks it out for 60 s from that failure; a valid key clears its failures. At most 256 addresses are remembered, the least
/// recently failed forgotten first. Not thread-safe; the server serializes calls.
/// </summary>
public sealed class LanLockout
{
    public const int Failures = 10;
    public const long WindowMs = 60_000;
    public const long LockMs = 60_000;
    public const int MaxAddresses = 256;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();

    /// <summary>The whole seconds left (rounded up) while <paramref name="address"/> is locked out, else null.</summary>
    public int? Locked(string address, long nowMs)
    {
        if (_entries.TryGetValue(address, out var entry) && entry.Until is { } until && nowMs < until)
        {
            return (int)((until - nowMs + 999) / 1000);
        }

        return null;
    }

    public void Fail(string address, long nowMs)
    {
        if (_entries.TryGetValue(address, out var entry))
        {
            _order.Remove(entry.Node);
        }
        else
        {
            entry = new Entry(new LinkedListNode<string>(address));
            _entries[address] = entry;
        }

        entry.Times.RemoveAll(t => nowMs - t >= WindowMs);
        entry.Times.Add(nowMs);
        if (entry.Times.Count >= Failures)
        {
            entry.Until = nowMs + LockMs;
            entry.Times.Clear();
        }

        _order.AddLast(entry.Node);
        while (_entries.Count > MaxAddresses && _order.First is { } oldest)
        {
            _order.RemoveFirst();
            _entries.Remove(oldest.Value);
        }
    }

    public void Succeed(string address)
    {
        if (_entries.Remove(address, out var entry))
        {
            _order.Remove(entry.Node);
        }
    }

    private sealed class Entry(LinkedListNode<string> node)
    {
        public LinkedListNode<string> Node { get; } = node;

        public List<long> Times { get; } = [];

        public long? Until { get; set; }
    }
}
