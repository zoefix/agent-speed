using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace AgentSpeed.Orb.Gpu;

/// <summary>The outcome of a present.</summary>
internal enum PresentResult
{
    Shown,
    Busy,
    Lost,
}

/// <summary>
/// The pixels of one orb window: a flip-model swap chain for composition (premultiplied alpha) set as the content of a
/// DirectComposition visual on the window (<c>WS_EX_NOREDIRECTIONBITMAP</c>, so there is no redirection surface and no
/// layered-window readback). Rebuilt when the device generation changes.
/// </summary>
internal sealed class CompositionSurface : IDisposable
{
    /// <summary>
    /// Presents wait for the next vertical blank: immediate presents (0) made the compositor recompose on every present,
    /// ≈ 7 % more CPU in dwm.exe for one orb at the redline on the reference machine.
    /// </summary>
    private const uint SyncInterval = 1;

    private readonly nint _hwnd;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _view;
    private int _generation = -1;

    public CompositionSurface(nint hwnd)
    {
        _hwnd = hwnd;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The render target for a frame of <paramref name="width"/> × <paramref name="height"/> pixels, or null when the device is gone.</summary>
    public ID3D11RenderTargetView? Begin(GpuDevice device, int width, int height)
    {
        device.NoteSubmit();
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_generation != device.Generation || _swapChain is null)
        {
            Release();
            Create(device, width, height);
        }
        else if (width != Width || height != Height)
        {
            _view?.Dispose();
            _view = null;
            var result = _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            if (result.Failure)
            {
                return null;
            }

            Width = width;
            Height = height;
        }

        if (_view is null && _swapChain is not null)
        {
            using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _view = device.Current.Device.CreateRenderTargetView(buffer);
        }

        return _view;
    }

    /// <summary>
    /// Queues the frame for the next composition without ever blocking the UI thread (several orbs present in the same
    /// tick): <see cref="PresentResult.Busy"/> when the compositor still holds the queue, so the caller retries next tick.
    /// </summary>
    public PresentResult Present()
    {
        if (_swapChain is null)
        {
            return PresentResult.Lost;
        }

        var start = FrameStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var result = _swapChain.Present(SyncInterval, PresentFlags.DoNotWait);
        var busy = result.Code == Vortice.DXGI.ResultCode.WasStillDrawing.Code;
        if (start != 0)
        {
            FrameStats.AddPresent(System.Diagnostics.Stopwatch.GetTimestamp() - start, busy);
        }

        if (busy)
        {
            return PresentResult.Busy;
        }

        return result.Success ? PresentResult.Shown : PresentResult.Lost;
    }

    private void Create(GpuDevice device, int width, int height)
    {
        var r = device.Current;
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };
        try
        {
            _swapChain = r.Factory.CreateSwapChainForComposition(r.Device, description);
        }
        catch (SharpGenException)
        {
            description.SwapEffect = SwapEffect.FlipSequential;
            _swapChain = r.Factory.CreateSwapChainForComposition(r.Device, description);
        }

        r.Composition.CreateTargetForHwnd(_hwnd, true, out var target).CheckError();
        _target = target;
        _visual = r.Composition.CreateVisual();
        _visual.SetContent(_swapChain).CheckError();
        _target.SetRoot(_visual).CheckError();
        r.Composition.Commit().CheckError();
        Width = width;
        Height = height;
        _generation = device.Generation;
    }

    private void Release()
    {
        _view?.Dispose();
        _view = null;
        _visual?.Dispose();
        _visual = null;
        _target?.Dispose();
        _target = null;
        _swapChain?.Dispose();
        _swapChain = null;
    }

    public void Dispose() => Release();
}
