namespace AgentSpeed.Presentation.Boards.Live;

/// <summary>Puts each row's 性能 (its cell and words) on the entries of a layout plan while the column shows.</summary>
public static class BoardReadoutRows
{
    public static IReadOnlyList<BoardEntry> Word(IReadOnlyList<BoardEntry> entries, BoardReadouts readouts, BoardTexts texts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(texts);
        if (!readouts.Intelligence)
        {
            return entries;
        }

        var worded = new BoardEntry[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            worded[i] = entry with { Readout = texts.Row(entry, readouts) };
        }

        return worded;
    }
}
