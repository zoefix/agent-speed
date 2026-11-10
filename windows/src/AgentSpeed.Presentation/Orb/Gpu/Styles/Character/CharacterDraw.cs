using System.Numerics;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

/// <summary>
/// The <c>character</c> style on the GPU (DESIGN 2a.6, CHARACTERS 3–8): the skinned character of the chosen pack with its
/// pose and character swaps, the ring of lights at her feet, the agent lights, her particles and the readout cards
/// floating beside her — the speed at her right with the running models under it, the facts in a column at her left,
/// the model above her head, always in front of her. The context's size is the footprint D (2 S). Without a character it draws the ring and the standard
/// readout with <c>character.empty</c>. Allocates nothing on a warm frame except when an action starts.
/// </summary>
public sealed partial class CharacterDraw : IOrbStyleDraw
{
    /// <summary>The character's height × D and her feet below the disc center × D.</summary>
    public const double Height = 0.86;
    public const double FeetDrop = 0.45;
    public const double RingRadius = 0.23;
    public const double RingSquash = 0.17;
    public const int RingLights = 96;

    private static readonly OrbBodyLook NoBody = new(null, null, Lens: false);
    private static ulong s_seeds = 7;

    private readonly CharacterModel _model;
    private readonly CharacterHud _hud = new(5, 1);
    private readonly CharacterParticles _particles = new();
    private readonly CharacterLightning _lightning = new();
    private readonly HudWings _wings = new();
    private readonly List<(double R, double G, double B)> _agents = new(8);
    private readonly (double W, double H)[] _cardSizes = new (double, double)[HudCards.Count];
    private readonly bool[] _cardShown = new bool[HudCards.Count];
    private readonly Dictionary<string, Vector4?> _petals = new(StringComparer.Ordinal);
    private HudSilhouette? _silhouette;
    private double[] _scratch = [];
    private OrbGpuContext? _ctx;
    private CharacterStage? _stage;
    private CharacterPack? _outgoingPack;
    private double _hudTime;
    private double _comet;
    private double _speedDrop;
    private double _frameTime;
    private string? _lastModel;
    private bool _loading;

    public CharacterDraw()
    {
        _model = new CharacterModel(this);
    }

    public OrbStyle Style => OrbStyle.Character;

    public IOrbStyleModel Model => _model;

    public OrbReadoutLayout Readout => OrbReadoutLayout.Character;

    public OrbBodyLook Body => NoBody;

    public bool SharedEffects => false;

    public OrbReadoutColors? BelowSurface => null;

    /// <summary>The character, the cards switched on and their texts; set by the orb before each frame.</summary>
    public OrbCharacterContent? Content { get; set; }

    /// <summary>The live values of the cards (quiet rules applied).</summary>
    public OrbCharacterReadings? Readings { get; set; }

    public IOrbGlyphs? Glyphs { get; set; }

    /// <summary>The readout's unit, peak and title texts.</summary>
    public OrbReadoutStrings? Strings { get; set; }

    /// <summary>A character is on stage: the orb draws <see cref="BuildScene"/> instead of the fallback.</summary>
    public bool CanDraw => _stage is not null;

    /// <summary>Her pack is loading: frames at 30 fps until it shows.</summary>
    public bool Loading => _loading;

    /// <summary>The cards' places this frame (accessibility, tests).</summary>
    public HudWings Wings => _wings;

    public CharacterStage? Stage => _stage;

    public void Configure(OrbGpuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
    }

    /// <summary>The fallback instrument (no character): the ring of lights at the feet (DESIGN 2a.6).</summary>
    public void Build(OrbDrawList list, OrbScene scene, in OrbFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scene);
        if (_ctx is { } ctx)
        {
            BuildRing(list, ctx, frame);
        }
    }

    private static CharacterPerformer NewPerformer(CharacterPack pack)
    {
        unchecked
        {
            s_seeds = s_seeds * 6364136223846793005UL + 1442695040888963407UL;
        }

        return new CharacterPerformer(pack, s_seeds >> 33, orb: 1);
    }

    /// <summary>Geometry of a performer on the canvas: the idle feet, her height, points per idle pixel, the reference unit, her side step.</summary>
    private readonly record struct Geometry(double FeetX, double FeetY, double Height, double PointsPerPixel, double Unit, double BodyDx, double D);

    private Geometry GeometryOf(OrbGpuContext ctx, CharacterPerformer performer)
    {
        var d = ctx.Size;
        var height = CharacterDraw.Height * d;
        var ppp = height / Math.Max(1, performer.IdleHeight);
        return new Geometry(ctx.CenterX, ctx.CenterY + FeetDrop * d, height, ppp, Math.Max(height / 972, 0.42), performer.BodyOffset.Dx * ppp, d);
    }

    /// <summary>Below S = 96 (D = 192) the agent lights and the today card are not drawn (DESIGN 2a.6, LOD).</summary>
    private static bool IsSmall(OrbGpuContext ctx) => ctx.Size < 192;

    private static CharacterLightning.Strength LightningStrength(EffectsPolicy effects, bool small)
    {
        if (effects.Level == EffectsIntensity.Off)
        {
            return CharacterLightning.Strength.Off;
        }

        if (effects.ReduceMotion)
        {
            return CharacterLightning.Strength.FlashOnly;
        }

        return effects.Level switch
        {
            EffectsIntensity.Soft => CharacterLightning.Strength.Soft,
            EffectsIntensity.Max => small ? CharacterLightning.Strength.Standard : CharacterLightning.Strength.Max,
            _ => small ? CharacterLightning.Strength.Soft : CharacterLightning.Strength.Standard,
        };
    }

    /// <summary>Advances everything one frame (the macOS renderer's step): the stage, particles, hud, lightning and the cards.</summary>
    private void Step(in OrbFrame frame, OrbScene scene, bool snap)
    {
        _frameTime = frame.Time;
        if (_ctx is not { } ctx || Content?.Pack is not { } pack || ctx.Size < 64)
        {
            _stage = null;
            _loading = Content is { Pack: null, NoneInstalled: false };
            return;
        }

        _loading = false;
        var dt = snap ? 1.0 / 240 : Math.Min(Math.Max(frame.Dt, 1.0 / 240), 0.1);
        if (_stage is null)
        {
            _stage = new CharacterStage(NewPerformer(pack));
            _particles.Clear();
        }
        else if (!ReferenceEquals(_stage.Current.Pack, pack) && !ReferenceEquals(_stage.Incoming?.Pack, pack))
        {
            _outgoingPack = _stage.Current.Pack;
            _stage.SwitchTo(NewPerformer(pack));
        }

        var effects = frame.Effects;
        _stage.Calm = effects.ReduceMotion;
        _stage.Amplitude = effects.Level switch
        {
            EffectsIntensity.Off => 0,
            EffectsIntensity.Soft => 0.5,
            _ => 1,
        };
        var level = frame.Unavailable ? 0 : (int)scene.Title;
        _stage.Step(dt, level);
        if (_stage.Incoming is null && _stage.Outgoing is null)
        {
            _outgoingPack = null;
        }

        var current = _stage.Current;
        var g = GeometryOf(ctx, current);
        var enabled = effects.Particles;
        _particles.Use(current.Pack, PetalRect(current.Pack, current.ActivePose) is not null);
        for (var i = 0; enabled && i < current.Events.Count; i++)
        {
            _particles.Burst(g.FeetX + g.BodyDx, g.FeetY - 0.55 * g.Height, g.Unit);
        }

        var side = ctx.Canvas;
        var area = _particles.IsPetals
            ? (0.05 * side, 0.1 * side, 0.95 * side, 0.85 * side)
            : (g.FeetX - 0.625 * g.Height, g.FeetY - 1.083 * g.Height, g.FeetX + 0.625 * g.Height, g.FeetY - 0.042 * g.Height);
        _particles.Update(dt, enabled ? current.Intensity : 0, area, g.Unit, enabled ? effects.ParticleRate : 0);

        _hudTime += dt;
        _hud.EmberRate = enabled ? effects.ParticleRate : 0;
        var title = scene.Title;
        var titleText = Strings?.Title(frame.Unavailable ? SpeedTitle.Idle : title) ?? string.Empty;
        _hud.Update(dt, _hudTime, frame.Unavailable ? 0 : scene.Value, frame.Unavailable ? 0 : frame.Heat, current.Intensity, titleText);
        _lightning.Update(dt, _hud.Odometer.Digits, frame.Heat, LightningStrength(effects, IsSmall(ctx)));
        if (!effects.ReduceMotion)
        {
            _comet = CharacterMath.Mod(_comet + dt / (6.0 - 5.3 * frame.Heat), 1);
        }

        var m = new ReadoutMetrics(ctx.Size);
        var advance = Glyphs is { } glyphs && glyphs.TryGet(OrbTextKey.Create("0", OrbFont.Value, m.Value), out var zero) ? zero.Advance * 0.95 : m.Value * 0.6;
        _agents.Clear();
        if (!IsSmall(ctx) && !frame.Idle)
        {
            foreach (var source in scene.AgentDots)
            {
                var c = HeatPalette.SourceColor(source);
                _agents.Add((c.R * 255, c.G * 255, c.B * 255));
            }
        }

        if (Content.ModelName is { } model)
        {
            _lastModel = model;
        }

        LayoutCards(dt, frame, current, g);
        var speed = _wings.Places[(int)HudCard.Speed];
        var speedTop = speed.Y - _wings.Sizes[(int)HudCard.Speed].H / 2;
        _hud.Layout(dt, current.HudLayout((g.FeetX, g.FeetY), g.PointsPerPixel, g.Unit), _agents, advance, (speed.X, speedTop - 1.1 * m.Peak));
    }

    /// <summary>The headwear part's square atlas rectangle (the petal sprite), 0…1; null when the pose has none.</summary>
    private Vector4? PetalRect(CharacterPack pack, int pose)
    {
        var key = pack.Poses[pose].AtlasPath;
        if (_petals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var rig = pack.Poses[pose].Rig;
        Vector4? rect = null;
        var part = rig.Parts.FirstOrDefault(p => p.Tag == "headwear");
        if (part is { Uv.Length: >= 2 })
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity;
            for (var i = 0; i + 1 < part.Uv.Length; i += 2)
            {
                minX = Math.Min(minX, part.Uv[i]);
                maxX = Math.Max(maxX, part.Uv[i]);
                minY = Math.Min(minY, part.Uv[i + 1]);
            }

            var sideLength = maxX - minX;
            var w = (double)Math.Max(1, rig.AtlasWidth);
            var h = (double)Math.Max(1, rig.AtlasHeight);
            if (sideLength > 1)
            {
                rect = new Vector4((float)(minX / w), (float)(minY / h), (float)(maxX / w), (float)((minY + sideLength) / h));
            }
        }

        if (_petals.Count > 64)
        {
            _petals.Clear();
        }

        _petals[key] = rect;
        return rect;
    }

    /// <summary>The frame class (DESIGN 2a.6, Frame policy): 15 fps while she only breathes, 30 at cruise, 60 when she acts or from boost.</summary>
    private FrameClass Demand(in OrbFrame frame)
    {
        if (_loading)
        {
            return FrameClass.Fps30;
        }

        if (_stage is null)
        {
            return frame.Idle || frame.Unavailable || frame.Effects.ReduceMotion ? FrameClass.None : FrameClass.Fps30;
        }

        var busy = IsBusy;
        if (frame.Effects.Level == EffectsIntensity.Off || frame.Effects.ReduceMotion)
        {
            return busy ? FrameClass.Fps30 : FrameClass.None;
        }

        if (busy || _stage.Current.IsActing)
        {
            return FrameClass.Fps60;
        }

        if (frame.Unavailable)
        {
            return FrameClass.Fps15;
        }

        return _stage.Current.Band switch
        {
            null or SpeedBand.Idle => FrameClass.Fps15,
            SpeedBand.Cruise => FrameClass.Fps30,
            _ => FrameClass.Fps60,
        };
    }

    private bool IsBusy => _lightning.IsActive || (_stage is { } stage
        && (stage.Outgoing is not null || stage.Incoming is not null || stage.Current.FadingPose is not null || _hud.PeakAge is not null
            || _particles.IsLively || _wings.IsFading || _hud.HasPulses || _models.IsMoving(_frameTime) || _quota.IsMoving(_frameTime)
            || Readings?.Latency.IsFading(_frameTime) == true));

    /// <summary>The style model the orb drives (update, snap, pulse, peak, demand).</summary>
    private sealed class CharacterModel(CharacterDraw draw) : IOrbStyleModel
    {
        private OrbScene? _scene;

        public OrbStyle Style => OrbStyle.Character;

        public FrameClass Demand { get; private set; } = FrameClass.Fps30;

        public OrbScene? Scene
        {
            get => _scene;
            set => _scene = value;
        }

        public void Update(in OrbFrame frame)
        {
            if (_scene is null)
            {
                return;
            }

            draw.Step(frame, _scene, snap: false);
            Demand = draw.Demand(frame);
        }

        public void Snap(in OrbFrame frame)
        {
            if (_scene is null)
            {
                return;
            }

            draw.Step(frame, _scene, snap: true);
            Demand = draw.Demand(frame);
        }

        public void Pulse(double heat, double strength) => draw._hud.AddPulse(strength);

        public void Peak() => draw._hud.CelebratePeak();
    }

    /// <summary>The orb's shared scene, which the model reads each frame (title, value, agents).</summary>
    public void Attach(OrbScene scene) => ((CharacterModel)_model).Scene = scene;

    /// <summary>
    /// The whole scene, back to front (DESIGN 2a.6, Layers): lights behind her, particles behind her, the character (the
    /// outgoing one crossfading), lights and particles in front of her, the cards, the speed, the new-peak pill.
    /// </summary>
    public void BuildScene(OrbDrawList list, OrbScene scene, in OrbFrame frame, double highlight, Rgba accent)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scene);
        if (_ctx is not { } ctx || _stage is not { } stage)
        {
            return;
        }

        var current = stage.Current;
        var g = GeometryOf(ctx, current);
        AddHudDots(list, ctx, _hud.Back);
        var activeAtlas = -1;
        var petal = PetalRect(current.Pack, current.ActivePose);
        if (_particles.IsPetals && _particles.Items.Length > 0 && petal is not null)
        {
            activeAtlas = list.AddCharacterAtlas(current.Pack.Poses[current.ActivePose], AtlasStep(ctx, current, current.ActivePose, g));
        }

        AddParticles(list, ctx, front: false, activeAtlas, petal, g);
        if (stage.Outgoing is { } outgoing)
        {
            DrawCharacter(list, ctx, outgoing, 1 - stage.OutgoingFade);
        }

        DrawCharacter(list, ctx, current, stage.Outgoing is null ? 1 : stage.OutgoingFade);
        AddHudDots(list, ctx, _hud.Front);
        AddParticles(list, ctx, front: true, activeAtlas, petal, g);
        if (highlight > 0.01)
        {
            AddHighlight(list, ctx, accent, highlight);
        }

        DrawCards(list, ctx, frame, scene);
        DrawPeak(list, ctx, frame, scene);
    }

    private int AtlasStep(OrbGpuContext ctx, CharacterPerformer performer, int pose, in Geometry g) =>
        CharacterAtlasSteps.Step(CharacterAtlasSteps.Needed(g.PointsPerPixel * performer.Placements[pose].Scale, ctx.Scale));

    private void DrawCharacter(OrbDrawList list, OrbGpuContext ctx, CharacterPerformer performer, double alpha)
    {
        var g = GeometryOf(ctx, performer);
        if (performer.FadingPose is { } fading)
        {
            DrawPose(list, ctx, performer, fading, performer.FadingWorld, performer.FadingOpacities, alpha * (1 - performer.FadeProgress), g);
        }

        var fadeIn = performer.FadingPose is null ? 1 : performer.FadeProgress;
        DrawPose(list, ctx, performer, performer.ActivePose, performer.World, performer.Opacities, alpha * fadeIn, g);

        // Keep the pose the band asks for resident too, so a swap finds its atlas ready.
        if (performer.Band is { } band && performer.PoseIndex(band) is var wanted && wanted != performer.ActivePose)
        {
            list.AddCharacterAtlas(performer.Pack.Poses[wanted], AtlasStep(ctx, performer, wanted, g));
        }
    }

    private void DrawPose(OrbDrawList list, OrbGpuContext ctx, CharacterPerformer performer, int pose, Affine2[] world, double[] opacities, double alpha,
        in Geometry g)
    {
        if (alpha <= 0.002 || world.Length == 0 || pose >= performer.Pack.Poses.Count)
        {
            return;
        }

        var placement = performer.Placements[pose];
        list.AddCharacterMesh(performer.Pack.Poses[pose], AtlasStep(ctx, performer, pose, g), world, opacities,
            new Vector4((float)g.FeetX, (float)g.FeetY, (float)(g.PointsPerPixel * placement.Scale), (float)alpha),
            new Vector2((float)placement.FeetX, (float)placement.FeetY));
    }

    /// <summary>The readout's lights: additive on dark glass, laid over on light; unlit (white) ring lights take ink on light, doubled with Increase Contrast.</summary>
    private static void AddHudDots(OrbDrawList list, OrbGpuContext ctx, ReadOnlySpan<HudDot> dots)
    {
        var dark = ctx.Dark;
        var contrast = ctx.HighContrast ? 2f : 1f;
        foreach (var dot in dots)
        {
            var light = new OrbCharacterDot(dot);
            if (dot.R == 1 && dot.G == 1 && dot.B == 1 && dot.Softness < 0.99f)
            {
                if (!dark)
                {
                    light.Color = new Vector4(0, 0, 0, dot.Alpha * 0.55f);
                }

                light.Color.W = Math.Min(1, light.Color.W * contrast);
            }

            list.AddCharacterDot(light, dot.Additive && dark ? OrbBlend.Additive : OrbBlend.Over);
        }
    }

    /// <summary>Her particles on one side: petals sample the atlas, the other kinds are soft dots or glyphs.</summary>
    private void AddParticles(OrbDrawList list, OrbGpuContext ctx, bool front, int atlas, Vector4? petal, in Geometry g)
    {
        if (_particles.Items.Length == 0)
        {
            return;
        }

        if (_particles.Current is not { } kind)
        {
            if (atlas >= 0 && petal is { } uv)
            {
                _particles.Sprites(front, atlas, uv, 1, list);
            }

            return;
        }

        if (kind.Look != CharacterParticles.Look.Glyph)
        {
            _particles.Dots(front, 1, list, kind.Additive && ctx.Dark ? OrbBlend.Additive : OrbBlend.Over);
            return;
        }

        if (Glyphs is not { } glyphs)
        {
            return;
        }

        var size = Math.Ceiling(_particles.GlyphCeiling(g.Unit));
        var cap = glyphs.CapHeight(OrbFont.Title, size);
        foreach (var p in _particles.Items)
        {
            if (p.Front != front)
            {
                continue;
            }

            var alpha = _particles.Alpha(p);
            if (alpha <= 0.01 || !glyphs.TryGet(OrbTextKey.Create(_particles.Glyph(p), OrbFont.Title, size), out var glyph))
            {
                continue;
            }

            var color = new Rgba(p.Color.R, p.Color.G, p.Color.B, alpha);
            list.AddInlineText(glyph, p.X - glyph.Advance / 2, p.Y + cap / 2, color, null, OrbTextClip.None, _particles.SizeOf(p) / size,
                p.Rotation * Math.PI / 180, p.X, p.Y);
        }
    }

    /// <summary>Settings → 懸浮窗 rings the hit capsule (DESIGN 2a.6).</summary>
    private static void AddHighlight(OrbDrawList list, OrbGpuContext ctx, Rgba accent, double strength)
    {
        var (x, y, w, h) = OrbHitShape.Bounds(OrbStyle.Character, ctx.Size);
        var cx = ctx.CenterX + x + w / 2;
        var cy = ctx.CenterY + y + h / 2;
        list.Add(OrbGpuInstance.RoundedRing(cx, cy, w / 2, h / 2, w / 2, 2, accent.MultiplyAlpha(strength)));
    }

    /// <summary>The fallback gauge: lights on a flat ellipse at the feet, filled in the heat colors with a bright comet (CHARACTERS 8).</summary>
    private void BuildRing(OrbDrawList list, OrbGpuContext ctx, in OrbFrame frame)
    {
        var d = ctx.Size;
        var cx = ctx.CenterX;
        var cy = ctx.CenterY + FeetDrop * d;
        var rx = RingRadius * d;
        var ry = rx * RingSquash;
        var heat = frame.Unavailable ? 0 : frame.Heat;
        var lit = heat * RingLights;
        var size = Math.Max(0.6, 0.006 * d);
        list.Add(OrbGpuInstance.Ellipse(cx, cy, rx * 1.15, ry * 1.6, 0, ctx.Label(heat).WithAlpha(0.08 + 0.25 * heat), core: 0, glow: 0.4 * rx, glowAlpha: 1),
            ctx.GlowBlend);
        for (var i = 0; i < RingLights; i++)
        {
            var t = (double)i / RingLights;
            var angle = Math.PI / 2 + 2 * Math.PI * t;
            var depth = 0.55 + 0.45 * (Math.Sin(angle) + 1) / 2;
            var px = cx + rx * Math.Cos(angle);
            var py = cy + ry * Math.Sin(angle);
            var offset = CharacterMath.Mod(t - _comet + 1.5, 1) - 0.5;
            var glint = Math.Exp(-Math.Pow(offset * 18, 2));
            if (i < lit)
            {
                var color = (i == (int)lit ? Rgba.FromHex("#FFF7D6") : ctx.Label(t)).WithAlpha((0.75 + 0.25 * glint) * depth);
                list.Add(OrbGpuInstance.Circle(px, py, size * depth, color, glow: size * 2, glowAlpha: 0.5), ctx.GlowBlend);
            }
            else
            {
                var unlit = ctx.HighContrast ? ctx.Contrast.GrayText : ctx.Tokens.UnlitSegment;
                list.Add(OrbGpuInstance.Circle(px, py, size * 0.75 * depth, unlit.MultiplyAlpha((1.4 + 2 * glint) * depth)));
            }
        }
    }

    /// <summary><c>character.empty</c> above the fallback readout while no character is installed (DESIGN 2a.6).</summary>
    public void BuildEmptyLabel(OrbDrawList list, OrbGpuContext ctx, string text)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(ctx);
        if (Glyphs is not { } glyphs || string.IsNullOrEmpty(text) || ctx.Size < 64)
        {
            return;
        }

        var size = Math.Max(7, 0.03 * ctx.Size);
        if (!glyphs.TryGet(OrbTextKey.Create(text, OrbFont.Info, size), out var glyph))
        {
            return;
        }

        var layout = OrbReadoutLayout.Character;
        var baseline = ctx.CenterY + (layout.ValueBaseline - layout.ValueSize - 0.03) * ctx.Size;
        var color = ctx.HighContrast ? ctx.Contrast.GrayText : ctx.Tokens.SecondaryText;
        list.AddText(glyph, ctx.CenterX - glyph.Advance / 2, baseline, color);
    }
}
