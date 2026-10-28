namespace AgentSpeed.Core.Tests;

public sealed class SourceSetTests
{
    [Fact]
    public void DefaultIsEmptyAndEverythingHoldsEverySourceInOrder()
    {
        Assert.True(default(SourceSet).IsEmpty);
        Assert.Equal(SourceSet.Empty, default);
        Assert.Empty(SourceSet.Empty);

        Assert.Equal(Enum.GetValues<SourceKind>(), SourceSet.Everything);
        Assert.Equal(Enum.GetValues<SourceKind>().Length, SourceSet.Everything.Count);
        Assert.False(SourceSet.Everything.IsEmpty);
    }

    [Fact]
    public void MembersEnumerateInSourceOrderWithoutDuplicates()
    {
        var set = SourceSet.From([SourceKind.Antigravity, SourceKind.Codex, SourceKind.Gemini, SourceKind.Codex]);

        Assert.Equal([SourceKind.Codex, SourceKind.Gemini, SourceKind.Antigravity], set);
        Assert.Equal(3, set.Count);
        Assert.True(set.Contains(SourceKind.Gemini));
        Assert.False(set.Contains(SourceKind.ClaudeCode));
        Assert.False(set.Contains((SourceKind)99));
        Assert.Equal("{Codex, Gemini, Antigravity}", set.ToString());

        var kinds = new List<SourceKind>();
        foreach (var kind in set)
        {
            kinds.Add(kind);
        }

        Assert.Equal(set, SourceSet.From(kinds));
    }

    [Fact]
    public void CollectionExpressionsOfAndFromAgree()
    {
        SourceSet pair = [SourceKind.ClaudeDesktop, SourceKind.ClaudeCode];

        Assert.Equal(SourceSet.Of(SourceKind.ClaudeCode).With(SourceKind.ClaudeDesktop), pair);
        Assert.Equal(SourceSet.From(new HashSet<SourceKind> { SourceKind.ClaudeCode, SourceKind.ClaudeDesktop }), pair);
        Assert.Equal(SourceSet.Create([SourceKind.ClaudeCode, SourceKind.ClaudeDesktop]), pair);
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceSet.Of((SourceKind)99));
        Assert.Throws<ArgumentNullException>(() => SourceSet.From(null!));
    }

    [Fact]
    public void SetOperations()
    {
        SourceSet a = [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Omp];
        SourceSet b = [SourceKind.ClaudeCode, SourceKind.Gemini];

        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini, SourceKind.Omp], a.Union(b));
        Assert.Equal([SourceKind.ClaudeCode], a.Intersect(b));
        Assert.Equal([SourceKind.Codex, SourceKind.Omp], a.Except(b));
        Assert.Equal([SourceKind.Codex, SourceKind.Omp], a.Without(SourceKind.ClaudeCode));
        Assert.Equal(a, a.Without(SourceKind.Gemini));
        Assert.True(a.Intersect(b).IsSubsetOf(a));
        Assert.False(a.IsSubsetOf(b));
        Assert.True(SourceSet.Empty.IsSubsetOf(b));
        Assert.True(a.IsSubsetOf(SourceSet.Everything));
    }

    [Fact]
    public void EqualSetsAreOneDictionaryKey()
    {
        var peaks = new Dictionary<SourceSet, double>
        {
            [[SourceKind.Codex, SourceKind.ClaudeCode]] = 1,
            [SourceSet.Of(SourceKind.Codex)] = 2,
        };

        peaks[SourceSet.From([SourceKind.ClaudeCode, SourceKind.Codex])] = 3;

        Assert.Equal(2, peaks.Count);
        Assert.Equal(3, peaks[[SourceKind.Codex, SourceKind.ClaudeCode]]);
        Assert.Equal(
            SourceSet.From([SourceKind.Gemini, SourceKind.Omp]).GetHashCode(),
            SourceSet.From([SourceKind.Omp, SourceKind.Gemini]).GetHashCode());
        Assert.NotEqual(SourceSet.Of(SourceKind.Codex), SourceSet.Of(SourceKind.ClaudeCode));
        Assert.True(SourceSet.Of(SourceKind.Gemini) == SourceSet.Empty.With(SourceKind.Gemini));
    }
}
