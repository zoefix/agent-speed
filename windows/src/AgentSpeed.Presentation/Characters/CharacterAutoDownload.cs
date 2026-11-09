namespace AgentSpeed.Presentation.Characters;

/// <summary>The automatic download of every character (CHARACTERS 2, <i>Automatic download</i>): which packs a run fetches and in what order.</summary>
public static class CharacterAutoDownload
{
    /// <summary>Settings key of the packs the user set aside (removed, or cancelled while downloading).</summary>
    public const string SetAsideKey = "charactersSetAside";

    /// <summary>
    /// The catalog's packs that are neither installed nor set aside nor <paramref name="skipped"/>: first those of
    /// <paramref name="vendors"/> (the vendors the orb shows live, fastest first), then the rest in catalog order.
    /// </summary>
    public static IReadOnlyList<string> Queue(CharacterCatalog catalog, IReadOnlySet<string> installed, IReadOnlySet<string> setAside,
        IReadOnlySet<string>? skipped, IReadOnlyList<string> vendors)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var wanted = catalog.Characters.Where(e => !installed.Contains(e.Id) && !setAside.Contains(e.Id) && skipped?.Contains(e.Id) != true).ToArray();
        var output = new List<string>();
        foreach (var vendor in vendors)
        {
            foreach (var entry in wanted)
            {
                if (entry.Vendors.Contains(vendor) && !output.Contains(entry.Id))
                {
                    output.Add(entry.Id);
                }
            }
        }

        foreach (var entry in wanted)
        {
            if (!output.Contains(entry.Id))
            {
                output.Add(entry.Id);
            }
        }

        return output;
    }

    /// <summary>Vendors by live output, fastest first (the fastest model's vendor leads on a tie).</summary>
    public static IReadOnlyList<string> Vendors(VendorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Rates.Where(r => r.Value > 0)
            .OrderByDescending(r => r.Value)
            .ThenBy(r => r.Key == reading.FastestVendor ? 0 : 1)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => r.Key)
            .ToArray();
    }
}
