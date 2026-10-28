using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Titles;

/// <summary>
/// The nine preset title themes in picker order (DESIGN 1, Title themes). Every theme names the same 13 levels on the
/// same thresholds plus an idle label; only Vehicles keeps the vehicle art in the dashboard hero.
/// </summary>
public enum TitlePreset
{
    Vehicles,
    Animals,
    Wind,
    Space,
    Cultivation,
    Wuxia,
    Mage,
    GameRank,
    Engineer,
}

public static class TitlePresets
{
    /// <summary>Levels 1–13; level 0 is idle.</summary>
    public const int LevelCount = 13;

    /// <summary>Picker order: the physical themes, then the ranks.</summary>
    public static IReadOnlyList<TitlePreset> All { get; } = Enum.GetValues<TitlePreset>();

    public static string Id(TitlePreset preset) => preset switch
    {
        TitlePreset.Animals => "animals",
        TitlePreset.Wind => "wind",
        TitlePreset.Space => "space",
        TitlePreset.Cultivation => "cultivation",
        TitlePreset.Wuxia => "wuxia",
        TitlePreset.Mage => "mage",
        TitlePreset.GameRank => "gameRank",
        TitlePreset.Engineer => "engineer",
        _ => "vehicles",
    };

    /// <summary>The preset with this exact id; null for anything else (a custom set, a newer version's preset).</summary>
    public static TitlePreset? Parse(string? id)
    {
        foreach (var preset in All)
        {
            if (string.Equals(Id(preset), id, StringComparison.Ordinal))
            {
                return preset;
            }
        }

        return null;
    }

    public static string NameKey(TitlePreset preset) => $"titles.{Id(preset)}.name";

    /// <summary>The idle label's string key (Vehicles keeps <c>tier.idle</c>).</summary>
    public static string IdleKey(TitlePreset preset) =>
        preset == TitlePreset.Vehicles ? SpeedTitles.IdleKey : $"titles.{Id(preset)}.idle";

    /// <summary>The string key of level 0 (idle) or 1–13 (Vehicles keeps its <c>title.*</c> keys).</summary>
    public static string Key(TitlePreset preset, int level)
    {
        if (level <= 0)
        {
            return IdleKey(preset);
        }

        level = Math.Min(level, LevelCount);
        return preset == TitlePreset.Vehicles ? SpeedTitles.Ladder[level - 1].Key : $"titles.{Id(preset)}.{level}";
    }

    /// <summary>The dashboard hero keeps the vehicle for Vehicles (else the title badge, DESIGN 4c).</summary>
    public static bool ShowsVehicleArt(TitlePreset preset) => preset == TitlePreset.Vehicles;
}
