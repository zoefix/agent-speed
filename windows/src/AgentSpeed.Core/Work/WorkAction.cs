namespace AgentSpeed.Core.Work;

/// <summary>
/// One action of an agent (METRICS 6.10): when it was called, which tool, the digest of its input, and from its first
/// result when that came and whether it failed; the model of the response that issued it; whether seeding read it.
/// </summary>
/// <param name="AtMs"><c>callMs</c>: the time of the line that holds the call.</param>
/// <param name="Failed">Null while the outcome is unknown, which is not a failure.</param>
internal readonly record struct WorkAction(long AtMs, string Tool, ulong Digest, bool? Failed = null)
{
    /// <summary>The time of the line that holds the call's first result; null until one is read.</summary>
    public long? ResultMs { get; init; }

    /// <summary>The agent's model right after the call's line was read; null when unknown.</summary>
    public string? Model { get; init; }

    /// <summary>Read while seeding: the performance score uses it, the signals do not.</summary>
    public bool Seeded { get; init; }

    public bool SameCall(in WorkAction other) => Digest == other.Digest && string.Equals(Tool, other.Tool, StringComparison.Ordinal);
}
