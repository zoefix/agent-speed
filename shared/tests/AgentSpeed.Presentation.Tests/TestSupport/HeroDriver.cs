using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>
/// Drives a <see cref="HeroStage"/> at 60 fps with explicit titles (the tests set the title the label would show).
/// The tier follows the value unless <see cref="Tier"/> is set.
/// </summary>
internal sealed class HeroDriver
{
    public const double FrameSeconds = 1.0 / 60;

    public HeroDriver(string? parkedKey = null, HeroOptions? options = null, double redline = HeatMath.ReferenceRedline, uint seed = 7)
    {
        Stage = new HeroStage(Art, parkedKey, seed);
        Options = options ?? new HeroOptions(EffectsIntensity.Standard);
        Redline = redline;
        Update();
    }

    public static VehicleArt Art => VehicleArt.Shared ?? throw new InvalidOperationException(VehicleArt.LoadError);

    public HeroStage Stage { get; }

    public double Now { get; private set; }

    public double Value { get; set; }

    public double Redline { get; set; }

    public SpeedTitle Title { get; set; } = SpeedTitle.Idle;

    /// <summary>Overrides the tier; null derives it from the value.</summary>
    public SpeedTier? Tier { get; set; }

    public HeroOptions Options { get; set; }

    public static HeroOptions Level(EffectsIntensity level, bool dark = true) => new(level, Dark: dark);

    public void Update(bool snap = false) =>
        Stage.Update(Now, Value, Redline, Tier ?? HeatMath.RawTier(Value, Redline), Title, Options, snap);

    public void Frame(bool snap = false)
    {
        Now += FrameSeconds;
        Update(snap);
    }

    /// <summary>Sets the inputs and advances one frame; returns the time of that frame (the title change, if any).</summary>
    public double Change(double value, SpeedTitle title, bool snap = false)
    {
        Value = value;
        Title = title;
        Frame(snap);
        return Now;
    }

    public void Run(double seconds, Action<HeroStage>? each = null)
    {
        var frames = (int)Math.Round(seconds / FrameSeconds);
        for (var i = 0; i < frames; i++)
        {
            Frame();
            each?.Invoke(Stage);
        }
    }

    /// <summary>Advances in whole frames while they stay before <paramref name="time"/>, then updates exactly at it.</summary>
    public void RunTo(double time)
    {
        while (Now + FrameSeconds < time)
        {
            Frame();
        }

        Now = time;
        Update();
    }

    public static T Some<T>(T? value)
        where T : struct =>
        value ?? throw new Xunit.Sdk.XunitException($"Expected a {typeof(T).Name}, got null.");
}
