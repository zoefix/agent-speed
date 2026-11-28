namespace AgentSpeed.Presentation.Formatting;

/// <summary>
/// The words of the taskbar's readout blocks (DESIGN 10, Menu bar and taskbar), in their order on the bar: the network
/// block's first, then the intelligence block's, then the quota block's. The widget's label adds them after its own words;
/// the tooltip's first line adds the network and intelligence words after <c> · </c> (its own quota lines carry the rest).
/// </summary>
public static class BarReadouts
{
    /// <summary><paramref name="text"/> followed by each shown block's words, joined by <paramref name="separator"/>.</summary>
    public static string Append(string text, string separator, BarTrafficBlock? traffic, BarIntelligenceBlock? intelligence,
        BarQuotaBlock? quota = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(separator);
        if (traffic?.Spoken is { } network)
        {
            text = text + separator + network;
        }

        if (intelligence is not null)
        {
            text = text + separator + intelligence.Spoken;
        }

        return quota is null ? text : text + separator + quota.Spoken;
    }
}
