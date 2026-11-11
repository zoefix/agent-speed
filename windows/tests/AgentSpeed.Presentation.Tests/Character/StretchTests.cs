using System.Text.Json;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>
/// <c>Squash</c> stretches (+) or squashes (-) her whole figure over the feet, face included: past <see cref="Held"/>
/// either way it reads as the art drawn out or pressed flat, so only an instant of a take-off or a landing goes there,
/// and the crouch before a leap no longer than <see cref="Crouch"/> (spec/CHARACTERS.md, Motion).
/// </summary>
public class StretchTests
{
    private const double Held = 0.2;
    private const double Brief = 0.25;
    private const double Crouch = 0.5;

    private static double Longest(IEnumerable<double> series, double dt, double sign = 1)
    {
        double run = 0, longest = 0;
        foreach (var v in series)
        {
            run = sign * v > Held + 1e-9 ? run + dt : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    private static void AssertInShape(IReadOnlyList<double> series, double dt, string name)
    {
        Assert.True(Longest(series, dt) < Brief, $"{name} stretched");
        Assert.True(Longest(series, dt, -1) < Crouch, $"{name} squashed");
    }

    private static IEnumerable<double> Squash(MotionSpec m, double dt)
    {
        var keys = m.Curves["Squash"];
        if (keys is null)
        {
            yield break;
        }

        var curve = new Curve(keys);
        for (var n = 0; n * dt <= m.Duration; n++)
        {
            yield return curve.Evaluate(n * dt);
        }
    }

    [Fact]
    public void NoActionHoldsHerDrawnOut()
    {
        const CharacterSide output = CharacterSide.Right, book = CharacterSide.Left;
        var actions = Moves.Names.Select(n => Moves.Make(n, output, book)!)
            .Concat(Moves.Profile.Keys.Select(n => Moves.ProfileMove(n, output, book)!))
            .Concat(Gestures.Head.Select(Gestures.HeadGesture))
            .Concat(Gestures.Face.Select(n => Gestures.FaceGesture(n)))
            .Concat([Moves.Sidestep(output, book, 0.5), Moves.Peek(output, book, 1), Moves.Pounce(output, book, 1)]);
        const double dt = 1.0 / 240;
        foreach (var m in actions)
        {
            AssertInShape(Squash(m, dt).ToList(), dt, m.Name);
        }

        var swap = new PoseSwitch();
        swap.Request(1);
        var hop = new List<double>();
        while (swap.IsRunning)
        {
            hop.Add(swap.Update(dt).Output?.Squash ?? 0);
        }

        AssertInShape(hop, dt, "pose swap");
    }

    [Fact]
    public void SheIsNeverHeldDrawnOutInAnyBand()
    {
        var parameters = CharacterFixture.Json("director.json").GetProperty("params").EnumerateArray().Select(CharacterParam.Decode).ToArray();
        var profile = MotionProfile.Decode(JsonDocument.Parse("""{"signature": ["stretch", "yawn", "tiptoe", "curtsy", "hop", "pounce"], "bounce": 1.4}""").RootElement);
        var director = new CharacterDirector(parameters, 7, CharacterSide.Right, CharacterSide.Left, -1, celebrate: false, profile: profile);
        var squash = director.Space.Slot("Squash")!.Value;
        int[] levels = [0, 0, 2, 6, 10, 13, 3, 0];
        const double dt = 1.0 / 60;
        const int frames = 240 * 60;
        var played = new HashSet<string>();
        var series = new List<double>(frames);
        for (var n = 0; n < frames; n++)
        {
            director.SetLevel(levels[n * levels.Length / frames]);
            director.Update(dt);
            series.Add(director.Values[squash]);
            if (director.ActiveGesture is { } g)
            {
                played.Add(g);
            }
        }

        Assert.Subset(played, new HashSet<string> { "stretch", "yawn", "tiptoe", "curtsy" });
        AssertInShape(series, dt, "director");
    }
}
