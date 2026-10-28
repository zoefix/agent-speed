namespace AgentSpeed.Presentation.Hero.Badge;

/// <summary>
/// Which art the dashboard hero's center shows (DESIGN 4c): the vehicle for the Vehicles title theme (and custom sets
/// based on it), else the title badge. A change crossfades between them in 0.3 s; Reduce Motion cuts.
/// </summary>
public sealed class HeroArtMix
{
    public const double Crossfade = 0.3;

    private double _changedAt = double.NegativeInfinity;
    private double _from;

    /// <summary>Null before the first <see cref="Update"/>.</summary>
    public bool? ShowsBadge { get; private set; }

    /// <summary>0 = the vehicle, 1 = the badge.</summary>
    public double BadgeMix(double time)
    {
        var target = ShowsBadge == true ? 1.0 : 0.0;
        var p = HeroMath.Clamp01((time - _changedAt) / Crossfade);
        return _from + (target - _from) * HeroMath.Smooth(p);
    }

    /// <summary>Notes the art the theme asks for; true when it changed.</summary>
    public bool Update(bool showsBadge, double time, bool reduceMotion)
    {
        if (ShowsBadge == showsBadge)
        {
            return false;
        }

        var cut = ShowsBadge is null || reduceMotion || !double.IsFinite(time);
        _from = cut ? showsBadge ? 1 : 0 : BadgeMix(time);
        ShowsBadge = showsBadge;
        _changedAt = cut ? double.NegativeInfinity : time;
        return true;
    }

    /// <summary>Both arts are drawn while they crossfade.</summary>
    public bool IsFading(double time) => time - _changedAt < Crossfade;
}
