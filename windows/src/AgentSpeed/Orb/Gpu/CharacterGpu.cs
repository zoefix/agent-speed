using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentSpeed.App;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// The character style's GPU passes (DESIGN 2a.6, Techniques): each pose's skinned mesh (static vertices and indices made
/// once per pose, bone matrices and part opacities uploaded per draw), the soft lights and the atlas sprites. Atlases are
/// decoded off the render thread at the resolution they are drawn at (√2 steps, CharacterAtlasScale), premultiplied with
/// mipmaps, and released two seconds after the last frame that asked for them, so only the poses on screen stay resident.
/// </summary>
internal sealed class CharacterGpu : IDisposable
{
    private const int MaxAtlasSide = 8192;
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(2);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MeshVertex
    {
        public Vector2 Position;
        public Vector2 Uv;
        public Vector4 Weights;
        public ushort Bone0;
        public ushort Bone1;
        public ushort Bone2;
        public ushort Bone3;
        public uint Part;

        public const int Stride = 44;
    }

    private sealed class PoseGpu(ID3D11Buffer vertices, ID3D11Buffer indices, int indexCount) : IDisposable
    {
        public ID3D11Buffer Vertices { get; } = vertices;

        public ID3D11Buffer Indices { get; } = indices;

        public int IndexCount { get; } = indexCount;

        public DateTime LastUsed { get; set; }

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
        }
    }

    private sealed class AtlasEntry
    {
        public ID3D11ShaderResourceView? View { get; set; }

        public ID3D11Texture2D? Texture { get; set; }

        public byte[]? Pixels { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public bool Failed { get; set; }

        public DateTime LastUsed { get; set; }

        public long Bytes => (long)Width * Height * 4 * 4 / 3;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _meshVertex;
    private readonly ID3D11PixelShader _meshPixel;
    private readonly ID3D11VertexShader _dotVertex;
    private readonly ID3D11PixelShader _dotPixel;
    private readonly ID3D11VertexShader _spriteVertex;
    private readonly ID3D11PixelShader _spritePixel;
    private readonly ID3D11InputLayout _meshLayout;
    private readonly ID3D11InputLayout _dotLayout;
    private readonly ID3D11InputLayout _spriteLayout;
    private readonly ID3D11Buffer _meshParams;
    private readonly float[] _meshScratch = new float[8 + 256 * 4 + 128 * 4];
    private readonly Dictionary<CharacterPose, PoseGpu> _poses = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Path, int Step), AtlasEntry> _atlases = new();
    private readonly object _gate = new();
    private ID3D11Buffer? _dots;
    private int _dotCapacity;
    private ID3D11Buffer? _sprites;
    private int _spriteCapacity;
    private DateTime _lastSweep;

    public CharacterGpu(ID3D11Device device, OrbShaderCode code)
    {
        _device = device;
        _meshVertex = device.CreateVertexShader(code.MeshVertex);
        _meshPixel = device.CreatePixelShader(code.MeshPixel);
        _dotVertex = device.CreateVertexShader(code.DotVertex);
        _dotPixel = device.CreatePixelShader(code.DotPixel);
        _spriteVertex = device.CreateVertexShader(code.SpriteVertex);
        _spritePixel = device.CreatePixelShader(code.SpritePixel);
        _meshLayout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 16, 0),
            new InputElementDescription("BLENDINDICES", 0, Format.R16G16B16A16_UInt, 32, 0),
            new InputElementDescription("PART", 0, Format.R32_UInt, 40, 0),
        ], code.MeshVertex);
        _dotLayout = device.CreateInputLayout(InstanceElements(3), code.DotVertex);
        _spriteLayout = device.CreateInputLayout(InstanceElements(3), code.SpriteVertex);
        _meshParams = device.CreateBuffer((uint)(_meshScratch.Length * 4), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
    }

    /// <summary>A pack's atlas finished decoding (any thread): the orbs draw a frame so it shows.</summary>
    public static event EventHandler? AssetReady;

    /// <summary>Atlases still decoding or waiting for their upload (harnesses wait for none).</summary>
    public int PendingDecodes
    {
        get
        {
            lock (_gate)
            {
                return _atlases.Values.Count(a => a.View is null && !a.Failed);
            }
        }
    }

    /// <summary>Bytes of the resident atlases (mipmaps included), for the memory budget.</summary>
    public long ResidentBytes
    {
        get
        {
            lock (_gate)
            {
                return _atlases.Values.Where(a => a.View is not null).Sum(a => a.Bytes);
            }
        }
    }

    private static InputElementDescription[] InstanceElements(int count)
    {
        var elements = new InputElementDescription[count];
        for (var i = 0; i < count; i++)
        {
            elements[i] = new InputElementDescription("INSTANCE", (uint)i, Format.R32G32B32A32_Float, (uint)(i * 16), 0,
                InputClassification.PerInstanceData, 1);
        }

        return elements;
    }

    /// <summary>
    /// Uploads the frame's dots and sprites (once per frame, before the batches) and touches every pose's atlas, so the poses
    /// a swap is about to show decode ahead of it.
    /// </summary>
    public bool Upload(ID3D11DeviceContext context, OrbDrawList list)
    {
        var now = DateTime.UtcNow;
        foreach (var mesh in list.CharacterMeshes)
        {
            if (mesh.Pose is { } pose)
            {
                _ = Atlas(context, pose, mesh.AtlasStep, now);
            }
        }

        if (list.CharacterDotCount > 0)
        {
            if (_dots is null || _dotCapacity < list.CharacterDotCount)
            {
                _dots?.Dispose();
                _dotCapacity = Math.Max(256, OrbGpuRenderer.Grow(list.CharacterDotCount));
                _dots = _device.CreateBuffer((uint)(_dotCapacity * OrbCharacterDot.Stride), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            }

            if (!OrbGpuRenderer.Upload(context, _dots, MemoryMarshal.AsBytes(list.CharacterDots)))
            {
                return false;
            }
        }

        if (list.CharacterSpriteCount > 0)
        {
            if (_sprites is null || _spriteCapacity < list.CharacterSpriteCount)
            {
                _sprites?.Dispose();
                _spriteCapacity = Math.Max(64, OrbGpuRenderer.Grow(list.CharacterSpriteCount));
                _sprites = _device.CreateBuffer((uint)(_spriteCapacity * OrbCharacterSprite.Stride), BindFlags.VertexBuffer, ResourceUsage.Dynamic,
                    CpuAccessFlags.Write);
            }

            if (!OrbGpuRenderer.Upload(context, _sprites, MemoryMarshal.AsBytes(list.CharacterSprites)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Draws one character batch; false when the device failed.</summary>
    public bool Draw(ID3D11DeviceContext context, GpuDevice.Resources r, OrbDrawList list, in OrbBatch batch)
    {
        var now = DateTime.UtcNow;
        switch (batch.Pass)
        {
            case OrbPass.CharacterDots when _dots is not null && batch.Count > 0:
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.IASetInputLayout(_dotLayout);
                context.IASetVertexBuffer(0, _dots, OrbCharacterDot.Stride);
                context.VSSetShader(_dotVertex);
                context.PSSetShader(_dotPixel);
                context.OMSetBlendState(batch.Blend == OrbBlend.Additive ? r.Additive : r.Over);
                context.DrawInstanced(6, (uint)batch.Count, 0, (uint)batch.Start);
                return true;
            case OrbPass.CharacterSprites when _sprites is not null && batch.Count > 0 && batch.Mode < list.CharacterMeshCount:
            {
                var mesh = list.CharacterMeshes[batch.Mode];
                if (mesh.Pose is not { } pose || Atlas(context, pose, mesh.AtlasStep, now) is not { } view)
                {
                    return true;
                }

                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.IASetInputLayout(_spriteLayout);
                context.IASetVertexBuffer(0, _sprites, OrbCharacterSprite.Stride);
                context.VSSetShader(_spriteVertex);
                context.PSSetShader(_spritePixel);
                context.PSSetShaderResource(3, view);
                context.OMSetBlendState(r.Over);
                context.DrawInstanced(6, (uint)batch.Count, 0, (uint)batch.Start);
                return true;
            }

            case OrbPass.CharacterMesh when batch.Mode < list.CharacterMeshCount:
                return DrawMesh(context, r, list.CharacterMeshes[batch.Mode], now);
            default:
                return true;
        }
    }

    private bool DrawMesh(ID3D11DeviceContext context, GpuDevice.Resources r, OrbCharacterMeshDraw mesh, DateTime now)
    {
        if (mesh.Pose is not { } pose || mesh.BoneCount == 0 || Geometry(pose, now) is not { } geometry
            || Atlas(context, pose, mesh.AtlasStep, now) is not { } view)
        {
            return true;
        }

        var p = _meshScratch;
        p[0] = mesh.Placement.X;
        p[1] = mesh.Placement.Y;
        p[2] = mesh.Placement.Z;
        p[3] = mesh.Placement.W;
        p[4] = mesh.Feet.X;
        p[5] = mesh.Feet.Y;
        p[6] = 0;
        p[7] = 0;
        Array.Copy(mesh.Bones, 0, p, 8, mesh.BoneCount * 8);
        var opacity = 8 + 256 * 4;
        for (var i = 0; i < mesh.PartCount; i++)
        {
            p[opacity + 4 * i] = mesh.Opacities[i];
        }

        if (!OrbGpuRenderer.Upload(context, _meshParams, MemoryMarshal.AsBytes(p.AsSpan())))
        {
            return false;
        }

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_meshLayout);
        context.IASetVertexBuffer(0, geometry.Vertices, MeshVertex.Stride);
        context.IASetIndexBuffer(geometry.Indices, Format.R32_UInt, 0);
        context.VSSetShader(_meshVertex);
        context.PSSetShader(_meshPixel);
        context.VSSetConstantBuffer(4, _meshParams);
        context.PSSetConstantBuffer(4, _meshParams);
        context.PSSetShaderResource(3, view);
        context.OMSetBlendState(r.Over);
        context.DrawIndexed((uint)geometry.IndexCount, 0, 0);
        return true;
    }

    private PoseGpu? Geometry(CharacterPose pose, DateTime now)
    {
        if (_poses.TryGetValue(pose, out var cached))
        {
            cached.LastUsed = now;
            return cached;
        }

        var rig = pose.Rig;
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var w = (float)Math.Max(1, rig.AtlasWidth);
        var h = (float)Math.Max(1, rig.AtlasHeight);
        Span<ushort> bones = stackalloc ushort[4];
        for (var partIndex = 0; partIndex < rig.Parts.Length; partIndex++)
        {
            var part = rig.Parts[partIndex];
            var start = (uint)vertices.Count;
            var k = Math.Max(1, part.Influences);
            for (var v = 0; v < part.VertexCount; v++)
            {
                bones.Clear();
                var weights = Vector4.Zero;
                for (var i = 0; i < Math.Min(4, k); i++)
                {
                    bones[i] = part.Bones[v * k + i];
                    weights[i] = (float)part.Weights[v * k + i];
                }

                vertices.Add(new MeshVertex
                {
                    Position = new Vector2((float)part.Vertices[2 * v], (float)part.Vertices[2 * v + 1]),
                    Uv = new Vector2((float)part.Uv[2 * v] / w, (float)part.Uv[2 * v + 1] / h),
                    Weights = weights,
                    Bone0 = bones[0],
                    Bone1 = bones[1],
                    Bone2 = bones[2],
                    Bone3 = bones[3],
                    Part = (uint)partIndex,
                });
            }

            foreach (var t in part.Triangles)
            {
                indices.Add(start + t);
            }
        }

        if (indices.Count == 0)
        {
            return null;
        }

        var geometry = new PoseGpu(_device.CreateBuffer(vertices.ToArray(), BindFlags.VertexBuffer),
            _device.CreateBuffer(indices.ToArray(), BindFlags.IndexBuffer), indices.Count) { LastUsed = now };
        _poses[pose] = geometry;
        return geometry;
    }

    /// <summary>The pose's atlas at <paramref name="step"/>, or null while it decodes (the decode starts on the first call).</summary>
    private ID3D11ShaderResourceView? Atlas(ID3D11DeviceContext context, CharacterPose pose, int step, DateTime now)
    {
        AtlasEntry entry;
        lock (_gate)
        {
            var key = (pose.AtlasPath, step);
            if (!_atlases.TryGetValue(key, out entry!))
            {
                entry = new AtlasEntry { LastUsed = now };
                _atlases[key] = entry;
                var path = pose.AtlasPath;
                var factor = CharacterAtlasSteps.Factor(step);
                _ = Task.Run(() => Decode(entry, path, factor));
                return null;
            }

            entry.LastUsed = now;
            if (entry.View is not null || entry.Failed || entry.Pixels is null)
            {
                return entry.View;
            }
        }

        try
        {
            unsafe
            {
                var texture = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)entry.Width, (uint)entry.Height, 1, 0,
                    BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
                fixed (byte* data = entry.Pixels)
                {
                    context.UpdateSubresource(texture, 0, null, (IntPtr)data, (uint)(entry.Width * 4), 0);
                }

                var view = _device.CreateShaderResourceView(texture);
                context.GenerateMips(view);
                lock (_gate)
                {
                    entry.Texture = texture;
                    entry.View = view;
                    entry.Pixels = null;
                }

                return view;
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or ArgumentException)
        {
            AppLog.Warn("A character atlas could not be uploaded.", ex);
            lock (_gate)
            {
                entry.Failed = true;
                entry.Pixels = null;
            }

            return null;
        }
    }

    private void Decode(AtlasEntry entry, string path, double factor)
    {
        byte[]? pixels = null;
        int width = 0, height = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth is > 0 and <= MaxAtlasSide && frame.PixelHeight is > 0 and <= MaxAtlasSide)
            {
                BitmapSource source = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                if (factor < 0.999)
                {
                    source = new TransformedBitmap(source, new ScaleTransform(factor, factor));
                }

                width = source.PixelWidth;
                height = source.PixelHeight;
                pixels = new byte[width * height * 4];
                source.CopyPixels(pixels, width * 4, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            AppLog.Warn("A character atlas could not be decoded.", ex);
        }

        lock (_gate)
        {
            entry.Pixels = pixels;
            entry.Width = width;
            entry.Height = height;
            entry.Failed = pixels is null;
        }

        AssetReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Releases poses and atlases no frame asked for in the last two seconds (at most once a second).</summary>
    public void Sweep()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweep < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastSweep = now;
        foreach (var (pose, geometry) in _poses.Where(p => now - p.Value.LastUsed > Keep).ToArray())
        {
            geometry.Dispose();
            _poses.Remove(pose);
        }

        lock (_gate)
        {
            foreach (var (key, entry) in _atlases.Where(a => now - a.Value.LastUsed > Keep && (a.Value.View is not null || a.Value.Failed)).ToArray())
            {
                entry.View?.Dispose();
                entry.Texture?.Dispose();
                _atlases.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        foreach (var geometry in _poses.Values)
        {
            geometry.Dispose();
        }

        _poses.Clear();
        lock (_gate)
        {
            foreach (var entry in _atlases.Values)
            {
                entry.View?.Dispose();
                entry.Texture?.Dispose();
            }

            _atlases.Clear();
        }

        _dots?.Dispose();
        _sprites?.Dispose();
        _meshParams.Dispose();
        _meshLayout.Dispose();
        _dotLayout.Dispose();
        _spriteLayout.Dispose();
        _meshVertex.Dispose();
        _meshPixel.Dispose();
        _dotVertex.Dispose();
        _dotPixel.Dispose();
        _spriteVertex.Dispose();
        _spritePixel.Dispose();
    }
}
