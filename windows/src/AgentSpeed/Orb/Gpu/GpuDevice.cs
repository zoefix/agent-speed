using AgentSpeed.App;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Gpu;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// The one Direct3D 11 device every floating orb shares (DESIGN 2a, Techniques): shaders, pipeline states, the heat palette,
/// the glyph atlas, the dynamic instance buffers and the DirectComposition device. Created on the UI thread and used only
/// there. When the device is lost (driver update, GPU reset, sleep) it is rebuilt and <see cref="Generation"/> increments
/// so every orb recreates its swap chain.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private static readonly FeatureLevel[] Levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
    private static OrbShaderCode? _code;
    private static bool s_composition = true;

    private Resources? _resources;
    private bool _disposed;

    private readonly AutoResetEvent _removed = new(false);
    private RegisteredWaitHandle? _removedWait;
    private System.Windows.Threading.Dispatcher? _dispatcher;

    private GpuDevice(Resources resources)
    {
        _resources = resources;
        Atlas = new GlyphAtlas();
    }

    /// <summary>Raised on the UI thread after the device was rebuilt: every orb presents its current frame again.</summary>
    public event EventHandler? Recovered;

    /// <summary>
    /// Watches for a device removal while every orb is static (driver update, GPU reset, resume from sleep), so the orbs are
    /// redrawn without waiting for the next change.
    /// </summary>
    private void WatchRemoval()
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        if (_resources?.Device.QueryInterfaceOrNull<ID3D11Device4>() is not { } device4)
        {
            return;
        }

        using (device4)
        {
            device4.RegisterDeviceRemovedEvent(_removed);
        }

        _removedWait = ThreadPool.RegisterWaitForSingleObject(_removed, (_, _) => _dispatcher?.BeginInvoke(() =>
        {
            if (!_disposed && IsLost)
            {
                Recover();
            }
        }), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>The app's device, or null when Direct3D is unavailable (the orbs then use the WPF renderer).</summary>
    public static GpuDevice? Shared { get; private set; }

    /// <summary>Why the device could not be created (for the log), or null.</summary>
    public static string? Failure { get; private set; }

    /// <summary>Incremented whenever the device is recreated.</summary>
    public int Generation { get; private set; }

    public GlyphAtlas Atlas { get; }

    public string AdapterName => _resources?.AdapterName ?? string.Empty;

    internal Resources Current => _resources ?? throw new InvalidOperationException("The orb device is gone.");

    /// <summary>Creates the shared device once (on the UI thread); false when Direct3D 11 cannot be used.</summary>
    /// <param name="composition">Also create the DirectComposition device (windows); the offscreen harness runs without a desktop.</param>
    public static bool Initialize(bool composition = true)
    {
        s_composition = composition;
        if (Shared is not null)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_RENDERER") is "wpf")
        {
            Failure = "AGENT_SPEED_ORB_RENDERER=wpf";
            return false;
        }

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var resources = Resources.Create();
            Shared = new GpuDevice(resources);
            Shared.WatchRemoval();
            AppLog.Info($"Orb renderer: Direct3D {resources.Level} on {resources.AdapterName} ({watch.ElapsedMilliseconds} ms).");
            SimulateLosses();
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
            or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            Failure = ex.Message;
            AppLog.Warn("Direct3D 11 is not available; the orbs use the WPF renderer.", ex);
            return false;
        }
    }

    /// <summary>
    /// Developer check of the recovery path: <c>AGENT_SPEED_ORB_SIMULATE_LOSS=SECONDS</c> drops and rebuilds every device
    /// resource on that period, as a device removal would (swap chains, atlas and buffers are recreated on the next frame).
    /// </summary>
    private static void SimulateLosses()
    {
        if (!double.TryParse(Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_SIMULATE_LOSS"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds < 1)
        {
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            AppLog.Info("AGENT_SPEED_ORB_SIMULATE_LOSS: rebuilding the orb's Direct3D device.");
            if (Shared?.Recover() != true)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// False while the GPU still runs the previous tick's frames (a busy GPU): the frames of this tick are skipped instead of
    /// queued behind them, so Present never waits for the GPU on the UI thread.
    /// </summary>
    public bool CanSubmit { get; private set; } = true;

    private bool _tickPending;
    private bool _submitted;

    /// <summary>Before a tick's frames.</summary>
    public void BeginTick()
    {
        _submitted = false;
        // A removed device may never finish the query: submit so the failed present recovers it.
        CanSubmit = !_tickPending || _resources is not { } r || r.Context.IsDataAvailable(r.TickDone, AsyncGetDataFlags.DoNotFlush)
            || IsLost;
        if (CanSubmit)
        {
            _tickPending = false;
        }
    }

    /// <summary>A frame of this tick goes to the GPU: the end of the tick waits for it.</summary>
    public void NoteSubmit() => _submitted = true;

    /// <summary>After a tick's frames: marks the end of their GPU work.</summary>
    public void EndTick()
    {
        // A tick that drew nothing has nothing to wait for. Its query would stay unsignalled until the driver flushes on its
        // own (up to 2 s on an idle GPU), holding back the first frame after a quiet spell (a board's idle fade).
        if (_tickPending || !CanSubmit || !_submitted || _resources is not { } r)
        {
            return;
        }

        r.Context.End(r.TickDone);
        r.Context.Flush();
        _tickPending = true;
    }

    /// <summary>True when the device was removed or reset (checked after a failed present or map).</summary>
    public bool IsLost => _resources is null || _resources.Device.DeviceRemovedReason.Failure;

    /// <summary>Rebuilds every device resource after a loss; false when it failed (the caller falls back to WPF).</summary>
    public bool Recover()
    {
        if (_disposed)
        {
            return false;
        }

        AppLog.Warn($"The orb's Direct3D device was lost ({_resources?.Device.DeviceRemovedReason}); recreating it.");
        _resources?.Dispose();
        _resources = null;
        _tickPending = false;
        try
        {
            _resources = Resources.Create();
            Atlas.ResetDevice();
            Generation++;
            if (_resources.Device.QueryInterfaceOrNull<ID3D11Device4>() is { } device4)
            {
                using (device4)
                {
                    device4.RegisterDeviceRemovedEvent(_removed);
                }
            }

            Recovered?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("Could not recreate the orb's Direct3D device.", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _removedWait?.Unregister(null);
        Atlas.ResetDevice();
        _resources?.Dispose();
        _resources = null;
        if (ReferenceEquals(Shared, this))
        {
            Shared = null;
        }
    }

    /// <summary>Everything that belongs to one device instance.</summary>
    internal sealed class Resources : IDisposable
    {
        public const int PaletteSize = 256;

        private Resources(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory2 factory, IDCompositionDevice? composition, string adapterName,
            FeatureLevel level)
        {
            Device = device;
            Context = context;
            Factory = factory;
            _composition = composition;
            AdapterName = adapterName;
            Level = level;
        }

        public ID3D11Device Device { get; }

        public ID3D11DeviceContext Context { get; }

        public IDXGIFactory2 Factory { get; }

        public IDCompositionDevice Composition => _composition ?? throw new InvalidOperationException("DirectComposition is not available.");

        private readonly IDCompositionDevice? _composition;

        public string AdapterName { get; }

        public FeatureLevel Level { get; }

        public ID3D11VertexShader ShapeVertex { get; private set; } = null!;

        public ID3D11PixelShader ShapePixel { get; private set; } = null!;

        public ID3D11VertexShader TextVertex { get; private set; } = null!;

        public ID3D11PixelShader TextPixel { get; private set; } = null!;

        public ID3D11VertexShader LiquidVertex { get; private set; } = null!;

        public ID3D11PixelShader LiquidPixel { get; private set; } = null!;

        public ID3D11VertexShader LedVertex { get; private set; } = null!;

        public ID3D11PixelShader LedPixel { get; private set; } = null!;

        public ID3D11VertexShader LayerVertex { get; private set; } = null!;

        public ID3D11PixelShader LayerPixel { get; private set; } = null!;

        public ID3D11InputLayout ShapeLayout { get; private set; } = null!;

        public ID3D11InputLayout TextLayout { get; private set; } = null!;

        /// <summary>The cached layers of the boards (DESIGN 2c).</summary>
        public ID3D11InputLayout LayerLayout { get; private set; } = null!;

        public ID3D11BlendState Over { get; private set; } = null!;

        public ID3D11BlendState Additive { get; private set; } = null!;

        public ID3D11SamplerState Sampler { get; private set; } = null!;

        public ID3D11RasterizerState Rasterizer { get; private set; } = null!;

        public ID3D11Buffer Uniforms { get; private set; } = null!;

        public ID3D11Buffer LiquidParams { get; private set; } = null!;

        public ID3D11Buffer LiquidPass { get; private set; } = null!;

        public ID3D11Buffer LedParams { get; private set; } = null!;

        public ID3D11ShaderResourceView Palette { get; private set; } = null!;

        /// <summary>Signalled once the GPU finished a tick's frames.</summary>
        public ID3D11Query TickDone { get; private set; } = null!;

        public ID3D11Buffer? Instances { get; set; }

        public int InstanceCapacity { get; set; }

        public ID3D11Buffer? Texts { get; set; }

        public int TextCapacity { get; set; }

        public ID3D11Buffer? Layers { get; set; }

        /// <summary>The character style's passes (DESIGN 2a.6); null when its shaders could not be made.</summary>
        public CharacterGpu? Character { get; private set; }

        public int LayerCapacity { get; set; }

        public static Resources Create()
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            FeatureLevel level = default;
            var flags = DeviceCreationFlags.BgraSupport;
            var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, Levels, out device, out level, out context);
            if (result.Failure || device is null || context is null)
            {
                throw new InvalidOperationException($"D3D11CreateDevice failed ({result}).");
            }

            try
            {
                // Frame latency is counted per device, not per swap chain: eight orbs present in the same tick, each a few
                // microseconds of GPU work, so allow that many presents in flight before Present reports busy.
                using var dxgiDevice = device.QueryInterface<IDXGIDevice1>();
                dxgiDevice.SetMaximumFrameLatency(16);
                using var adapter = dxgiDevice.GetAdapter();
                var description = adapter.Description;
                // The Microsoft Basic Render Driver (WARP in a remote or driverless session) renders on the CPU.
                if (description.VendorId == 0x1414 && description.DeviceId == 0x8C)
                {
                    throw new NotSupportedException("Only the software rasterizer is available.");
                }

                var composition = s_composition ? DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice) : null;
                var resources = new Resources(device, context, factory.QueryInterface<IDXGIFactory2>(), composition,
                    description.Description.Trim(), level);
                resources.Build();
                return resources;
            }
            catch
            {
                context.Dispose();
                device.Dispose();
                throw;
            }
        }

        private void Build()
        {
            var code = _code ??= ShaderLibrary.Load();
            ShapeVertex = Device.CreateVertexShader(code.ShapeVertex);
            ShapePixel = Device.CreatePixelShader(code.ShapePixel);
            TextVertex = Device.CreateVertexShader(code.TextVertex);
            TextPixel = Device.CreatePixelShader(code.TextPixel);
            LiquidVertex = Device.CreateVertexShader(code.LiquidVertex);
            LiquidPixel = Device.CreatePixelShader(code.LiquidPixel);
            LedVertex = Device.CreateVertexShader(code.LedVertex);
            LedPixel = Device.CreatePixelShader(code.LedPixel);
            ShapeLayout = Device.CreateInputLayout(InstanceElements(6), code.ShapeVertex);
            TextLayout = Device.CreateInputLayout(InstanceElements(9), code.TextVertex);
            LayerVertex = Device.CreateVertexShader(code.LayerVertex);
            LayerPixel = Device.CreatePixelShader(code.LayerPixel);
            LayerLayout = Device.CreateInputLayout(InstanceElements(5), code.LayerVertex);

            Over = Device.CreateBlendState(Blend(additive: false));
            Additive = Device.CreateBlendState(Blend(additive: true));
            Sampler = Device.CreateSamplerState(SamplerDescription.LinearClamp);
            Rasterizer = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = false });
            Uniforms = Device.CreateBuffer(48, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            LiquidParams = Device.CreateBuffer(OrbLiquidUniforms.Size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            LiquidPass = Device.CreateBuffer(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            LedParams = Device.CreateBuffer(OrbLedUniforms.Size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            Palette = CreatePalette();
            TickDone = Device.CreateQuery(QueryType.Event);
            try
            {
                Character = new CharacterGpu(Device, code);
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                AppLog.Warn("The character shaders could not be created.", ex);
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

        private static BlendDescription Blend(bool additive)
        {
            var description = new BlendDescription();
            description.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Vortice.Direct3D11.Blend.One,
                DestinationBlend = additive ? Vortice.Direct3D11.Blend.One : Vortice.Direct3D11.Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Vortice.Direct3D11.Blend.One,
                DestinationBlendAlpha = Vortice.Direct3D11.Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            return description;
        }

        /// <summary>The heat palette (DESIGN 1) as a 256 × 1 texture, for shapes that follow the spectrum.</summary>
        private ID3D11ShaderResourceView CreatePalette()
        {
            var pixels = new byte[PaletteSize * 4];
            for (var i = 0; i < PaletteSize; i++)
            {
                var color = HeatPalette.Evaluate(i / (double)(PaletteSize - 1));
                pixels[i * 4] = color.R8;
                pixels[i * 4 + 1] = color.G8;
                pixels[i * 4 + 2] = color.B8;
                pixels[i * 4 + 3] = 255;
            }

            using var texture = Device.CreateTexture2D(new ReadOnlySpan<byte>(pixels), Format.R8G8B8A8_UNorm, PaletteSize, 1);
            return Device.CreateShaderResourceView(texture);
        }

        public void Dispose()
        {
            Context.ClearState();
            Context.Flush();
            TickDone?.Dispose();
            Character?.Dispose();
            Instances?.Dispose();
            Texts?.Dispose();
            Layers?.Dispose();
            Palette?.Dispose();
            LedParams?.Dispose();
            LiquidPass?.Dispose();
            LiquidParams?.Dispose();
            Uniforms?.Dispose();
            Rasterizer?.Dispose();
            Sampler?.Dispose();
            Additive?.Dispose();
            Over?.Dispose();
            LayerLayout?.Dispose();
            TextLayout?.Dispose();
            ShapeLayout?.Dispose();
            LayerPixel?.Dispose();
            LayerVertex?.Dispose();
            LedPixel?.Dispose();
            LedVertex?.Dispose();
            LiquidPixel?.Dispose();
            LiquidVertex?.Dispose();
            TextPixel?.Dispose();
            TextVertex?.Dispose();
            ShapePixel?.Dispose();
            ShapeVertex?.Dispose();
            _composition?.Dispose();
            Factory.Dispose();
            Context.Dispose();
            Device.Dispose();
        }
    }
}
