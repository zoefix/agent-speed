namespace AgentSpeed.Presentation.Settings;

/// <summary>The taskbar's traffic block (DESIGN 10, Menu bar and taskbar).</summary>
public enum BarTraffic
{
    Off,
    Machine,
    Agents,
}

/// <summary>The unit of traffic rates (DESIGN 10): bytes (default) or bits; totals are always bytes.</summary>
public enum TrafficUnit
{
    Bytes,
    Bits,
}

/// <summary>Network traffic measuring and display (DESIGN 10, settings value <c>traffic</c>). Immutable; use <c>with</c> to change.</summary>
public sealed record TrafficSettings
{
    public static TrafficSettings Default { get; } = new();

    /// <summary><c>settings.traffic.enabled</c>: off stops sampling and the helper connection at once.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Displayed source and agent traffic includes the tools the agents run (METRICS 10.5); stored data is unchanged.</summary>
    public bool IncludeTools { get; init; } = true;

    public TrafficUnit Unit { get; init; } = TrafficUnit.Bytes;

    public BarTraffic Bar { get; init; } = BarTraffic.Off;

    public bool Bits => Unit == TrafficUnit.Bits;

    public TrafficSettings Validated() => this with
    {
        Unit = Enum.IsDefined(Unit) ? Unit : TrafficUnit.Bytes,
        Bar = Enum.IsDefined(Bar) ? Bar : BarTraffic.Off,
    };

    public static string Key(BarTraffic bar) => bar switch
    {
        BarTraffic.Machine => "machine",
        BarTraffic.Agents => "agents",
        _ => "off",
    };

    public static BarTraffic ParseBar(string? key) => key switch
    {
        "machine" => BarTraffic.Machine,
        "agents" => BarTraffic.Agents,
        _ => BarTraffic.Off,
    };
}
