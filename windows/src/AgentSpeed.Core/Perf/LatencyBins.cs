namespace AgentSpeed.Core.Perf;

/// <summary>
/// The 64 first-content latency buckets of the performance history (METRICS 6.11 <i>Latency buckets</i>): integer edges
/// <c>E_j = ⌈100 · 2^(j/4)⌉</c>, so every platform buckets alike.
/// </summary>
public static class LatencyBins
{
    public const int Count = 64;

    /// <summary><c>E_0 … E_64</c>.</summary>
    public static IReadOnlyList<long> Edges { get; } = BuildEdges();

    /// <summary>The bucket of an integer latency: the number of edges <c>E_1 … E_63</c> it reaches.</summary>
    public static int Of(long ms)
    {
        int low = 1, high = Count - 1, reached = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (ms >= Edges[mid])
            {
                reached = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return reached;
    }

    /// <summary>The geometric centre <c>100 · 2^((k + 0.5) / 4)</c> of bucket <paramref name="k"/>, as the reference writes it.</summary>
    public static double Centre(int k) => 100 * Math.Pow(2, (2 * k + 1) / 8.0);

    private static long[] BuildEdges()
    {
        var edges = new long[Count + 1];
        for (var j = 0; j <= Count; j++)
        {
            edges[j] = Edge(j);
        }

        return edges;
    }

    /// <summary>The ceiling of the fourth root of <c>10^8 · 2^j</c>, in integers.</summary>
    private static long Edge(int j)
    {
        var n = System.Numerics.BigInteger.Pow(10, 8) * System.Numerics.BigInteger.Pow(2, j);
        var root = IntegerRoot(IntegerRoot(n));
        return (long)(System.Numerics.BigInteger.Pow(root, 4) == n ? root : root + 1);
    }

    private static System.Numerics.BigInteger IntegerRoot(System.Numerics.BigInteger n)
    {
        if (n < 2)
        {
            return n;
        }

        var x = (System.Numerics.BigInteger)Math.Sqrt((double)n);
        while (x * x > n)
        {
            x--;
        }

        while ((x + 1) * (x + 1) <= n)
        {
            x++;
        }

        return x;
    }
}
