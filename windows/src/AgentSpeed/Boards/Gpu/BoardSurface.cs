using System.Windows.Media.Imaging;
using AgentSpeed.App;
using AgentSpeed.Boards.Layers;
using AgentSpeed.Orb.Gpu;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Boards.Gpu;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Boards.Gpu;

/// <summary>
/// One board's picture on the GPU (DESIGN 2c, Frame policy): the platform-neutral <see cref="BoardScene"/> builds each frame
/// on the orbs' shared clock, its layers are rasterized only after a tick changed them, and the frame is encoded with
/// Direct3D 11 into the window's composition swap chain — only when it differs from the one on screen. Static when idle;
/// no frames while hidden, cloaked, faded out, locked or with the display off.
/// </summary>
internal sealed class BoardSurface : IOrbFrameClient, IDisposable
{
    private readonly GpuDevice _device;
    private readonly OrbClock _clock;
    private readonly BoardFrame _frame;
    private CompositionSurface? _surface;
    private double _scale = 1;
    private int _pixels = 1;
    private bool _visible;
    private bool _paused;
    private bool _locked;
    private bool _forcePresent = true;
    private ulong _presented;
    private int _presentedGeneration = -1;
    private FrameClass _demand = FrameClass.None;
    private double _opacity = 1;

    public BoardSurface(GpuDevice device, OrbClock clock, BoardStyle style, BoardSize size, IBoardLayerPainter painter, uint seed = 1)
    {
        _device = device;
        _clock = clock;
        _frame = new BoardFrame(style, size, painter, seed);
        device.Recovered += OnRecovered;
    }

    /// <summary>Frames presented by every board (the bench).</summary>
    public static long FramesPresented { get; private set; }

    /// <summary>Frames built but not presented because nothing changed (the bench).</summary>
    public static long FramesSkipped { get; private set; }

    /// <summary>Frames the compositor was not ready for (retried on the next tick).</summary>
    public static long FramesBusy { get; private set; }

    /// <summary>Frames rendered per class since start (the bench).</summary>
    public static long[] FramesByClass { get; } = new long[Enum.GetValues<FrameClass>().Length];

    public BoardScene Scene => _frame.Scene;

    public BoardFrame Frame => _frame;

    /// <summary>The window handle (the swap chain's composition target).</summary>
    public nint Hwnd { get; set; }

    public int Pixels => _pixels;

    public double Scale => _scale;

    /// <summary>The window's opacity (settings × idle fade); 0 renders nothing.</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (Math.Abs(_opacity - value) > 0.0005)
            {
                _opacity = value;
                _clock.Kick(this);
            }
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            if (value)
            {
                Scene.Settle();
                _forcePresent = true;
                _clock.Register(this);
            }
            else
            {
                _clock.Unregister(this);
            }
        }
    }

    /// <summary>Cloaked (another virtual desktop) or otherwise not shown.</summary>
    public bool Paused
    {
        get => _paused;
        set => SetResumed(ref _paused, value);
    }

    /// <summary>Session locked or the console display off.</summary>
    public bool Locked
    {
        get => _locked;
        set => SetResumed(ref _locked, value);
    }

    public bool IsActive => _visible && !_paused && !_locked && !CompositorTicker.DisplayOff && Hwnd != 0 && _opacity > 0.001;

    public bool IsPreferred { get; set; }

    public FrameClass Demand => _demand;

    public double? NextWake => null;

    public FrameClass Cap { get; set; } = FrameClass.Fps60;

    public void Configure(BoardStyle style, BoardSize size, BoardEffectsPolicy policy)
    {
        Scene.Configure(style, size, policy);
        if (policy.ReduceMotion)
        {
            Scene.Settle();
        }

        _frame.Invalidate();
        _forcePresent = true;
        _clock.Kick(this);
    }

    /// <summary>Window side in physical pixels and the display scale it is drawn at.</summary>
    public void SetPixels(int pixels, double scale)
    {
        pixels = Math.Max(1, pixels);
        if (pixels == _pixels && Math.Abs(scale - _scale) < 0.001)
        {
            return;
        }

        _pixels = pixels;
        _scale = scale;
        _frame.Invalidate();
        _forcePresent = true;
        _clock.Kick(this);
    }

    /// <summary>An engine tick: rows move, values change, events fire effects.</summary>
    public void Apply(BoardTick tick)
    {
        Scene.Apply(tick, _clock.Now);
        _frame.Invalidate();
        _clock.Kick(this);
    }

    public void SetHovered(string? key)
    {
        if (!string.Equals(Scene.Hovered, key, StringComparison.Ordinal))
        {
            Scene.Hovered = key;
            _clock.Kick(this);
        }
    }

    public void Kick() => _clock.Kick(this);

    public void Render(double now)
    {
        if (Hwnd == 0)
        {
            return;
        }

        if (!Build(now))
        {
            return;
        }

        FramesByClass[(int)_demand]++;
        var fingerprint = _frame.Fingerprint(_pixels);
        if (_presentedGeneration != _device.Generation)
        {
            _forcePresent = true;
        }

        if (fingerprint == _presented && !_forcePresent)
        {
            FramesSkipped++;
            return;
        }

        if (!Draw())
        {
            return;
        }

        _presented = fingerprint;
        _presentedGeneration = _device.Generation;
        _forcePresent = false;
        FramesPresented++;
    }

    /// <summary>The board as it would be drawn at <paramref name="now"/>, read back (the frame harness and developer captures).</summary>
    public BitmapSource? Capture(double now, OffscreenTarget target)
    {
        if (!Build(now))
        {
            return null;
        }

        var pixels = target.Render(_device, view => _frame.Draw(_device, view, _pixels), _pixels, _pixels);
        return pixels is null ? null : OffscreenTarget.ToBitmap(pixels, _pixels, _pixels);
    }

    public void Dispose()
    {
        _device.Recovered -= OnRecovered;
        _clock.Unregister(this);
        _surface?.Dispose();
        _surface = null;
        _frame.Dispose();
    }

    /// <summary>Builds the frame's draw list; false when the device is gone.</summary>
    private bool Build(double now)
    {
        if (_device.IsLost && !Recover())
        {
            return false;
        }

        if (!_frame.Build(_device, now, _scale, _opacity))
        {
            // A lost device is rebuilt; otherwise the layer texture is recreated on the next frame.
            if (_device.IsLost)
            {
                Recover();
            }
            else
            {
                _frame.Invalidate();
                _clock.Kick(this);
            }

            return false;
        }

        _demand = Scene.Demand(now);
        return true;
    }

    private bool Draw()
    {
        _surface ??= new CompositionSurface(Hwnd);
        try
        {
            if (!_device.CanSubmit)
            {
                FrameStats.AddSkip();
                return Skip();
            }

            var target = _surface.Begin(_device, _pixels, _pixels);
            if (target is not null && _frame.Draw(_device, target, _pixels))
            {
                switch (_surface.Present())
                {
                    case PresentResult.Shown:
                        return true;
                    case PresentResult.Busy:
                        return Skip();
                }
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            AppLog.Warn("A board frame failed on the GPU.", ex);
        }

        if (_device.IsLost)
        {
            Recover();
        }

        return false;
    }

    /// <summary>The compositor has not taken the previous frame yet, or the GPU is still busy: the next frame presents instead.</summary>
    private bool Skip()
    {
        FramesBusy++;
        _forcePresent = true;
        if (_demand == FrameClass.None)
        {
            _clock.Kick(this);
        }

        return false;
    }

    private bool Recover()
    {
        if (_device.Recover())
        {
            _frame.Invalidate();
            _forcePresent = true;
            _clock.Kick(this);
        }

        return false;
    }

    private void OnRecovered(object? sender, EventArgs e)
    {
        _frame.Invalidate();
        _forcePresent = true;
        _clock.Kick(this);
    }

    private void SetResumed(ref bool flag, bool value)
    {
        if (flag == value)
        {
            return;
        }

        flag = value;
        if (IsActive)
        {
            // Time passed while nothing was drawn: rows and effects resume at rest.
            Scene.Settle();
            _forcePresent = true;
            _clock.Kick(this);
        }
        else
        {
            _clock.Reschedule();
        }
    }
}
