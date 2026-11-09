namespace AgentSpeed.Presentation.Character;

/// <summary>
/// SplitMix64, the random generator every character player shares (spec/CHARACTERS.md, Random numbers), so the same
/// seed and the same speed history give the same motion on every platform.
/// </summary>
public struct Rng
{
    public Rng(ulong seed)
    {
        State = seed;
    }

    public ulong State { get; private set; }

    public ulong NextUInt64()
    {
        unchecked
        {
            State += 0x9E37_79B9_7F4A_7C15UL;
            var z = State;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in [0, 1): the top 53 bits over 2^53.</summary>
    public double Random() => (NextUInt64() >> 11) / 9_007_199_254_740_992.0;

    public double Uniform(double a, double b) => a + (b - a) * Random();

    public int Index(int n) => Math.Min((int)(Random() * n), n - 1);

    public T Choice<T>(IReadOnlyList<T> items) => items[Index(items.Count)];

    /// <summary>Fisher-Yates from the last item down.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Index(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    public List<T> Sample<T>(IReadOnlyList<T> items, int k)
    {
        var copy = new List<T>(items);
        Shuffle(copy);
        if (copy.Count > k)
        {
            copy.RemoveRange(k, copy.Count - k);
        }

        return copy;
    }
}
