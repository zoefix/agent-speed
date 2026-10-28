using AgentSpeed.Linux.Platform;
using AgentSpeed.Linux.Rendering;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace AgentSpeed.Linux.UI;

internal sealed class HeroControl : Control
{
    private readonly LinuxApp _app;
    private readonly HeroStage _stage;
    private readonly RigPose _pose = new();
    private readonly object _gate = new();
    private readonly Dictionary<VehiclePart, SKPath> _paths = [];
    private TopLevel? _visibilityRoot;
    private bool _attached;
    private bool _rootVisible;
    private readonly SKFont _font = new() { Edging = SKFontEdging.Antialias };
    private readonly SKPaint _paint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 30) };
    private readonly SKTypeface _face = SKTypeface.FromFamilyName("Noto Sans CJK SC", SKFontStyle.Bold);
    public HeroControl(LinuxApp app)
    {
        _app = app; _stage = new(VehicleArt.Shared ?? VehicleArt.LoadEmbedded()); Width = Height = 170;
        foreach (var vehicle in _stage.Art.Vehicles)
        foreach (var part in vehicle.Parts)
        {
            var path = new SKPath();
            if (part.Path is { } data)
                foreach (var segment in data.Segments)
                {
                    var a = segment.P1; var b = segment.P2; var c = segment.P3;
                    switch (segment.Verb)
                    {
                        case PathVerb.Move: path.MoveTo((float)a.X, (float)a.Y); break;
                        case PathVerb.Line: path.LineTo((float)a.X, (float)a.Y); break;
                        case PathVerb.Quad: path.QuadTo((float)a.X, (float)a.Y, (float)b.X, (float)b.Y); break;
                        case PathVerb.Cubic: path.CubicTo((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, (float)c.X, (float)c.Y); break;
                        case PathVerb.Close: path.Close(); break;
                    }
                }
            if (part.Circle is { } circle) path.AddCircle((float)circle.X, (float)circle.Y, (float)circle.Radius);
            _paths[part] = path;
        }
        _timer.Tick += (_, _) => InvalidateVisual();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            AttachVisibilityRoot();
            UpdateTimer();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            DetachVisibilityRoot();
            UpdateTimer();
        };
    }
    internal bool AnimationTimerEnabled => _timer.IsEnabled;
    internal string AnimationTimerStatus =>
        $"enabled={_timer.IsEnabled}, attached={_attached}, rootPresent={_visibilityRoot is not null}, " +
        $"rootVisible={_rootVisible}, controlVisible={IsVisible}";

    private void UpdateTimer()
    {
        var shouldRun = _attached && IsVisible && _rootVisible;
        if (shouldRun && !_timer.IsEnabled) _timer.Start();
        else if (!shouldRun && _timer.IsEnabled) _timer.Stop();
    }

    private void AttachVisibilityRoot()
    {
        DetachVisibilityRoot();
        var root = TopLevel.GetTopLevel(this);
        if (root is null) return;
        _visibilityRoot = root;
        _rootVisible = root.IsVisible;
        root.PropertyChanged += OnRootPropertyChanged;
    }

    private void DetachVisibilityRoot()
    {
        if (_visibilityRoot is { } root) root.PropertyChanged -= OnRootPropertyChanged;
        _visibilityRoot = null;
        _rootVisible = false;
    }

    private void OnRootPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        _rootVisible = _visibilityRoot?.IsVisible == true;
        UpdateTimer();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        lock (_gate)
        {
            var s = _app.Settings.Current; var value = _app.Speed.Snapshot.Live.ValueFor(s.Metric);
            _stage.Update(_app.Clock.Elapsed.TotalSeconds, value, s.Redline, HeatMath.RawTier(value, s.Redline), SpeedTitles.Raw(value, s.Redline),
                new(s.Effects, _app.Accessibility.Current.ReduceMotion, _app.Accessibility.Current.HighContrast,
                    _app.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light));
        }
        context.Custom(new HeroDraw(this, new Rect(Bounds.Size)));
    }
    private void Draw(SKCanvas canvas)
    {
        canvas.Save(); canvas.Scale((float)(Bounds.Width / 100));
        var paint = _stage.Paint();
        _paint.Color = SkiaOrbRenderer.Color(AgentSpeed.Presentation.Orb.Gpu.OrbGpuInstance.ToVector(paint.Color), .25f);
        _paint.Style = SKPaintStyle.Stroke; _paint.StrokeWidth = .7f; canvas.DrawCircle(50, 50, 47, _paint);
        if (_app.Settings.Current.TitleThemes.General.Base != AgentSpeed.Presentation.Titles.TitlePreset.Vehicles)
        {
            _font.Typeface = _face; _font.Size = 9; _paint.Style = SKPaintStyle.Fill;
            _paint.Color = SkiaOrbRenderer.Color(AgentSpeed.Presentation.Orb.Gpu.OrbGpuInstance.ToVector(paint.Color));
            var text = _app.Settings.Current.TitleThemes.General.Label(_stage.Title, _app.Loc);
            canvas.DrawText(text, 50 - _font.MeasureText(text) / 2, 52, SKTextAlign.Left, _font, _paint);
            for (var i = 1; i <= 13; i++)
            {
                var angle = (i - 1) / 13d * Math.Tau - Math.PI / 2;
                _paint.Color = _paint.Color.WithAlpha((byte)(i <= (int)_stage.Title ? 255 : 45));
                canvas.DrawCircle(50 + (float)Math.Cos(angle) * 39, 50 + (float)Math.Sin(angle) * 39, 1.4f, _paint);
            }
        }
        else
        {
            if (_stage.Previous is { } old) Actor(canvas, old, true, paint);
            Actor(canvas, _stage.Current, false, paint);
        }
        canvas.Restore();
    }
    private void Actor(SKCanvas canvas, HeroActor actor, bool outgoing, HeroPaint paint)
    {
        var vehicle = _stage.Art.Vehicles[actor.Vehicle]; var placement = _stage.Place(actor, outgoing); var fx = _stage.Startup(actor, outgoing);
        var motion = _stage.Motion(vehicle, placement, fx); var rigged = _stage.PoseRig(vehicle, fx, _pose);
        if (rigged) for (var i = 0; i < _pose.FarCount; i++) Limb(canvas, _pose[i], motion, paint, placement.Alpha * .42);
        foreach (var part in vehicle.Parts)
        {
            var role = _stage.Art.Role(part.Role); canvas.Save();
            canvas.Translate((float)motion.OffsetX, (float)(part.Sprung ? motion.SprungY : motion.OffsetY));
            if (part.Sprung) canvas.RotateDegrees((float)motion.PitchDegrees, (float)motion.Pivot.X, (float)motion.Pivot.Y);
            if (part.Spin is { } spin) canvas.RotateRadians((float)_stage.SpinAngle(vehicle, spin), (float)spin.Pivot.X, (float)spin.Pivot.Y);
            if (rigged && part.Role == PartRole.Rider) canvas.Translate((float)_pose.RiderOffset.X, (float)_pose.RiderOffset.Y);
            _paint.Color = SkiaOrbRenderer.Color(AgentSpeed.Presentation.Orb.Gpu.OrbGpuInstance.ToVector(role.Hot ? paint.Hot : paint.Color), (float)(role.Opacity * placement.Alpha * paint.Presence));
            _paint.StrokeWidth = (float)(_stage.Art.StrokeWidth * role.Weight);
            _paint.Style = part.Role is PartRole.Flame or PartRole.FlameCore ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
            canvas.DrawPath(_paths[part], _paint); canvas.Restore();
        }
        if (rigged)
        {
            for (var i = _pose.FarCount; i < _pose.Count; i++) Limb(canvas, _pose[i], motion, paint, placement.Alpha);
            if (_pose.HasHead)
            { _paint.Style = SKPaintStyle.Stroke; canvas.DrawCircle((float)(_pose.Head.X + motion.OffsetX), (float)(_pose.Head.Y + motion.OffsetY), (float)_pose.Head.Radius, _paint); }
        }
    }
    private void Limb(SKCanvas c, Limb limb, ActorMotion motion, HeroPaint paint, double alpha)
    {
        using var path = new SKPath();
        for (var i = 0; i < limb.Count; i++)
        { var p = limb[i]; if (i == 0) path.MoveTo((float)(p.X + motion.OffsetX), (float)(p.Y + motion.OffsetY)); else path.LineTo((float)(p.X + motion.OffsetX), (float)(p.Y + motion.OffsetY)); }
        _paint.Color = SkiaOrbRenderer.Color(AgentSpeed.Presentation.Orb.Gpu.OrbGpuInstance.ToVector(paint.Color), (float)alpha);
        _paint.Style = SKPaintStyle.Stroke; _paint.StrokeWidth = 1.5f; c.DrawPath(path, _paint);
    }
    private sealed class HeroDraw(HeroControl owner, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        { if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is { } feature) { using var lease = feature.Lease(); lock (owner._gate) owner.Draw(lease.SkCanvas); } }
    }
}
