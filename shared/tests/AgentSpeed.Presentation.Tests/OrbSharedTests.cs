using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Render;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class OrbSharedTests
{
    [Fact]
    public void OdometerRollsOnlyChangedCharactersUpWhenRising()
    {
        var odometer = new OdometerText();
        odometer.Set("128", rising: true, now: 0, animate: true);
        odometer.Set("131", rising: true, now: 1, animate: true);
        Assert.True(odometer.Rolls);
        Assert.False(odometer.Changed(0));
        Assert.True(odometer.Changed(1));
        Assert.True(odometer.Changed(2));
        var (outgoing, incoming) = odometer.Offsets(0.5);
        Assert.True(outgoing < 0);
        Assert.True(incoming > 0);
        Assert.True(odometer.IsAnimating(1 + 0.2));
        Assert.False(odometer.IsAnimating(1 + OdometerText.RollSeconds));
    }

    [Fact]
    public void OdometerCrossfadesALengthChangeInTwelveHundredths()
    {
        var odometer = new OdometerText();
        odometer.Set("99", true, 0, true);
        odometer.Set("100", true, 1, true);
        Assert.False(odometer.Rolls);
        Assert.Equal(0.5, odometer.CrossfadeAmount(1.06), 6);
        Assert.Equal(1, odometer.CrossfadeAmount(1 + OdometerText.CrossfadeSeconds), 6);
    }

    [Fact]
    public void OdometerDoesNotAnimateWhenAskedNotTo()
    {
        var odometer = new OdometerText();
        odometer.Set("5", true, 0, animate: false);
        Assert.False(odometer.IsAnimating(0));
    }

    [Theory]
    [InlineData(OrbStyle.Led, 0.012, 0.206, 0.3785, -0.232)]
    [InlineData(OrbStyle.Needle, 0.035, 0.338, 0.405, -0.175)]
    [InlineData(OrbStyle.Liquid, 0.012, 0.206, 0.3785, -0.232)]
    [InlineData(OrbStyle.Reactor, 0.03, 0.184, 0.252, -0.172)]
    [InlineData(OrbStyle.Minimal, 0.07, 0.168, null, -0.25)]
    public void ReadoutFramesFollowTheSpecTable(OrbStyle style, double value, double title, double? dots, double pill)
    {
        var layout = OrbReadoutLayout.For(style);
        Assert.Equal(value, layout.ValueBaseline, 6);
        Assert.Equal(title, layout.TitleBaseline, 6);
        Assert.Equal(dots, layout.DotsY);
        Assert.Equal(pill, layout.PillY, 6);
        Assert.Equal(style == OrbStyle.Minimal, layout.UnitSize is null);
    }

    [Fact]
    public void UnitAndTitleNeverGoBelowSevenPoints() =>
        Assert.Equal(7, OrbReadoutLayout.TextSize(0.062, 80));

    [Fact]
    public void ReadoutZoneFadesParticlesBehindTheText()
    {
        Assert.Equal(ReadoutZone.Floor, ReadoutZone.Fade(0, ReadoutZone.CenterY), 6);
        Assert.True(ReadoutZone.Fade(0.4, 0.4) > 0.99);
        Assert.InRange(ReadoutZone.Fade(0.1, 0.07), 0.35, 1);
    }

    [Fact]
    public void SpriteRasterClearsWhatThePreviousFrameDrew()
    {
        var raster = new SpriteRaster(64, 64);
        raster.Begin();
        raster.Dot(20, 20, 4, Rgba.White, 1, DotShape.Solid);
        var first = raster.End();
        Assert.False(first.IsEmpty);
        Assert.NotEqual(0u, raster.Pixels[20 * 64 + 20]);

        raster.Begin();
        raster.Dot(40, 40, 4, Rgba.White, 1, DotShape.Soft);
        var second = raster.End();
        Assert.Equal(0u, raster.Pixels[20 * 64 + 20]);
        Assert.True(second.X <= 16 && second.Right >= 44, "the dirty rect covers both the cleared and the new dot");

        raster.Begin();
        var third = raster.End();
        Assert.False(third.IsEmpty);
        raster.Begin();
        Assert.True(raster.End().IsEmpty);
    }

    [Fact]
    public void AdditiveBlendSaturatesAndOverBlendCovers()
    {
        var raster = new SpriteRaster(8, 8);
        raster.Begin();
        for (var i = 0; i < 5; i++)
        {
            raster.Dot(4, 4, 3, new Rgba(1, 0.5, 0), 0.6, DotShape.Solid, SpriteBlend.Additive);
        }

        var pixel = raster.Pixels[4 * 8 + 4];
        Assert.Equal(255u, pixel >> 24);
        Assert.True(((pixel >> 16) & 0xFF) <= 255);

        var over = new SpriteRaster(8, 8);
        over.Begin();
        over.Dot(4, 4, 3, Rgba.Black, 1, DotShape.Solid, SpriteBlend.Over);
        over.Dot(4, 4, 3, Rgba.White, 1, DotShape.Solid, SpriteBlend.Over);
        Assert.Equal(0xFFFFFFFFu, over.Pixels[4 * 8 + 4]);
    }

    [Fact]
    public void StreaksAndRingsStayInsideTheBuffer()
    {
        var raster = new SpriteRaster(16, 16);
        raster.Begin();
        raster.Streak(-10, -10, 30, 30, 2, Rgba.White, 1);
        raster.Ring(8, 8, 20, 2, Rgba.White, 1);
        raster.Dot(-50, 70, 5, Rgba.White, 1);
        var dirty = raster.End();
        Assert.True(dirty.X >= 0 && dirty.Y >= 0 && dirty.Right <= 16 && dirty.Bottom <= 16);
    }

    [Fact]
    public void SpriteRasterAllocatesNothingPerFrame()
    {
        var raster = new SpriteRaster(200, 200);
        raster.Begin();
        raster.End();
        AllocationProbe.AssertNone(() =>
        {
            for (var frame = 0; frame < 50; frame++)
            {
                raster.Begin();
                for (var i = 0; i < 100; i++)
                {
                    raster.Dot(100 + 60 * Math.Sin(i + frame), 100 + 60 * Math.Cos(i), 3, Rgba.White, 0.8, DotShape.Spark);
                }

                raster.End();
            }
        });
    }

    [Fact]
    public void SharedSignOfLifeRunsOnTheGlobalSixSecondGrid()
    {
        var scene = new OrbScene(1)
        {
            SharedSignOfLife = true,
            Effects = new EffectsPolicy(EffectsIntensity.Standard),
            Redline = HeatMath.ReferenceRedline,
        };
        scene.Update(100.5);
        Assert.Null(scene.SignOfLifeAge);
        // Idle since 100.5: the event at 102 starts less than 2 s later and is skipped; the next is at 108.
        Assert.Equal(108, scene.NextSignOfLife);
        scene.Update(108.2);
        Assert.NotNull(scene.SignOfLifeAge);
        Assert.Equal(0.2, scene.SignOfLifeAge!.Value, 6);
        scene.Update(109.2);
        Assert.Null(scene.SignOfLifeAge);
        Assert.Equal(114, scene.NextSignOfLife);
    }

    [Fact]
    public void MinimalHasNoSignOfLife()
    {
        var scene = new OrbScene(1) { SharedSignOfLife = true, SignOfLifeDuration = 0 };
        scene.Update(10);
        Assert.Null(scene.NextSignOfLife);
        Assert.Null(scene.NextWake);
    }

    [Fact]
    public void SharedFrameClassIsStaticWhenNothingMoves()
    {
        var scene = new OrbScene(1) { SharedSignOfLife = true, Effects = new EffectsPolicy(EffectsIntensity.Standard) };
        scene.Update(0);
        scene.Update(5);
        Assert.Equal(FrameClass.None, scene.SharedFrameClass(minimal: false));
        scene.TriggerPulse(100, 1000);
        Assert.Equal(FrameClass.Fps60, scene.SharedFrameClass(minimal: false));
        Assert.Equal(FrameClass.Fps30, scene.SharedFrameClass(minimal: true));
    }

    [Fact]
    public void SnapJumpsTheSpringsToTheTarget()
    {
        var scene = new OrbScene(1) { Redline = 400, Effects = new EffectsPolicy(EffectsIntensity.Standard) };
        scene.Update(0);
        scene.SetTarget(400, SpeedTier.Redline, SpeedTitle.Rocket);
        scene.Snap(0.01);
        Assert.Equal(1, scene.Heat, 6);
        Assert.False(scene.SpringsMoving);
    }
}
