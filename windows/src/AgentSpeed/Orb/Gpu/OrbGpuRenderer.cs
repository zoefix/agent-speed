using System.Numerics;
using System.Runtime.InteropServices;
using AgentSpeed.Presentation.Orb.Gpu;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// Encodes an <see cref="OrbDrawList"/> into a render target with the shared device: instance buffers mapped once per frame,
/// one instanced draw per batch, the liquid and led passes, then every text quad in one draw, then the batches above the text.
/// Allocates nothing on a warm frame.
/// </summary>
internal static class OrbGpuRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public Vector4 Viewport;
        public Vector4 Transform;
        public Vector4 Globals;
    }

    /// <summary>GPU time per frame when enabled (benchmarks), read back a few frames later without stalling.</summary>
    public static GpuTimer? Timer { get; set; }

    /// <summary>Draws <paramref name="list"/> into <paramref name="target"/> (pixels); false when the device was lost.</summary>
    public static bool Draw(GpuDevice device, OrbDrawList list, ID3D11RenderTargetView target, int pixelWidth, int pixelHeight)
    {
        var r = device.Current;
        var context = r.Context;
        if (!EnsureBuffers(r, list.Count, list.TextCount))
        {
            return false;
        }

        if (list.Count > 0 && !Upload(context, r.Instances!, MemoryMarshal.AsBytes(list.Instances)))
        {
            return false;
        }

        if (list.TextCount > 0 && !Upload(context, r.Texts!, MemoryMarshal.AsBytes(list.Texts)))
        {
            return false;
        }

        if (list.HasLiquid && !Upload(context, r.LiquidParams, MemoryMarshal.AsBytes(new ReadOnlySpan<OrbLiquidUniforms>(in list.Liquid))))
        {
            return false;
        }

        if (list.HasLed && !Upload(context, r.LedParams, MemoryMarshal.AsBytes(new ReadOnlySpan<OrbLedUniforms>(in list.Led))))
        {
            return false;
        }

        if ((list.CharacterDotCount > 0 || list.CharacterSpriteCount > 0 || list.CharacterMeshCount > 0) && r.Character is { } character
            && !character.Upload(context, list))
        {
            return false;
        }

        var atlas = device.Atlas.Upload(r.Device, context);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(0, 0, pixelWidth, pixelHeight);
        context.RSSetState(r.Rasterizer);
        context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));

        // After the clear: the wait for the compositor to release the back buffer is not the orb's GPU time.
        Timer?.Begin(r);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.PSSetSampler(0, r.Sampler);
        context.PSSetShaderResource(0, r.Palette);
        context.PSSetShaderResource(1, atlas);
        context.VSSetConstantBuffer(0, r.Uniforms);
        context.PSSetConstantBuffer(0, r.Uniforms);
        context.VSSetConstantBuffer(1, r.LiquidParams);
        context.PSSetConstantBuffer(1, r.LiquidParams);
        context.VSSetConstantBuffer(2, r.LiquidPass);
        context.PSSetConstantBuffer(2, r.LiquidPass);
        context.VSSetConstantBuffer(3, r.LedParams);
        context.PSSetConstantBuffer(3, r.LedParams);

        var canvas = (float)list.Canvas;
        var scale = canvas > 0 ? pixelWidth / canvas : 1;
        var uniforms = new Uniforms
        {
            Viewport = new Vector4(canvas, canvas, scale, 0),
            Transform = new Vector4((float)list.ContentScale, (float)list.CenterX, (float)list.CenterY, 0),
            Globals = new Vector4((float)list.Opacity, 0, 0, 0),
        };
        var written = false;
        var layout = 0;
        var batches = list.Batches;
        var overlayAt = list.OverlayBatchStart >= 0 ? list.OverlayBatchStart : batches.Length;
        for (var index = 0; index <= batches.Length; index++)
        {
            if (index == overlayAt && !DrawOverlayText(context, r, list, ref uniforms, ref written, ref layout))
            {
                return false;
            }

            if (index == batches.Length)
            {
                break;
            }

            ref readonly var batch = ref batches[index];
            if (!written || uniforms.Viewport.W != batch.Clip || uniforms.Transform.W != batch.Feather)
            {
                uniforms.Viewport.W = batch.Clip;
                uniforms.Transform.W = batch.Feather;
                if (!Upload(context, r.Uniforms, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms))))
                {
                    return false;
                }

                written = true;
            }

            if (batch.Pass is OrbPass.CharacterMesh or OrbPass.CharacterDots or OrbPass.CharacterSprites)
            {
                if (r.Character is { } characters && !characters.Draw(context, r, list, batch))
                {
                    return false;
                }

                layout = 0;
                continue;
            }

            if (batch.Pass is OrbPass.Liquid or OrbPass.Led)
            {
                Span<int> mode = [batch.Mode, 0, 0, 0];
                if (!Upload(context, r.LiquidPass, MemoryMarshal.AsBytes(mode)))
                {
                    return false;
                }

                var led = batch.Pass == OrbPass.Led;
                context.IASetInputLayout(null);
                context.VSSetShader(led ? r.LedVertex : r.LiquidVertex);
                context.PSSetShader(led ? r.LedPixel : r.LiquidPixel);
                context.OMSetBlendState(batch.Blend == OrbBlend.Additive ? r.Additive : r.Over);
                context.Draw(led ? (uint)OrbLedUniforms.VertexCount(batch.Mode) : 6, 0);
                layout = 0;
                continue;
            }

            if (batch.Count <= 0)
            {
                continue;
            }

            if (batch.Pass == OrbPass.Text)
            {
                if (layout != 2)
                {
                    context.IASetInputLayout(r.TextLayout);
                    context.IASetVertexBuffer(0, r.Texts!, OrbTextInstance.Stride);
                    context.VSSetShader(r.TextVertex);
                    context.PSSetShader(r.TextPixel);
                    layout = 2;
                }

                context.OMSetBlendState(r.Over);
                context.DrawInstanced(6, (uint)batch.Count, 0, (uint)batch.Start);
                continue;
            }

            if (layout != 1)
            {
                context.IASetInputLayout(r.ShapeLayout);
                context.IASetVertexBuffer(0, r.Instances!, OrbGpuInstance.Stride);
                context.VSSetShader(r.ShapeVertex);
                context.PSSetShader(r.ShapePixel);
                layout = 1;
            }

            context.OMSetBlendState(batch.Blend == OrbBlend.Additive ? r.Additive : r.Over);
            context.DrawInstanced(6, (uint)batch.Count, 0, (uint)batch.Start);
        }

        Timer?.End(r);
        r.Character?.Sweep();
        return true;
    }

    /// <summary>Every text quad of the overlay in one draw (the readout above every effect added before it).</summary>
    private static bool DrawOverlayText(ID3D11DeviceContext context, GpuDevice.Resources r, OrbDrawList list, ref Uniforms uniforms, ref bool written,
        ref int layout)
    {
        if (list.TextCount <= list.OverlayTextStart)
        {
            return true;
        }

        if (!written || uniforms.Viewport.W != 0 || uniforms.Transform.W != 0)
        {
            uniforms.Viewport.W = 0;
            uniforms.Transform.W = 0;
            if (!Upload(context, r.Uniforms, MemoryMarshal.AsBytes(new ReadOnlySpan<Uniforms>(in uniforms))))
            {
                return false;
            }

            written = true;
        }

        context.IASetInputLayout(r.TextLayout);
        context.IASetVertexBuffer(0, r.Texts!, OrbTextInstance.Stride);
        context.VSSetShader(r.TextVertex);
        context.PSSetShader(r.TextPixel);
        context.OMSetBlendState(r.Over);
        context.DrawInstanced(6, (uint)(list.TextCount - list.OverlayTextStart), 0, (uint)list.OverlayTextStart);
        layout = 2;
        return true;
    }

    private static bool EnsureBuffers(GpuDevice.Resources r, int instances, int texts)
    {
        if (r.Instances is null || r.InstanceCapacity < instances)
        {
            r.Instances?.Dispose();
            r.InstanceCapacity = Math.Max(1024, Grow(instances));
            r.Instances = r.Device.CreateBuffer((uint)(r.InstanceCapacity * OrbGpuInstance.Stride), BindFlags.VertexBuffer, ResourceUsage.Dynamic,
                CpuAccessFlags.Write);
        }

        if (r.Texts is null || r.TextCapacity < texts)
        {
            r.Texts?.Dispose();
            r.TextCapacity = Math.Max(64, Grow(texts));
            r.Texts = r.Device.CreateBuffer((uint)(r.TextCapacity * OrbTextInstance.Stride), BindFlags.VertexBuffer, ResourceUsage.Dynamic,
                CpuAccessFlags.Write);
        }

        return true;
    }

    internal static int Grow(int count)
    {
        var capacity = 1;
        while (capacity < count)
        {
            capacity <<= 1;
        }

        return capacity;
    }

    internal static unsafe bool Upload(ID3D11DeviceContext context, ID3D11Buffer buffer, ReadOnlySpan<byte> bytes)
    {
        var result = context.Map(buffer, 0, MapMode.WriteDiscard, MapFlags.None, out var mapped);
        if (result.Failure)
        {
            return false;
        }

        bytes.CopyTo(new Span<byte>((void*)mapped.DataPointer, bytes.Length));
        context.Unmap(buffer, 0);
        return true;
    }
}

/// <summary>Measures GPU time per orb frame with timestamp queries (DESIGN 2a, Budgets: ≤ 1 ms at S = 240, 200 %).</summary>
internal sealed class GpuTimer : IDisposable
{
    private const int Depth = 6;
    private readonly ID3D11Query[] _disjoint = new ID3D11Query[Depth];
    private readonly ID3D11Query[] _start = new ID3D11Query[Depth];
    private readonly ID3D11Query[] _end = new ID3D11Query[Depth];
    private readonly bool[] _pending = new bool[Depth];
    private int _index;
    private ID3D11Device? _device;
    private double _total;
    private int _frames;

    public void Begin(GpuDevice.Resources r)
    {
        if (!ReferenceEquals(_device, r.Device))
        {
            Create(r.Device);
        }

        Collect(r.Context);
        if (_pending[_index])
        {
            return;
        }

        r.Context.Begin(_disjoint[_index]);
        r.Context.End(_start[_index]);
    }

    public void End(GpuDevice.Resources r)
    {
        if (_pending[_index])
        {
            return;
        }

        r.Context.End(_end[_index]);
        r.Context.End(_disjoint[_index]);
        _pending[_index] = true;
        _index = (_index + 1) % Depth;
    }

    /// <summary>Average milliseconds per frame and frames since the last call.</summary>
    public (double Milliseconds, int Frames) Drain()
    {
        var result = (_frames > 0 ? _total / _frames * 1000 : 0, _frames);
        _total = 0;
        _frames = 0;
        return result;
    }

    private void Collect(ID3D11DeviceContext context)
    {
        for (var i = 0; i < Depth; i++)
        {
            if (!_pending[i] || !context.IsDataAvailable(_disjoint[i], AsyncGetDataFlags.DoNotFlush))
            {
                continue;
            }

            _pending[i] = false;
            if (context.GetData(_disjoint[i], AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint) && !disjoint.Disjoint
                && context.GetData(_start[i], AsyncGetDataFlags.DoNotFlush, out ulong start)
                && context.GetData(_end[i], AsyncGetDataFlags.DoNotFlush, out ulong end) && end > start && disjoint.Frequency > 0)
            {
                _total += (end - start) / (double)disjoint.Frequency;
                _frames++;
            }
        }
    }

    private void Create(ID3D11Device device)
    {
        DisposeQueries();
        _device = device;
        for (var i = 0; i < Depth; i++)
        {
            _disjoint[i] = device.CreateQuery(QueryType.TimestampDisjoint);
            _start[i] = device.CreateQuery(QueryType.Timestamp);
            _end[i] = device.CreateQuery(QueryType.Timestamp);
            _pending[i] = false;
        }
    }

    private void DisposeQueries()
    {
        for (var i = 0; i < Depth; i++)
        {
            _disjoint[i]?.Dispose();
            _start[i]?.Dispose();
            _end[i]?.Dispose();
        }
    }

    public void Dispose() => DisposeQueries();
}
