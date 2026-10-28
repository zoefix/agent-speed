using System.Collections.Immutable;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>
/// First launch of the multi-orb version (DESIGN 2b, Migration): one all-sources <c>led</c> orb with the old size,
/// opacity and toggles. Every old per-display anchor becomes a single-display arrangement holding that orb's
/// fraction/fraction record, the last used display's the most recent, so the orb sits exactly where the old one did.
/// The old keys stay in the file untouched, so a downgrade restores the single orb.
/// </summary>
public static class OrbsMigration
{
    public static OrbsDocument FromLegacy(AppSettings legacy, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var orb = new OrbDefinition(OrbDefinition.CleanId(id) ?? OrbDefinition.NewId())
        {
            Sources = OrbSources.All,
            Style = OrbStyle.Led,
            Size = legacy.OrbSize,
            Opacity = legacy.OrbOpacity,
            AlwaysOnTop = legacy.OrbAlwaysOnTop,
            ClickThrough = legacy.OrbClickThrough,
            FadeWhenIdle = legacy.OrbFadeWhenIdle,
            Visible = true,
        }.Validated();

        var keys = legacy.OrbAnchors.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k) && k.Length <= OrbPositionRecord.MaxDisplayKeyLength)
            .OrderBy(k => legacy.OrbMonitor is { } last && string.Equals(k, last, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(OrbsDocument.MaxArrangements)
            .ToArray();

        var layouts = new List<OrbLayout>(keys.Length);
        foreach (var key in keys)
        {
            var record = OrbPositionRecord.FromAnchor(key, legacy.OrbAnchors[key]).Validated();
            if (record is { } valid && OrbsDocument.IsValidArrangement(key))
            {
                var positions = ImmutableDictionary.Create<string, OrbPositionRecord>(StringComparer.Ordinal).Add(orb.Id, valid);
                layouts.Add(new OrbLayout(OrbsDocument.ArrangementKey([key]), positions));
            }
        }

        return new OrbsDocument([orb], layouts).Validated();
    }
}
