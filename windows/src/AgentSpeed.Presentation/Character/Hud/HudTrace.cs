namespace AgentSpeed.Presentation.Character;

public sealed partial class CharacterPerformer
{
    /// <summary>Parts whose top is the top of her head (the model chip clears it).</summary>
    private static readonly HashSet<string> HeadTags = new(StringComparer.Ordinal) { "face", "front hair", "back hair", "headwear", "ears" };

    /// <summary>
    /// Traces her drawn outline this frame into <paramref name="silhouette"/> (cleared first): every vertex of each visible
    /// part of the active pose, skinned as the GPU skins it, and every triangle edge long enough to skip a band, so a thin
    /// staff or a raised arm marks each band it crosses. Returns the anchors of the cards. <paramref name="scratch"/> holds
    /// skinned positions and only grows, so a warm trace never allocates.
    /// </summary>
    public HudAnchors Trace(HudSilhouette silhouette, (double X, double Y) feetOnScreen, double pointsPerPixel, ref double[] scratch)
    {
        ArgumentNullException.ThrowIfNull(silhouette);
        silhouette.Clear();
        var rig = ActiveRig;
        var placement = ActivePlacement;
        var k = pointsPerPixel * placement.Scale;
        var ox = feetOnScreen.X - placement.FeetX * k;
        var oy = feetOnScreen.Y - placement.FeetY * k;
        var headTop = double.PositiveInfinity;
        var step = silhouette.Step;
        var world = _world;
        for (var index = 0; index < rig.Parts.Length; index++)
        {
            if (index < _opacities.Length && !(_opacities[index] > 0.05))
            {
                continue;
            }

            var part = rig.Parts[index];
            var n = part.VertexCount;
            var m = part.Influences;
            if (scratch.Length < 2 * n)
            {
                Array.Resize(ref scratch, 2 * n);
            }

            var head = HeadTags.Contains(part.Tag);
            for (var v = 0; v < n; v++)
            {
                var x = part.Vertices[2 * v];
                var y = part.Vertices[2 * v + 1];
                double sx = 0, sy = 0;
                for (var j = 0; j < m; j++)
                {
                    var w = part.Weights[v * m + j];
                    if (w == 0)
                    {
                        continue;
                    }

                    var t = world[part.Bones[v * m + j]];
                    sx += w * (t.A * x + t.B * y + t.Tx);
                    sy += w * (t.C * x + t.D * y + t.Ty);
                }

                var px = ox + sx * k;
                var py = oy + sy * k;
                scratch[2 * v] = px;
                scratch[2 * v + 1] = py;
                silhouette.Add(px, py);
                if (head && py < headTop)
                {
                    headTop = py;
                }
            }

            var triangles = part.Triangles;
            for (var tri = 0; tri + 2 < triangles.Length; tri += 3)
            {
                var a = (int)triangles[tri];
                var b = (int)triangles[tri + 1];
                var c = (int)triangles[tri + 2];
                Edge(silhouette, scratch, a, b, step);
                Edge(silhouette, scratch, b, c, step);
                Edge(silhouette, scratch, c, a, step);
            }
        }

        var anchors = RestAnchors(feetOnScreen, pointsPerPixel);
        return double.IsFinite(headTop) ? anchors with { HeadTop = headTop } : anchors;
    }

    private static void Edge(HudSilhouette s, double[] p, int a, int b, double step)
    {
        var y0 = p[2 * a + 1];
        var y1 = p[2 * b + 1];
        if (!(Math.Abs(y1 - y0) > step))
        {
            return;
        }

        s.Add(p[2 * a], y0, p[2 * b], y1);
    }

    /// <summary>
    /// The anchors of the active pose at rest (face, chin and waist landmarks), moved with her side steps; without
    /// landmarks, shares of her height.
    /// </summary>
    public HudAnchors RestAnchors((double X, double Y) feetOnScreen, double pointsPerPixel)
    {
        var placement = ActivePlacement;
        var lm = ActiveRig.Landmarks;
        var dx = BodyOffset.Dx * pointsPerPixel;
        var h = IdleHeight * pointsPerPixel;
        if (lm.Face is not { Length: 4 } face)
        {
            var fy = feetOnScreen.Y;
            return new HudAnchors(feetOnScreen.X + dx, fy - h, fy - 0.68 * h, fy - 0.78 * h);
        }

        var chin = lm.Chin ?? face[3];
        var waist = lm.Waist is { Length: 2 } w ? w[1] : chin + 0.32 * IdleHeight / placement.Scale;
        var center = placement.Screen((face[0] + face[2]) / 2, face[1], feetOnScreen, pointsPerPixel);
        var shoulders = placement.Screen(0, chin + 0.25 * (waist - chin), feetOnScreen, pointsPerPixel).Y;
        var chest = placement.Screen(0, chin + 0.45 * (waist - chin), feetOnScreen, pointsPerPixel).Y;
        return new HudAnchors(center.X + dx, center.Y, chest, shoulders);
    }
}
