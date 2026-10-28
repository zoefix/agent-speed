using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// The value odometer of the floating orb styles (DESIGN 2a, Shared foundation): on a change only the characters that
/// changed roll, each in its own cell, 0.24 s ease-out, up when rising; values of a different length crossfade in 0.12 s.
/// </summary>
public sealed class OdometerText
{
    public const double RollSeconds = 0.24;
    public const double CrossfadeSeconds = 0.12;

    /// <summary>Roll distance as a fraction of the value size.</summary>
    public const double Travel = 0.92;

    public string Current { get; private set; } = "0";

    /// <summary>The value before the latest change (null before any change).</summary>
    public string? Previous { get; private set; }

    public bool Rising { get; private set; } = true;

    public double ChangedAt { get; private set; } = double.NegativeInfinity;

    public bool Animated { get; private set; }

    public void Set(string text, bool rising, double now, bool animate)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == Current)
        {
            return;
        }

        Previous = Current;
        Current = text;
        Rising = rising;
        ChangedAt = now;
        Animated = animate;
    }

    /// <summary>Linear progress 0..1 of the running change (1 when settled or not animated).</summary>
    public double Progress(double now) =>
        !Animated || Previous is null ? 1 : Math.Clamp((now - ChangedAt) / RollSeconds, 0, 1);

    public bool IsAnimating(double now) => Progress(now) < 1;

    /// <summary>True when the running change rolls per character (same length); otherwise it crossfades.</summary>
    public bool Rolls => Previous is { } previous && previous.Length == Current.Length;

    /// <summary>Eased roll amount 0..1.</summary>
    public double RollEase(double now) => Easing.OutCubic(Progress(now));

    /// <summary>Crossfade amount 0..1 for a change of length (runs over the first half of the roll time).</summary>
    public double CrossfadeAmount(double now) => Math.Clamp(Progress(now) * RollSeconds / CrossfadeSeconds, 0, 1);

    /// <summary>
    /// Vertical offsets (in units of the roll distance, y down) of the outgoing and incoming character for a changed
    /// cell: rising values move up, so the old character leaves upwards and the new one enters from below.
    /// </summary>
    public (double Outgoing, double Incoming) Offsets(double eased)
    {
        var direction = Rising ? -1 : 1;
        return (direction * eased, -direction * (1 - eased));
    }

    /// <summary>True when the character at <paramref name="index"/> (left to right) differs between the two values.</summary>
    public bool Changed(int index) =>
        Previous is { } previous && index < previous.Length && index < Current.Length && previous[index] != Current[index];
}
