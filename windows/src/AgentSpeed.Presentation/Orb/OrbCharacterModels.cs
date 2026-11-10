using System.Globalization;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Lines;

namespace AgentSpeed.Presentation.Orb;

/// <summary>One row of the models card (DESIGN 2a.6): the model id, its fastest agent's speed and its vendor's accent.</summary>
/// <param name="Speed">Whole tok/s; null while the model has no timed response yet (<c>–</c>).</param>
/// <param name="Accent">The vendor's accent dot; null when no character stands for the vendor (drawn in the secondary token).</param>
public sealed record OrbModelRow(string Model, int? Speed, Rgba? Accent)
{
    public const string Dash = "–";

    /// <summary>The speed in tabular digits, or <c>–</c>.</summary>
    public string Number { get; } = Speed is { } speed ? speed.ToString(CultureInfo.InvariantCulture) : Dash;
}

/// <summary>The models card's rows and texts (DESIGN 2a.6, METRICS 6.9 <i>Readouts of a set</i>).</summary>
public static class OrbModelsText
{
    /// <summary>At most this many rows; more models add the <c>character.models.more</c> line.</summary>
    public const int MaxRows = 4;

    /// <summary>Below S = 96 the card keeps its first two rows (DESIGN 2a.6, LOD).</summary>
    public const int SmallRows = 2;

    /// <summary>The running models in their order, each with the accent <paramref name="accent"/> picks for it.</summary>
    public static IReadOnlyList<OrbModelRow> Rows(IReadOnlyList<PerfRunningModel> models, Func<PerfRunningModel, Rgba?> accent)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(accent);
        var rows = new OrbModelRow[models.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new OrbModelRow(models[i].Model, models[i].Shown, accent(models[i]));
        }

        return rows;
    }

    /// <summary><c>character.models.more</c> for the models beyond the rows shown (<c>+3 個模型</c>).</summary>
    public static string More(int hidden, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Format("character.models.more", hidden.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <c>a11y.orb.models</c> with one <c>a11y.orb.modelSpeed</c> per model (the full id; the model alone while it has no
    /// speed), joined by <c>list.separator</c>; null without rows.
    /// </summary>
    public static string? Spoken(IReadOnlyList<OrbModelRow> rows, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(localizer);
        if (rows.Count == 0)
        {
            return null;
        }

        var items = rows.Select(r => r.Speed is { } speed
            ? localizer.Format("a11y.orb.modelSpeed", r.Model, speed.ToString(CultureInfo.InvariantCulture))
            : r.Model);
        return localizer.Format("a11y.orb.models", string.Join(localizer["list.separator"], items));
    }
}

/// <summary>
/// The quiet rule of the models card (DESIGN 2a.6, Which cards show): loud while a model runs; it keeps its last rows for
/// the 10 s after the last loud moment and while it fades out, then lets them go. Times are seconds on the shared orb clock.
/// </summary>
public sealed class OrbModelsQuietRule
{
    public IReadOnlyList<OrbModelRow> Rows { get; private set; } = [];

    /// <summary>10 s after the last tick with a running model; the card fades out from then.</summary>
    public double QuietAt { get; private set; } = double.NegativeInfinity;

    /// <summary>Changes whenever <see cref="Rows"/> does, so the card's rows glide and fade from the last ones they had.</summary>
    public int Version { get; private set; }

    /// <summary>Applies one engine tick; true when the rows or the card's loudness changed.</summary>
    public bool Apply(IReadOnlyList<OrbModelRow> rows, double time)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 0)
        {
            var loudBefore = IsLoud(time);
            QuietAt = time + OrbLinesQuietRule.QuietAfter;
            return Replace(rows) || !loudBefore;
        }

        return Rows.Count > 0 && time >= QuietAt + HudWings.FadeOut && Replace([]);
    }

    public bool IsLoud(double time) => Rows.Count > 0 && time < QuietAt;

    private bool Replace(IReadOnlyList<OrbModelRow> rows)
    {
        if (rows.SequenceEqual(Rows))
        {
            return false;
        }

        Rows = rows;
        Version++;
        return true;
    }
}
