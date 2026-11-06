namespace AgentSpeed.Core.Remote;

/// <summary>
/// Splits the relay's stdout into lines (REMOTE 5.1): at most 1 MiB each. A longer line is dropped and counted, so the
/// connection counts it as invalid. Not thread-safe.
/// </summary>
internal sealed class RemoteLineFramer
{
    public const int MaxLineBytes = 1 << 20;

    private readonly MemoryStream _partial = new();
    private bool _skipping;

    /// <summary>The complete lines of <paramref name="chunk"/>, in order, and how many oversized lines it ended.</summary>
    public (List<byte[]> Lines, int Oversized) Consume(ReadOnlySpan<byte> chunk)
    {
        var lines = new List<byte[]>();
        var oversized = 0;
        while (!chunk.IsEmpty)
        {
            var newline = chunk.IndexOf((byte)'\n');
            var segment = newline < 0 ? chunk : chunk[..newline];
            if (!_skipping)
            {
                if (_partial.Length + segment.Length > MaxLineBytes)
                {
                    _skipping = true;
                    _partial.SetLength(0);
                }
                else
                {
                    _partial.Write(segment);
                }
            }

            if (newline < 0)
            {
                break;
            }

            chunk = chunk[(newline + 1)..];
            if (_skipping)
            {
                _skipping = false;
                oversized++;
            }
            else
            {
                lines.Add(_partial.ToArray());
            }

            _partial.SetLength(0);
        }

        return (lines, oversized);
    }
}
