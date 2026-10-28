namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Fixed geometry of the <c>needle</c> style (DESIGN 2a.2). Radii and widths are fractions of S from the disc center;
/// dial positions <c>n</c> map to <see cref="StyleMath.DialDegrees"/>.
/// </summary>
public static class NeedleGeometry
{
    public const double ZeroStop = -0.012;
    public const double Peg = 1.012;

    public const double DialBandRadius = 0.41;
    public const double DialBandWidth = 0.075;
    public const double DialBandStart = -0.004;
    public const double DialBandEnd = 1.004;

    public const double RedZoneRadius = 0.4655;
    public const double RedZoneWidth = 0.017;
    public const double RedZoneEnd = 1;

    public const int TickCount = 41;
    public const int TickDivisions = 40;
    public const int MajorEvery = 5;
    public const double TickOuterRadius = 0.445;
    public const double MajorLength = 0.062;
    public const double MajorWidth = 0.014;
    public const double MinorLength = 0.03;
    public const double MinorWidth = 0.0075;

    public const double TraceRadius = 0.366;
    public const double TraceWidth = 0.011;

    public const double PinRadius = 0.405;
    public const double PinSize = 0.011;
    public const double ZeroPin = -0.034;
    public const double PegPin = 1.034;

    public const int LampCount = 5;
    public const double LampRingRadius = 0.33;
    public const double LampSize = 0.019;

    /// <summary>The lit lamp fills 86 % of its housing (prototype).</summary>
    public const double LampLitScale = 0.86;

    public const double WakeInnerRadius = 0.235;
    public const double WakeOuterRadius = 0.43;

    public const double NeedleTail = -0.09;
    public const double NeedleTip = 0.44;
    public const double NeedleBaseWidth = 0.062;
    public const double NeedleTipWidth = 0.012;

    /// <summary>The blade edges at the counterweight are 90 % of the pivot width (prototype).</summary>
    public const double NeedleTailWidthScale = 0.9;

    public const double SpineStart = 0.21;
    public const double SpineWidth = 0.0045;
    public const double FlareSpotRadius = 0.06;

    public const double HubRadius = 0.235;
    public const double HubBezelWidth = 0.013;
    public const double HubShadowSigma = 0.025;
    public const double HubShadowOffsetY = 0.012;

    /// <summary>Where the needle grinds against the peg pin (sparks are born here).</summary>
    public const double ContactDial = 1.024;

    public const double ContactRadius = 0.405;

    /// <summary>The red zone and the last lamp start at the redline-tier threshold, <c>0.75^0.6</c>.</summary>
    public static double RedZoneStart => StyleMath.RedlineDial;

    private static readonly double[] LampDials = [0.70, 0.735, 0.77, 0.805, StyleMath.RedlineDial];
    private static readonly double[] LampAngles = [-110, -100, -90, -80, -70];

    /// <summary>Lamp i lights when the needle passes this dial position.</summary>
    public static ReadOnlySpan<double> LampThresholds => LampDials;

    /// <summary>Lamp centers in degrees (on <see cref="LampRingRadius"/>).</summary>
    public static ReadOnlySpan<double> LampDegrees => LampAngles;

    public static double TickDial(int k) => k / (double)TickDivisions;

    public static bool IsMajor(int k) => k % MajorEvery == 0;
}
