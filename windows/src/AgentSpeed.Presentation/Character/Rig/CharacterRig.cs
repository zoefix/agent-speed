using System.Runtime.CompilerServices;

namespace AgentSpeed.Presentation.Character;

/// <summary>A 2D affine transform: x' = a x + b y + tx, y' = c x + d y + ty.</summary>
public readonly record struct Affine2(double A, double B, double C, double D, double Tx, double Ty)
{
    public static Affine2 Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>this · other (other applied first).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Affine2 Then(in Affine2 o) => new(A * o.A + B * o.C, A * o.B + B * o.D, C * o.A + D * o.C, C * o.B + D * o.D,
        A * o.Tx + B * o.Ty + Tx, C * o.Tx + D * o.Ty + Ty);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);
}

/// <summary>
/// A compiled rig (spec/CHARACTERS.md, Rig): parameters by index, bones parents-first, drivers per bone and channel,
/// parts in draw order with their skinning data.
/// </summary>
public sealed class CharacterRig
{
    public enum Channel
    {
        Rotate,
        X,
        Y,
        ScaleX,
        ScaleY,
    }

    public sealed record Driver(int Param, Channel Channel, double[] Xs, double[] Ys);

    public sealed record OpacityDriver(int Param, double[] Xs, double[] Ys);

    /// <param name="Vertices">Rest positions, x y pairs in canvas pixels.</param>
    /// <param name="Uv">Atlas pixel coordinates, x y pairs.</param>
    /// <param name="Bones">Bone indices, <paramref name="Influences"/> per vertex.</param>
    /// <param name="Weights">Weights, <paramref name="Influences"/> per vertex, summing to 1.</param>
    public sealed record Part(string Name, string Tag, double Order, double[] Vertices, double[] Uv, uint[] Triangles, int Influences,
        ushort[] Bones, double[] Weights, OpacityDriver[] Opacity)
    {
        public int VertexCount => Vertices.Length / 2;
    }

    public CharacterRig(RigDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.FormatName != RigDocument.Format || doc.FormatVersion != RigDocument.Version)
        {
            throw CharacterRigException.Unsupported(doc.FormatName, doc.FormatVersion);
        }

        Name = doc.Name;
        CanvasWidth = doc.Canvas.Length > 0 ? doc.Canvas[0] : 0;
        CanvasHeight = doc.Canvas.Length > 1 ? doc.Canvas[1] : 0;
        AtlasFile = doc.AtlasInfo.File;
        AtlasWidth = doc.AtlasInfo.Size.Length > 0 ? doc.AtlasInfo.Size[0] : 0;
        AtlasHeight = doc.AtlasInfo.Size.Length > 1 ? doc.AtlasInfo.Size[1] : 0;
        var parameters = new List<CharacterParam>();
        var paramIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in doc.Params)
        {
            if (paramIndex.TryAdd(p.Id, parameters.Count))
            {
                parameters.Add(p);
            }
        }

        Params = parameters.ToArray();
        ParamIndex = paramIndex;

        var boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var parents = new int[doc.Bones.Length];
        var pivots = new (double X, double Y)[doc.Bones.Length];
        for (var i = 0; i < doc.Bones.Length; i++)
        {
            var b = doc.Bones[i];
            if (b.Pivot.Length != 2 || boneIndex.ContainsKey(b.Name))
            {
                throw CharacterRigException.BadBone(b.Name);
            }

            var parent = -1;
            if (b.Parent is { } name)
            {
                if (!boneIndex.TryGetValue(name, out parent))
                {
                    throw CharacterRigException.ParentsNotFirst(b.Name);
                }
            }

            boneIndex[b.Name] = i;
            parents[i] = parent;
            pivots[i] = (b.Pivot[0], b.Pivot[1]);
        }

        BoneNames = doc.Bones.Select(b => b.Name).ToArray();
        Parents = parents;
        Pivots = pivots;

        var drivers = new List<Driver>[doc.Bones.Length];
        for (var i = 0; i < drivers.Length; i++)
        {
            drivers[i] = [];
        }

        foreach (var d in doc.Drivers)
        {
            if (!boneIndex.TryGetValue(d.Bone, out var bone) || !paramIndex.TryGetValue(d.Param, out var param) || ChannelOf(d.Channel) is not { } channel
                || d.Keys.Length == 0 || d.Keys.Any(k => k.Length != 2))
            {
                throw CharacterRigException.BadDriver($"{d.Param} → {d.Bone}.{d.Channel}");
            }

            drivers[bone].Add(new Driver(param, channel, d.Keys.Select(k => k[0]).ToArray(), d.Keys.Select(k => k[1]).ToArray()));
        }

        Drivers = drivers.Select(list => list.ToArray()).ToArray();

        var opacity = new Dictionary<string, List<OpacityDriver>>(StringComparer.Ordinal);
        foreach (var d in doc.PartDrivers)
        {
            if (!paramIndex.TryGetValue(d.Param, out var param) || d.Keys.Length == 0)
            {
                continue;
            }

            if (!opacity.TryGetValue(d.Part, out var list))
            {
                opacity[d.Part] = list = [];
            }

            list.Add(new OpacityDriver(param, d.Keys.Select(k => k.Length > 0 ? k[0] : 0).ToArray(), d.Keys.Select(k => k.Length > 1 ? k[1] : 0).ToArray()));
        }

        var ordered = doc.Parts.Select((p, i) => (p, i)).OrderBy(x => x.p.Order).ThenBy(x => x.i).Select(x => x.p);
        var parts = new List<Part>();
        foreach (var p in ordered)
        {
            var n = p.Vertices.Length / 2;
            if (p.Vertices.Length % 2 != 0 || p.Uv.Length != p.Vertices.Length || p.Triangles.Length % 3 != 0 || p.Influences <= 0
                || p.Bones.Length != n * p.Influences || p.Weights.Length != n * p.Influences
                || p.Triangles.Any(t => t < 0 || t >= n) || p.Bones.Any(b => b < 0 || b >= doc.Bones.Length))
            {
                throw CharacterRigException.BadPart(p.Name);
            }

            parts.Add(new Part(p.Name, p.Tag, p.Order, p.Vertices, p.Uv, p.Triangles.Select(t => (uint)t).ToArray(), p.Influences,
                p.Bones.Select(b => (ushort)b).ToArray(), p.Weights, opacity.TryGetValue(p.Name, out var o) ? o.ToArray() : []));
        }

        Parts = parts.ToArray();
        foreach (var chain in doc.Physics)
        {
            if (chain.Anchor.Length != 2 || chain.Segments.Length == 0
                || chain.Outputs.Any(o => o.Segment < 0 || o.Segment >= chain.Segments.Length || o.Degrees == 0))
            {
                throw CharacterRigException.BadChain(chain.Name);
            }
        }

        Physics = doc.Physics;
        Landmarks = doc.LandmarkSet;
    }

    public string Name { get; }

    public double CanvasWidth { get; }

    public double CanvasHeight { get; }

    public string AtlasFile { get; }

    public int AtlasWidth { get; }

    public int AtlasHeight { get; }

    public CharacterParam[] Params { get; }

    public IReadOnlyDictionary<string, int> ParamIndex { get; }

    public string[] BoneNames { get; }

    public int[] Parents { get; }

    public (double X, double Y)[] Pivots { get; }

    public Driver[][] Drivers { get; }

    public Part[] Parts { get; }

    public RigDocument.Chain[] Physics { get; }

    public RigDocument.Landmarks Landmarks { get; }

    public int BoneCount => Parents.Length;

    internal static Channel? ChannelOf(string name) => name switch
    {
        "rotate" => Channel.Rotate,
        "x" => Channel.X,
        "y" => Channel.Y,
        "scaleX" => Channel.ScaleX,
        "scaleY" => Channel.ScaleY,
        _ => null,
    };

    /// <summary>Defaults of every parameter, in parameter order.</summary>
    public double[] Defaults() => Params.Select(p => p.Default).ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double Clamped(ReadOnlySpan<double> values, int i)
    {
        var p = Params[i];
        return i < values.Length ? Math.Min(Math.Max(values[i], p.Min), p.Max) : p.Default;
    }

    /// <summary>World transform of every bone for parameter values in parameter order (clamped to their ranges).</summary>
    public void WorldTransforms(ReadOnlySpan<double> values, ref Affine2[] world)
    {
        if (world.Length != BoneCount)
        {
            world = new Affine2[BoneCount];
        }

        for (var i = 0; i < BoneCount; i++)
        {
            double rotate = 0, x = 0, y = 0, scaleX = 0, scaleY = 0;
            foreach (var d in Drivers[i])
            {
                var v = CharacterMath.Interpolate(d.Xs, d.Ys, Clamped(values, d.Param));
                switch (d.Channel)
                {
                    case Channel.Rotate:
                        rotate += v;
                        break;
                    case Channel.X:
                        x += v;
                        break;
                    case Channel.Y:
                        y += v;
                        break;
                    case Channel.ScaleX:
                        scaleX += v;
                        break;
                    case Channel.ScaleY:
                        scaleY += v;
                        break;
                }
            }

            var (px, py) = Pivots[i];
            var r = rotate * (Math.PI / 180);
            var c = Math.Cos(r);
            var s = Math.Sin(r);
            var sx = 1 + scaleX;
            var sy = 1 + scaleY;
            var a = c * sx;
            var b = -s * sy;
            var cc = s * sx;
            var dd = c * sy;
            var local = new Affine2(a, b, cc, dd, px - (a * px + b * py) + x, py - (cc * px + dd * py) + y);
            world[i] = Parents[i] < 0 ? local : world[Parents[i]].Then(local);
        }
    }

    /// <summary>Opacity of every part (the product of its drivers, 1 without any), in draw order.</summary>
    public void Opacities(ReadOnlySpan<double> values, ref double[] output)
    {
        if (output.Length != Parts.Length)
        {
            output = new double[Parts.Length];
        }

        for (var i = 0; i < Parts.Length; i++)
        {
            var o = 1.0;
            foreach (var d in Parts[i].Opacity)
            {
                o *= CharacterMath.Interpolate(d.Xs, d.Ys, Clamped(values, d.Param));
            }

            output[i] = o;
        }
    }

    /// <summary>Skinned positions of a part's vertices (x y pairs), as the GPU computes them.</summary>
    public void Skin(int index, Affine2[] world, ref double[] output)
    {
        var part = Parts[index];
        var n = part.VertexCount;
        var k = part.Influences;
        if (output.Length != n * 2)
        {
            output = new double[n * 2];
        }

        for (var v = 0; v < n; v++)
        {
            var x = part.Vertices[2 * v];
            var y = part.Vertices[2 * v + 1];
            double sx = 0, sy = 0;
            for (var j = 0; j < k; j++)
            {
                var w = part.Weights[v * k + j];
                if (w == 0)
                {
                    continue;
                }

                var (px, py) = world[part.Bones[v * k + j]].Apply(x, y);
                sx += w * px;
                sy += w * py;
            }

            output[2 * v] = sx;
            output[2 * v + 1] = sy;
        }
    }
}
