using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Render;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The liquid's additive lights drawn with normal blending (DESIGN 2a.3) and the raster shapes they use.</summary>
public class LiquidLightTests
{
    private static (LiquidModel Model, EnergyOrbDriver Driver) Boiling(bool dark = true, double ratio = 1.45)
    {
        var model = new LiquidModel(5);
        var driver = new EnergyOrbDriver(model) { Size = 180, Dark = dark }.Speed(ratio).Level(EffectsIntensity.Standard);
        driver.Snap();
        driver.Run(1.5);
        return (model, driver);
    }

    private static Rgba Over(Rgba top, Rgba under)
    {
        var a = Math.Clamp(top.A, 0, 1);
        return new(top.R * a + under.R * (1 - a), top.G * a + under.G * (1 - a), top.B * a + under.B * (1 - a));
    }

    [Theory]
    [InlineData(0.0, 0.3)]
    [InlineData(-0.2, 0.15)]
    [InlineData(0.15, 0.36)]
    [InlineData(0.1, -0.3)]
    public void LitColorDrawnOverAddsTheLightToWhatIsUnderIt(double x, double y)
    {
        var (model, _) = Boiling();
        var light = new Rgba(0.4, 0.3, 0.15, 0.35);
        var under = model.LightBase(x, y, LiquidLightStage.AboveScrim);
        var drawn = Over(model.LitOver(light, x, y), under);
        Assert.Equal(Math.Min(1, under.R + light.A * light.R), drawn.R, 6);
        Assert.Equal(Math.Min(1, under.G + light.A * light.G), drawn.G, 6);
        Assert.Equal(Math.Min(1, under.B + light.A * light.B), drawn.B, 6);
    }

    [Fact]
    public void LitMoteOnGoldLiquidIsBrighterThanTheSameMoteBlendedNormally()
    {
        var (model, _) = Boiling();
        var mote = OkLab.Mix(model.LiquidColor, EnergyPalette.WhiteHot, 0.9).WithAlpha(0.4);
        var under = model.LightBase(-0.25, 0.25, LiquidLightStage.AboveScrim);
        var lit = Over(model.LitOver(mote, -0.25, 0.25), under);
        var plain = Over(mote, under);
        // A near-white light still adds in full: the color keeps within 1 by drawing it at a higher alpha.
        Assert.Equal(under.R + 0.4 * mote.R, lit.R, 6);
        Assert.Equal(under.G + 0.4 * mote.G, lit.G, 6);
        Assert.True(lit.R > plain.R + 0.05);
        Assert.True(lit.G > plain.G + 0.05);
        // The halo keeps the liquid's hue instead of washing it toward cream.
        Assert.True(lit.R - lit.B > plain.R - plain.B);
    }

    [Fact]
    public void LightAppearanceBlendsLightsNormally()
    {
        var (model, _) = Boiling(dark: false);
        var light = new Rgba(0.9, 0.7, 0.2, 0.5);
        Assert.Equal(light, model.LitOver(light, 0, 0.3));
    }

    [Fact]
    public void BaseFollowsTheLayersUnderTheLight()
    {
        var (cruise, _) = Boiling(ratio: 0.2);
        var glassTop = cruise.LightBase(0, -0.44, LiquidLightStage.AboveScrim);
        var body = LiquidModel.DarkBodyTop;
        // Above the surface and the spill: the dark body, a touch darker for the volume shading.
        Assert.True(glassTop.R <= body.R + 1e-9 && glassTop.R > body.R * 0.5);

        // In the spill band the glass picks up the liquid's light.
        var (model, _) = Boiling();
        var spill = model.LightBase(0, model.MeanSurfaceY - 0.02, LiquidLightStage.AboveScrim);
        Assert.True(spill.R > model.LightBase(0, -0.44, LiquidLightStage.AboveScrim).R - 1e-9);

        // Under the surface the liquid shows; the scrim darkens it behind the readout only for lights above the scrim.
        var below = model.LightBase(0, 0.1, LiquidLightStage.InnerGlow);
        var scrimmed = model.LightBase(0, 0.1, LiquidLightStage.AboveScrim);
        Assert.True(below.R > 0.2);
        Assert.True(scrimmed.R < below.R * 0.75);

        // Deep in the liquid it darkens toward the bottom; the inner glow lifts it for the layers above the glow.
        var deep = model.LightBase(0, 0.4, LiquidLightStage.InnerGlow);
        var lifted = model.LightBase(0, 0.4, LiquidLightStage.AboveScrim);
        Assert.True(lifted.G > deep.G + 0.05);
    }

    [Fact]
    public void MoteProfilesFollowThePrototypeGradient()
    {
        var raster = new SpriteRaster(64, 64);
        raster.Begin();
        raster.Dot(32, 32, 20, Rgba.White, 1, DotShape.Mote, SpriteBlend.Over);
        static double Alpha(uint pixel) => (pixel >> 24) / 255.0;
        Assert.InRange(Alpha(raster.Pixels[31 * 64 + 31]), 0.95, 1);
        // 35 % of the radius: 40 / 95 of the peak.
        Assert.InRange(Alpha(raster.Pixels[31 * 64 + 31 + 7]), 0.38, 0.47);
        // 82 % of the radius: 40 / 95 × 0.18 / 0.65.
        Assert.InRange(Alpha(raster.Pixels[31 * 64 + 31 + 17]), 0.08, 0.14);
        Assert.Equal(0u, raster.Pixels[31 * 64 + 31 + 21] >> 24);

        var light = new SpriteRaster(64, 64);
        light.Begin();
        light.Dot(32, 32, 20, Rgba.White, 1, DotShape.MoteLight, SpriteBlend.Over);
        Assert.InRange(Alpha(light.Pixels[31 * 64 + 31 + 7]), 0.46, 0.56);
    }

    [Fact]
    public void SurfaceGlowIsTheStrokesBlurredShadow()
    {
        var raster = new SpriteRaster(100, 100);
        raster.Begin();
        double[] xs = [0, 50, 100];
        double[] ys = [50, 50, 50];
        const double width = 2;
        const double sigma = 5;
        raster.SurfaceGlow(xs, ys, width, sigma, new Rgba(1, 0.8, 0.1, 0.9), 0.95);
        static double Alpha(uint pixel) => (pixel >> 24) / 255.0;
        var center = Alpha(raster.Pixels[49 * 100 + 40]);
        // A 2 px box blurred with σ 5: 0.9 × 0.95 × (Φ(0.2) − Φ(−0.2)) ≈ 0.136 at the line.
        Assert.InRange(center, 0.12, 0.15);
        Assert.Equal(center, Alpha(raster.Pixels[50 * 100 + 40]), 2);
        var off = Alpha(raster.Pixels[(50 + 10) * 100 + 40]);
        Assert.InRange(off, 0.01, center * 0.25);
        Assert.Equal(0u, raster.Pixels[(50 + 20) * 100 + 40]);
        var dirty = raster.End();
        Assert.True(dirty.Y >= 50 - 20 && dirty.Bottom <= 50 + 20);
    }

    [Fact]
    public void SurfaceGlowStaysInsideTheClipCircle()
    {
        var raster = new SpriteRaster(100, 100);
        raster.SetClipCircle(50, 50, 30);
        raster.Begin();
        double[] xs = [0, 100];
        double[] ys = [50, 50];
        raster.SurfaceGlow(xs, ys, 2, 4, Rgba.White, 1);
        Assert.Equal(0u, raster.Pixels[50 * 100 + 5]);
        Assert.NotEqual(0u, raster.Pixels[50 * 100 + 50]);
    }

    [Fact]
    public void SurfaceGlowAllocatesNothing()
    {
        var raster = new SpriteRaster(200, 200);
        var xs = new double[48];
        var ys = new double[48];
        for (var i = 0; i < xs.Length; i++)
        {
            xs[i] = 4 * i;
            ys[i] = 100 + 6 * Math.Sin(i * 0.4);
        }

        raster.Begin();
        raster.End();
        AllocationProbe.AssertNone(() =>
        {
            raster.Begin();
            raster.SurfaceGlow(xs, ys, 2, 5, Rgba.White, 0.8);
            raster.End();
        });
    }

    [Fact]
    public void LitColorsAllocateNothing()
    {
        var (model, _) = Boiling();
        var light = new Rgba(1, 0.9, 0.6, 0.5);
        model.LitOver(light, 0.1, 0.2);
        AllocationProbe.AssertNone(() =>
        {
            for (var i = 0; i < 64; i++)
            {
                _ = model.LitOver(light, -0.3 + i * 0.01, 0.3 - i * 0.008);
            }
        });
    }
}
