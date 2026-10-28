namespace AgentSpeed.Presentation.Motion;

public static class Easing
{
    public static double Clamp01(double t) => double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;

    public static double OutCubic(double t)
    {
        t = 1 - Clamp01(t);
        return 1 - t * t * t;
    }

    public static double OutQuad(double t)
    {
        t = Clamp01(t);
        return 1 - (1 - t) * (1 - t);
    }

    public static double InOutSine(double t) => -(Math.Cos(Math.PI * Clamp01(t)) - 1) / 2;

    public static double OutBack(double t, double overshoot = 1.70158)
    {
        t = Clamp01(t) - 1;
        return t * t * ((overshoot + 1) * t + overshoot) + 1;
    }

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3 - 2 * t);
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
