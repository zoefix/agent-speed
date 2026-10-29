namespace AgentSpeed.Core.Work;

/// <summary>
/// What an agent's recent tool calls say (METRICS 6.10): going in circles with failures (<see cref="Looping"/>) or without
/// (<see cref="Repeating"/>), how much of it is new, and how many failures were fixed. Never a score.
/// </summary>
/// <param name="Looping">Repeats of the newest repeated block when the run has a failure; null otherwise.</param>
/// <param name="Repeating">The same without a failure (polling, waiting); null while looping.</param>
/// <param name="Novelty">Distinct actions ÷ recent actions, from <see cref="NoveltyMinimum"/> recent actions on.</param>
/// <param name="Failed">Failed recent actions.</param>
/// <param name="Recovered">Failed recent actions that the same tool soon got right.</param>
public sealed record WorkSignals(int? Looping, int? Repeating, double? Novelty, int Failed, int Recovered)
{
    public const long RecentMs = 600_000;
    public const long FreshMs = 120_000;
    public const int LoopRepeats = 3;
    public const int LargestBlock = 3;
    public const int NoveltyMinimum = 8;
    public const int RecoveryReach = 5;

    public static WorkSignals None { get; } = new(null, null, null, 0, 0);

    public bool IsLooping => Looping is not null;

    /// <summary>The signals of <paramref name="actions"/> (file order) at <paramref name="nowMs"/>; seeded actions are ignored.</summary>
    internal static WorkSignals Of(IReadOnlyList<WorkAction> actions, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return None;
        }

        var recent = actions.Where(a => !a.Seeded && a.AtMs >= nowMs - RecentMs).ToArray();
        if (recent.Length == 0)
        {
            return None;
        }

        int looping = 0, repeating = 0;
        if (nowMs - recent[^1].AtMs < FreshMs)
        {
            for (var size = 1; size <= LargestBlock; size++)
            {
                var (count, run) = RepeatRun(recent, size);
                if (count < LoopRepeats)
                {
                    continue;
                }

                if (run.Any(a => a.Failed == true))
                {
                    looping = Math.Max(looping, count);
                }
                else
                {
                    repeating = Math.Max(repeating, count);
                }
            }
        }

        var failed = 0;
        var recovered = 0;
        for (var i = 0; i < recent.Length; i++)
        {
            if (recent[i].Failed != true)
            {
                continue;
            }

            failed++;
            if (IsRecovered(recent, i))
            {
                recovered++;
            }
        }

        double? novelty = recent.Length >= NoveltyMinimum
            ? (double)recent.Select(a => (a.Tool, a.Digest)).Distinct().Count() / recent.Length
            : null;
        return new WorkSignals(
            looping > 0 ? looping : null,
            looping == 0 && repeating > 0 ? repeating : null,
            novelty,
            failed,
            recovered);
    }

    /// <summary>How many times the newest block of <paramref name="size"/> actions repeats back to back, and the run it covers.</summary>
    private static (int Count, ArraySegment<WorkAction> Run) RepeatRun(WorkAction[] actions, int size)
    {
        if (actions.Length < size)
        {
            return (0, ArraySegment<WorkAction>.Empty);
        }

        var block = actions.AsSpan(actions.Length - size);
        var count = 1;
        var end = actions.Length - size;
        while (end - size >= 0 && SameCalls(actions.AsSpan(end - size, size), block))
        {
            count++;
            end -= size;
        }

        return (count, new ArraySegment<WorkAction>(actions, end, actions.Length - end));
    }

    private static bool SameCalls(ReadOnlySpan<WorkAction> a, ReadOnlySpan<WorkAction> b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].SameCall(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Within the next <see cref="RecoveryReach"/> actions and ten minutes the same tool did not fail.</summary>
    private static bool IsRecovered(WorkAction[] recent, int failedIndex)
    {
        var failed = recent[failedIndex];
        var last = Math.Min(recent.Length, failedIndex + 1 + RecoveryReach);
        for (var j = failedIndex + 1; j < last; j++)
        {
            var next = recent[j];
            if (next.Failed == false && next.AtMs - failed.AtMs <= RecentMs
                && string.Equals(next.Tool, failed.Tool, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
