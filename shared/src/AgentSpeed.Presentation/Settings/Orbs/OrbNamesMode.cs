namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>The global name plate setting <c>orbNames</c> (DESIGN 2b, Name plate); each orb can override it.</summary>
public enum OrbNamesMode
{
    /// <summary>A plate when two or more orbs are visible, or when the orb tracks some (not all) sources.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>The persisted keys of <see cref="OrbNamesMode"/>.</summary>
public static class OrbNamesModes
{
    public static string Key(OrbNamesMode mode) => mode switch
    {
        OrbNamesMode.Always => "always",
        OrbNamesMode.Never => "never",
        _ => "auto",
    };

    /// <summary><c>"auto"</c>, <c>"always"</c> or <c>"never"</c>; anything else is <see cref="OrbNamesMode.Auto"/>.</summary>
    public static OrbNamesMode Parse(string? key) => key switch
    {
        "always" => OrbNamesMode.Always,
        "never" => OrbNamesMode.Never,
        _ => OrbNamesMode.Auto,
    };
}
