namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>
/// The unit an orb shows (DESIGN 2b <c>unit</c>, 9): token speed or the API-equivalent cost per second. Heat, tiers
/// and titles always follow tokens.
/// </summary>
public enum OrbUnit
{
    Tokens,
    Cost,
}

public static class OrbUnits
{
    public static string Key(OrbUnit unit) => unit == OrbUnit.Cost ? "cost" : "tokens";

    /// <summary>Unknown values (a newer version's) read as <see cref="OrbUnit.Tokens"/>.</summary>
    public static OrbUnit Parse(string? key) => key == "cost" ? OrbUnit.Cost : OrbUnit.Tokens;

    public static OrbUnit Toggled(this OrbUnit unit) => unit == OrbUnit.Tokens ? OrbUnit.Cost : OrbUnit.Tokens;
}
