namespace AgentSpeed.Presentation.Character;

/// <summary>
/// Turn side-on, swap the illustration, turn back (spec/CHARACTERS.md, Pose swap): <c>Spin</c> runs 0 → 0.25 (edge-on)
/// over 0.2 s, the frame that crosses 0.2 s shows the new pose at exactly 0.75 (also edge-on), then <c>Spin</c> runs on
/// to 1 over 0.32 s with a small squash, lift and skirt flare, and returns to 0.
/// </summary>
public sealed class PoseSwitch
{
    public const double OutDuration = 0.2;
    public const double InDuration = 0.32;

    private readonly Curve _hop = new([new(0, 0), new(0.08, -0.25), new(0.18, 0.35), new(0.36, 0.1), new(0.46, -0.3), new(0.52, 0)]);
    private readonly Curve _lift = new([new(0, 0), new(0.12, 0), new(0.3, 0.07), new(0.46, 0), new(0.52, 0)]);
    private readonly Curve _flare = new([new(0, 0), new(0.2, 0.7), new(0.4, 0.5), new(0.7, 0)]);

    public double? T { get; private set; }

    public int? Target { get; private set; }

    public bool IsRunning => T is not null;

    /// <summary>The values the swap writes this frame.</summary>
    public readonly record struct Output(double Spin, double Squash, double PosY, double SkirtFlare);

    /// <summary>Starts a swap toward <paramref name="target"/>; ignored while one runs.</summary>
    public void Request(int target)
    {
        if (T is null)
        {
            T = 0;
            Target = target;
        }
    }

    /// <summary>
    /// Starts on the edge-on instant, for a character that takes over from another one: the next update shows
    /// <c>Spin</c> = 0.75 and turns back from there.
    /// </summary>
    public void EnterEdgeOn()
    {
        T = OutDuration - 1e-9;
        Target = null;
    }

    /// <summary>
    /// Advances one frame. Returns the target on the edge-on frame that swaps (else null) and the values to apply:
    /// <c>Spin</c> replaces, squash and lift add, the flare takes the larger value.
    /// </summary>
    public (int? Swap, Output? Output, bool Swapped) Update(double dt)
    {
        if (T is not { } before)
        {
            return (null, null, false);
        }

        var now = before + dt;
        int? swap = null;
        var swapped = false;
        double spin;
        if (now < OutDuration)
        {
            var k = now / OutDuration;
            spin = 0.25 * k * k;
        }
        else
        {
            if (before < OutDuration)
            {
                swap = Target;
                swapped = true;
                now = OutDuration;
            }

            var k = Math.Min(1.0, (now - OutDuration) / InDuration);
            spin = 0.75 + 0.25 * (1 - Math.Pow(1 - k, 3));
        }

        var output = new Output(spin, _hop.Evaluate(now), _lift.Evaluate(now), _flare.Evaluate(now));
        if (now >= OutDuration + InDuration)
        {
            output = output with { Spin = 0 };
            T = null;
        }
        else
        {
            T = now;
        }

        return (swap, output, swapped);
    }
}
