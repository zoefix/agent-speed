using System.Runtime.Versioning;
using System.Security.Cryptography;
using AgentSpeed.Linux.Rendering;
using AgentSpeed.Linux.UI;
using AgentSpeed.Linux.Platform;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Reporting;
using Avalonia.Media;
using SkiaSharp;

[assembly: SupportedOSPlatform("linux")]
namespace AgentSpeed.Linux.Tests;

public sealed class PlatformTests
{
    [Fact]
    public void LinuxNewOrbPlacementUsesSharedFreeSpotRules()
    {
        var display = new OrbDisplay("A", new RectD(0, 0, 1920, 1080), 1, true);
        var document = new OrbsDocument([new OrbDefinition("existing"), new OrbDefinition("new")]);
        var centers = new Dictionary<string, PointD>(StringComparer.Ordinal)
        {
            ["existing"] = new PointD(960, 520),
        };

        var result = LinuxOrbLayout.PlaceNew(document, [display], centers, "new", "existing");
        var existing = new OrbLocation(display.Key, centers["existing"], document.Find("existing")!.RadiusPx(display.Scale));
        var added = result.Locations["new"];

        Assert.True(OrbGeometry.Distance(existing.Center, added.Center)
            >= existing.Radius + added.Radius + OrbGeometry.Gap - OrbGeometry.Tolerance);
        Assert.Equal(OrbPositionRecord.FromCenter(display.Key, added.Center, added.Radius, display.Work, display.Scale),
            result.Document.RecordFor(result.Arrangement, "new"));
    }

    [Fact]
    public void LinuxLayoutKeepsOrbPositionWhenDisplayArrangementChanges()
    {
        var first = new OrbDisplay("A", new RectD(0, 0, 1920, 1080), 1, true);
        var second = new OrbDisplay("B", new RectD(1920, 0, 2560, 1440), 1.5, false);
        var orb = new OrbDefinition("orb");
        var center = new PointD(800, 420);
        var record = OrbPositionRecord.FromCenter(first.Key, center, orb.RadiusPx(first.Scale), first.Work, first.Scale);
        var document = new OrbsDocument([orb]).WithPosition("A", orb.Id, record);

        var result = LinuxOrbLayout.Resolve(document, [first, second]);

        Assert.Equal(record.ToCenter(first.Work, first.Scale, orb.RadiusPx(first.Scale)), result.Locations[orb.Id].Center);
        Assert.Equal(first.Key, result.Locations[orb.Id].Display);
        Assert.Equal(record, result.Document.RecordFor(result.Arrangement, orb.Id));
    }

    [Fact]
    public void LinuxLayoutMovesMissingDisplayPositionToPrimaryUsingSavedAnchors()
    {
        var removed = new OrbDisplay("removed", new RectD(0, 0, 1920, 1080), 1, false);
        var primary = new OrbDisplay("primary", new RectD(1920, 0, 2560, 1440), 1.5, true);
        var orb = new OrbDefinition("orb");
        var record = new OrbPositionRecord(removed.Key, OrbAxis.Fraction(.4), OrbAxis.High(12));
        var document = new OrbsDocument([orb]).WithPosition("primary+removed", orb.Id, record);

        var result = LinuxOrbLayout.Resolve(document, [primary]);
        var expected = result.Document.RecordFor(result.Arrangement, orb.Id)
            ?? throw new InvalidOperationException("The resolved layout did not store the orb position.");

        Assert.Equal(primary.Key, result.Locations[orb.Id].Display);
        Assert.Equal(primary.Key, expected.Display);
        Assert.Equal(new OrbAxis(OrbAxisAnchor.Fraction, .4), expected.X);
        Assert.Equal(new OrbAxis(OrbAxisAnchor.High, 12), expected.Y);
        Assert.Equal(expected.ToCenter(primary.Work, primary.Scale, orb.RadiusPx(primary.Scale)), result.Locations[orb.Id].Center);
    }

    [Fact]
    public void ClosingRetainedOrbFlushesItsPositionBeforeClosing()
    {
        var actions = new List<string>();

        LinuxOrbPositionLifecycle.Close(retainPosition: true,
            () => actions.Add("flush"), () => actions.Add("close"));

        Assert.Equal(["flush", "close"], actions);
    }

    [Fact]
    public void RemovingOrbClosesWithoutTryingToPersistItsDeletedPosition()
    {
        var actions = new List<string>();

        LinuxOrbPositionLifecycle.Close(retainPosition: false,
            () => actions.Add("flush"), () => actions.Add("close"));

        Assert.Equal(["close"], actions);
    }

    [Fact]
    public void SuspendingFlushesOrbPositionsBeforeTrafficAndSettings()
    {
        var actions = new List<string>();

        LinuxOrbPositionLifecycle.Suspend(
            () => actions.Add("positions"), () => actions.Add("traffic"), () => actions.Add("settings"));

        Assert.Equal(["positions", "traffic", "settings"], actions);
    }

    [Fact]
    public void OrbInputShapeUsesTheCurrentScaleForItsDiscAndNamePlate()
    {
        var oneX = X11Windows.BuildOrbInputRegion(148, 1, namePlate: true);
        var twoX = X11Windows.BuildOrbInputRegion(148, 2, namePlate: true);

        Assert.Contains(oneX, rectangle => rectangle.X == 37 && rectangle.Y == 111 && rectangle.Width == 148);
        Assert.Contains(twoX, rectangle => rectangle.X == 74 && rectangle.Y == 222 && rectangle.Width == 296);
        Assert.Contains(oneX, rectangle => rectangle.Y == 222 && rectangle.Height == 30);
        Assert.Contains(twoX, rectangle => rectangle.Y == 444 && rectangle.Height == 60);
    }

    [Theory]
    [InlineData("'prefer-dark'", "'Yaru'", true)]
    [InlineData("'prefer-light'", "'Yaru-dark'", false)]
    [InlineData("'default'", "'Yaru-dark'", true)]
    [InlineData("'default'", "'Adwaita'", false)]
    public void AutomaticOrbAppearanceFollowsGnomePreference(string scheme, string theme, bool dark)
        => Assert.Equal(dark, LinuxAccessibility.ResolveDark(scheme, theme));

    [Fact]
    public void OrbDigitsUseTheShippedFontInsteadOfAPlatformFallback()
    {
        using var glyphs = new SkiaGlyphs();
        foreach (var (text, font) in new[] { ("0", OrbFont.Value), ("183", OrbFont.Value), ("tok/s", OrbFont.Unit) })
        {
            Assert.True(glyphs.TryGet(OrbTextKey.Create(text, font, 36), out var glyph));
            Assert.Contains("Inter", glyphs.At(glyph.U0).Face.FamilyName, StringComparison.OrdinalIgnoreCase);
            Assert.True(glyph.Advance > 10);
            using var renderer = new SkiaOrbRenderer(glyphs);
            var list = new OrbDrawList(); list.AddText(glyph, 30, 70, Rgba.White);
            using var surface = SKSurface.Create(new SKImageInfo(200, 100));
            surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, list, software: true);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            Assert.True(pixels.Pixels.Count(p => p.Alpha > 128) > 50, $"{text} contains no legible ink.");
        }
    }
    [Fact]
    public void ChartScaleHandlesEmptyZeroAndNonFiniteSamples()
    {
        Assert.Equal(1, LineChart.SafeScaleMax([]));
        Assert.Equal(1, LineChart.SafeScaleMax([0, -2, double.NaN, double.PositiveInfinity]));
        var clean = LineChart.Sanitize([double.NaN, -2, 12.5, double.PositiveInfinity]);
        Assert.Equal([0d, 0d, 12.5, 0d], clean);
        Assert.True(LineChart.SafeScaleMax(clean) >= 12.5);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DesktopPaletteKeepsTextReadable(bool dark, bool highContrast)
    {
        var palette = DesktopStyles.PaletteFor(dark, highContrast);
        foreach (var background in new[] { palette.Window, palette.Sidebar, palette.Card, palette.CardInset })
        {
            Assert.True(Contrast(palette.TextPrimary, background) >= 4.5);
            Assert.True(Contrast(palette.TextSecondary, background) >= 4.5);
            Assert.True(Contrast(palette.TextMuted, background) >= 4.5);
            Assert.True(Contrast(palette.Success, background) >= 4.5);
            Assert.True(Contrast(palette.Accent, background) >= 4.5);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThemeBinderAppliesHighContrastRefreshWithoutChangingLightOrDark(bool dark)
    {
        var binder = new ThemeBinder(DesktopStyles.PaletteFor(dark, highContrast: false));
        var refreshed = DesktopStyles.PaletteFor(dark, highContrast: true);

        Assert.Equal(dark, refreshed.Dark);
        binder.Apply(refreshed);

        Assert.True(binder.Palette.HighContrast);
        Assert.Equal(dark, binder.Palette.Dark);
    }

    private static double Contrast(Color foreground, Color background) =>
        Rgba.ContrastRatio(ToRgba(foreground), ToRgba(background));

    private static Rgba ToRgba(Color color) => new(color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0);

    [Fact]
    public void LinuxUiHasAllFourLanguages()
    {
        Assert.Empty(AgentSpeed.Linux.UI.LinuxText.Table.MissingTranslations(AgentSpeed.Presentation.Localization.LanguageCodes.All));
        Assert.Equal("实时面板", AgentSpeed.Linux.UI.LinuxText.Get("zh-Hans", "Dashboard"));
        Assert.Equal("ダッシュボード", AgentSpeed.Linux.UI.LinuxText.Get("ja", "Dashboard"));
    }
    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Needle)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Minimal)]
    public void AllStylesRenderAtIdleAndRedline(OrbStyle style)
    {
        using var glyphs = new SkiaGlyphs(); using var renderer = new SkiaOrbRenderer(glyphs);
        var instrument = new OrbGpuOrb(42, glyphs);
        var effects = new EffectsPolicy(EffectsIntensity.Standard, true);
        var labels = Enumerable.Repeat("TEST", 16).ToArray();
        instrument.Configure(new(style, 148, effects, true, true, 100, new("tok/s", "NEW PEAK", labels, true), null, default),
            new(148, 1, OrbTokens.Dark, effects));
        using var surface = SKSurface.Create(new SKImageInfo(222, 222));
        foreach (var speed in new[] { 0, 140d })
        {
            instrument.SetData(speed + 1, speed, AgentSpeed.Presentation.Heat.SpeedTier.Boost,
                AgentSpeed.Presentation.Heat.SpeedTitle.Rocket, [], "", null);
            instrument.Render(speed + 1);
            foreach (var software in new[] { false, true })
            {
                surface.Canvas.Clear(SKColors.Transparent);
                renderer.Draw(surface.Canvas, instrument.List, software);
                using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
                Assert.Contains(pixels.Pixels, p => p.Alpha > 0);
                Assert.Equal(0, pixels.GetPixel(0, 0).Alpha); // The floating window must retain its transparent corners.
            }
        }
    }
    [Fact]
    public void DeviceSecretsArePrivateAndSurviveRotation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-speed-secrets-" + Guid.NewGuid());
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var keys = new ReportKeyStore(directory);
            using var old = keys.CreateKey(id); using var loaded = keys.LoadKey(id);
            Assert.Equal(old.PublicKey.Fingerprint, loaded!.PublicKey.Fingerprint);
            using var next = keys.CreateKey(id, pending: true); Assert.True(keys.PromotePending(id));
            using var rotated = keys.LoadKey(id); Assert.Equal(next.PublicKey.Fingerprint, rotated!.PublicKey.Fingerprint);
            keys.SaveApiKey("secret", id); Assert.Equal("secret", keys.LoadApiKey(id));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            foreach (var file in Directory.EnumerateFiles(directory)) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Throws<ArgumentException>(() => keys.CreateKey("../../outside"));
            keys.DeleteAll(id); Assert.False(keys.HasFiles(id));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void KeyStoreWriteFailuresUseTheHandledStorageExceptionContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-speed-secret-blocker-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var blockedDirectory = Path.Combine(root, "report");
            File.WriteAllText(blockedDirectory, "not a directory");
            var keys = new ReportKeyStore(blockedDirectory);

            var createError = Assert.Throws<CryptographicException>(() => keys.CreateKey(id));
            Assert.IsAssignableFrom<IOException>(createError.InnerException);
            var apiKeyError = Assert.Throws<CryptographicException>(() => keys.SaveApiKey("fake-api-key-123", id));
            Assert.IsAssignableFrom<IOException>(apiKeyError.InnerException);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void FailedAtomicReplacementPreservesTheExistingDeviceKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-speed-secret-replacement-" + Guid.NewGuid());
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var keys = new ReportKeyStore(directory);
            using var original = keys.CreateKey(id);
            var blockedPendingPath = Path.Combine(directory, id + ".pending.key");
            Directory.CreateDirectory(blockedPendingPath);

            Assert.Throws<CryptographicException>(() => keys.CreateKey(id, pending: true));
            using var loaded = keys.LoadKey(id);
            Assert.Equal(original.PublicKey.Fingerprint, loaded!.PublicKey.Fingerprint);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory),
                path => Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void FailedPendingPromotionKeepsThePendingKeyAndDeleteIsBestEffort()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-speed-secret-promotion-" + Guid.NewGuid());
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var keys = new ReportKeyStore(directory);
            using var pending = keys.CreateKey(id, pending: true);
            var blockedTarget = Path.Combine(directory, id + ".key");
            Directory.CreateDirectory(blockedTarget);

            Assert.False(keys.PromotePending(id));
            Assert.True(File.Exists(Path.Combine(directory, id + ".pending.key")));
            keys.DeleteAll(id);
            Assert.True(Directory.Exists(blockedTarget));
            Assert.False(File.Exists(Path.Combine(directory, id + ".pending.key")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void PairingPollingRequiresTheVisibleReportingTabNetworkAndLivePairing()
    {
        var now = DateTimeOffset.Parse("2026-10-28T00:00:00Z");
        var waiting = new ReportPairing("pairing-id", "waiting", now.AddMinutes(1), null, null);
        Assert.True(MainWindow.ShouldPollPairing(true, true, true, true, waiting, now));
        Assert.False(MainWindow.ShouldPollPairing(false, true, true, true, waiting, now));
        Assert.False(MainWindow.ShouldPollPairing(true, false, true, true, waiting, now));
        Assert.False(MainWindow.ShouldPollPairing(true, true, false, true, waiting, now));
        Assert.False(MainWindow.ShouldPollPairing(true, true, true, false, waiting, now));
        Assert.False(MainWindow.ShouldPollPairing(true, true, true, true, null, now));
        Assert.False(MainWindow.ShouldPollPairing(true, true, true, true, waiting with { State = "approved" }, now));
        Assert.False(MainWindow.ShouldPollPairing(true, true, true, true, waiting with { ExpiresAt = now }, now));
    }

    [Fact]
    public async Task LatePairingPollCannotReplacePairingCreatedWhileItWasWaiting()
    {
        var polls = new PairingRequestVersion();
        var operations = new PairingRequestVersion();
        var pairing = "pairing A";
        var pollVersion = polls.Current;
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task CompletePoll()
        {
            var result = await response.Task;
            polls.TryApply(pollVersion, () => pairing = result);
        }

        var poll = CompletePoll();
        var createVersion = operations.Next();
        polls.Next();
        Assert.True(operations.TryApply(createVersion, () =>
        {
            polls.Next();
            pairing = "pairing B";
        }));

        response.SetResult("pairing A");
        await poll;

        Assert.Equal("pairing B", pairing);
    }

    [Fact]
    public async Task LastPairingMutationWinsWhenFakeResponsesReturnOutOfOrder()
    {
        var operations = new PairingRequestVersion();
        var pairing = "pairing A";
        var requestsStarted = 0;
        var createResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        long StartFakeRequest()
        {
            requestsStarted++;
            return operations.Next();
        }

        async Task ApplyResponse(long version, Task<string> response)
        {
            var result = await response;
            operations.TryApply(version, () => pairing = result);
        }

        var createVersion = StartFakeRequest();
        var create = ApplyResponse(createVersion, createResponse.Task);
        var claimVersion = StartFakeRequest();
        var claim = ApplyResponse(claimVersion, claimResponse.Task);
        Assert.Equal(2, requestsStarted);

        claimResponse.SetResult("pairing B");
        await claim;
        createResponse.SetResult("pairing A");
        await create;

        Assert.Equal("pairing B", pairing);
        Assert.Equal(2, requestsStarted);
    }

    [Fact]
    public void RelativeXdgPathIsIgnored()
    {
        const string variable = "AGENT_SPEED_XDG_UNIT_TEST";
        var before = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "relative/path");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "agent-speed"), LinuxPaths.Resolve(variable, ".config"));
        }
        finally { Environment.SetEnvironmentVariable(variable, before); }
    }
}
