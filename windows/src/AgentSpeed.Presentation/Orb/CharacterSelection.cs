namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// Which character a <c>character</c> orb shows (CHARACTERS 7). Locked to an installed pack it shows that one; on Auto
/// it follows the fastest live model vendor that has an installed pack, holding a change for 3 s so two close vendors
/// never flicker, and keeps the last one when no live vendor has a pack.
/// </summary>
public sealed class CharacterSelection(string? shown = null)
{
    public const double Hold = 3;

    /// <summary>An installed pack and the vendors it stands for, in catalog order.</summary>
    public sealed record Pack(string Id, IReadOnlyList<string> Vendors);

    private string? _candidate;
    private double _candidateSince;

    public string? Shown { get; private set; } = shown;

    /// <summary>The character to show now. <paramref name="vendorRates"/> are the summed live output rates per vendor.</summary>
    public string? Update(string? locked, IReadOnlyDictionary<string, double> vendorRates, IReadOnlyList<Pack> installed, double now)
    {
        ArgumentNullException.ThrowIfNull(vendorRates);
        ArgumentNullException.ThrowIfNull(installed);
        if (installed.Count == 0)
        {
            Shown = null;
            _candidate = null;
            return null;
        }

        var ids = installed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (locked is not null && ids.Contains(locked))
        {
            Shown = locked;
            _candidate = null;
            return Shown;
        }

        if (Shown is { } current && !ids.Contains(current))
        {
            Shown = null;
        }

        if (Fastest(vendorRates, installed) is not { } wanted)
        {
            _candidate = null;
            Shown ??= installed[0].Id;
            return Shown;
        }

        if (Shown is null)
        {
            Shown = wanted;
            _candidate = null;
            return Shown;
        }

        if (wanted == Shown)
        {
            _candidate = null;
        }
        else if (_candidate != wanted)
        {
            _candidate = wanted;
            _candidateSince = now;
        }
        else if (now - _candidateSince >= Hold)
        {
            Shown = wanted;
            _candidate = null;
        }

        return Shown;
    }

    /// <summary>The pack of the fastest vendor that has one; ties go to the vendor name in order, so the choice is stable.</summary>
    public static string? Fastest(IReadOnlyDictionary<string, double> vendorRates, IReadOnlyList<Pack> installed)
    {
        ArgumentNullException.ThrowIfNull(vendorRates);
        ArgumentNullException.ThrowIfNull(installed);
        foreach (var (vendor, _) in vendorRates.Where(r => r.Value > 0 && double.IsFinite(r.Value))
                     .OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            if (installed.FirstOrDefault(p => p.Vendors.Contains(vendor)) is { } pack)
            {
                return pack.Id;
            }
        }

        return null;
    }
}
