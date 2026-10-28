namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>
/// What an orb's traffic readout shows (DESIGN 2b <c>traffic</c>, 10): the traffic of its own sources, the whole computer,
/// or nothing. Add, Duplicate and Split copy it; Merge keeps the target's.
/// </summary>
public enum OrbTraffic
{
    Sources,
    Machine,
    Off,
}

public static class OrbTraffics
{
    public static string Key(OrbTraffic traffic) => traffic switch
    {
        OrbTraffic.Machine => "machine",
        OrbTraffic.Off => "off",
        _ => "sources",
    };

    /// <summary>Absent (orbs saved by earlier versions) reads as <see cref="OrbTraffic.Sources"/>; an unknown value as <see cref="OrbTraffic.Off"/>.</summary>
    public static OrbTraffic Parse(string? key) => key switch
    {
        null or "sources" => OrbTraffic.Sources,
        "machine" => OrbTraffic.Machine,
        _ => OrbTraffic.Off,
    };
}
