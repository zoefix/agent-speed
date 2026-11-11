namespace AgentSpeed.Presentation.Character;

/// <summary>
/// Head gestures with the body joining in (motions/gestures.py). Arm gestures, two-arm combos and face gestures need
/// pose or expression variants; packs without them leave those out (spec/CHARACTERS.md, Motion).
/// </summary>
internal static class Gestures
{
    public static readonly string[] Head = ["nod", "shake", "tilt", "lookaround", "giggle", "doubletake"];
    public static readonly string[] Face = ["yawn", "hum"];
    public static readonly string[] Arm = ["wave", "peace", "fist", "idea", "chin", "hairtuck", "present", "hip", "hug", "raise", "open"];
    public static readonly string[] Combos = ["cheer", "hooray", "eureka", "ponder", "cuddle", "proud"];

    public static MotionSpec HeadGesture(string name)
    {
        var c = new CurveSet();
        switch (name)
        {
            case "nod":
                c.Set("AngleY", [(0, 0), (0.18, 0.4), (0.38, -0.15), (0.58, 0.35), (0.8, -0.05), (1.1, 0)]);
                c.Set("Squash", [(0, 0), (0.18, -0.12), (0.38, 0.04), (0.58, -0.1), (0.8, 0), (1.1, 0)]);
                c.Set("EyeOpen.left", [(0, 1), (0.2, 0.7), (0.6, 0.7), (1.1, 1)]);
                c.Set("EyeOpen.right", [(0, 1), (0.2, 0.7), (0.6, 0.7), (1.1, 1)]);
                return new MotionSpec("nod", 1.1, c, expression: "smile");
            case "shake":
                c.Set("AngleX", [(0, 0), .. Keys.Oscillate(0.1, 1.1, 0.42, 0.4), (1.3, 0)]);
                c.Set("BodyAngleZ", [(0, 0), .. Keys.Oscillate(0.15, 1.15, 0.42, 0.12), (1.3, 0)]);
                c.Set("AngleY", [(0, 0), (0.2, -0.15), (1.1, -0.15), (1.3, 0)]);
                return new MotionSpec("shake", 1.3, c);
            case "tilt":
                c.Set("AngleZ", [(0, 0), (0.35, 0.7), (1.8, 0.65), (2.2, 0)]);
                c.Set("AngleX", [(0, 0), (0.35, 0.25), (1.8, 0.25), (2.2, 0)]);
                c.Set("EyeBallX", [(0, 0), (0.3, 0.5), (1.8, 0.5), (2.2, 0)]);
                c.Set("Hip", [(0, 0), (0.45, 0.35), (1.8, 0.35), (2.2, 0)]);
                c.Set("BodyAngleZ", [(0, 0), (0.45, -0.2), (1.8, -0.2), (2.2, 0)]);
                return new MotionSpec("tilt", 2.2, c, fade: 0.2, expression: "smile");
            case "lookaround":
                c.Set("EyeBallX", [(0, 0), (0.2, -0.9), (0.9, -0.9), (1.1, 0.9), (2.2, 0.9), (2.8, 0)]);
                c.Set("AngleX", [(0, 0), (0.45, -0.55), (1.0, -0.55), (1.6, 0.55), (2.3, 0.5), (2.8, 0)]);
                c.Set("AngleY", [(0, 0), (0.45, 0.1), (1.3, -0.05), (2.3, 0.1), (2.8, 0)]);
                c.Set("Hip", [(0, 0), (0.5, -0.25), (1.0, -0.25), (1.7, 0.25), (2.3, 0.2), (2.8, 0)]);
                c.Set("Lean", [(0, 0), (0.5, -0.15), (1.0, -0.15), (1.7, 0.15), (2.3, 0.1), (2.8, 0)]);
                return new MotionSpec("lookaround", 2.8, c, fade: 0.2);
            case "giggle":
                c.Set("BodyAngleZ", [(0, 0), .. Keys.Oscillate(0.1, 1.4, 0.17, 0.12), (1.6, 0)]);
                c.Set("Shoulder.left", [(0, 0), .. Keys.Oscillate(0.1, 1.4, 0.34, 0.25, baseValue: 0.3), (1.6, 0)]);
                c.Set("Shoulder.right", [(0, 0), .. Keys.Oscillate(0.1, 1.4, 0.34, 0.25, baseValue: 0.3), (1.6, 0)]);
                c.Set("Squash", [(0, 0), .. Keys.Oscillate(0.1, 1.4, 0.34, 0.08, baseValue: -0.05), (1.6, 0)]);
                c.Set("AngleY", [(0, 0), (0.2, -0.25), (1.4, -0.2), (1.6, 0)]);
                c.Set("AngleZ", [(0, 0), (0.2, 0.3), (1.4, 0.3), (1.6, 0)]);
                c.Set("Breath", [(0, 0), (0.3, 1), (1.6, 0.3)]);
                return new MotionSpec("giggle", 1.6, c, expression: "happy");
            default:
                c.Set("AngleX", [(0, 0), (0.15, 0.35), (0.35, 0.3), (0.5, -0.1), (0.65, 0.6), (1.2, 0.5), (1.5, 0)]);
                c.Set("AngleY", [(0, 0), (0.6, 0.35), (1.2, 0.3), (1.5, 0)]);
                c.Set("EyeBallX", [(0, 0), (0.6, 0.8), (1.2, 0.8), (1.5, 0)]);
                c.Set("PosY", [(0, 0), (0.55, 0), (0.66, 0.07), (0.8, 0), (1.5, 0)]);
                c.Set("Squash", [(0, 0), (0.5, -0.25), (0.62, 0.3), (0.82, -0.15), (1.0, 0), (1.5, 0)]);
                c.Set("Shoulder.left", [(0, 0), (0.62, 0.6), (1.2, 0.3), (1.5, 0)]);
                c.Set("Shoulder.right", [(0, 0), (0.62, 0.6), (1.2, 0.3), (1.5, 0)]);
                return new MotionSpec("doubletake", 1.5, c, fade: 0.1, expression: "surprised");
        }
    }

    public static MotionSpec FaceGesture(string name, double duration = 2.4)
    {
        var c = new CurveSet();
        if (name == "yawn")
        {
            c.Set("AngleY", [(0, 0), (0.5, 0.45), (1.6, 0.4), (duration, 0)]);
            c.Set("AngleZ", [(0, 0), (0.6, -0.35), (duration, 0)]);
            c.Set("Breath", [(0, 0), (0.7, 1.0), (1.8, 0.2), (duration, 0)]);
            c.Set("Squash", [(0, 0), (0.7, 0.15), (1.5, 0.13), (duration, 0)]);
            c.Set("Shoulder.left", [(0, 0), (0.7, 0.9), (1.6, 0.8), (duration, 0)]);
            c.Set("Shoulder.right", [(0, 0), (0.7, 0.9), (1.6, 0.8), (duration, 0)]);
            c.Set("FootPoint", [(0, 0), (0.7, 0.5), (1.5, 0.5), (duration, 0)]);
            c.Set("Lean", [(0, 0), (0.8, 0.1), (1.6, -0.08), (duration, 0)]);
            return new MotionSpec("yawn", duration, c, fade: 0.25, expression: "sleepy");
        }

        var d = duration + 1.0;
        c.Set("AngleZ", Keys.Oscillate(0, d, 1.2, 0.3));
        c.Set("AngleX", [(0, 0), (0.4, -0.25), (d, 0)]);
        c.Set("Hip", Keys.Oscillate(0, d, 1.2, 0.3, phase: Math.PI));
        c.Set("BodyAngleZ", Keys.Oscillate(0, d, 1.2, 0.15));
        return new MotionSpec("hum", d, c, fade: 0.25, expression: "smile", talk: true);
    }
}
