using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentSpeed.Boards.Gpu;
using AgentSpeed.Boards.Layers;
using AgentSpeed.Interop;
using AgentSpeed.Orb.Gpu;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Demo;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Boards.Harness;

/// <summary>
/// Developer harness <c>AGENT_SPEED_BOARD_FADE=FILE</c>: one real board window (click-through, bottom-right of the primary work
/// area) with fade when idle on, fed by the demo on the real clock (DESIGN 2c, Fade when idle). It runs busy, then quiet until
/// the board fades, samples the fade, counts the frames it built and presented while fading and once faded, then a simulated
/// hover (the window's own input path), the fade again with Reduce Motion, and the sources' activity. <c>FILE</c> is the report,
/// <c>FILE-normal.png</c> and <c>FILE-faded.png</c> the board as drawn (premultiplied alpha). Runs next to the installed app.
/// <c>AGENT_SPEED_BOARD_FADE_STYLE</c> (default neon), <c>_SIZE</c> (default S) and <c>_LIGHT=1</c> adjust it.
/// </summary>
internal static class BoardFadeHarness
{
    public static int Run(string path)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        if (!GpuDevice.Initialize() || GpuDevice.Shared is not { } device)
        {
            File.WriteAllText(path, "Direct3D 11 unavailable: " + GpuDevice.Failure);
            return 2;
        }

        var style = BoardStyles.Parse(Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FADE_STYLE") ?? "neon");
        var size = BoardSizes.Parse(Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FADE_SIZE") ?? "S");
        var dark = Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FADE_LIGHT") != "1";
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
        var watch = Stopwatch.StartNew();
        var clock = new OrbClock(dispatcher, () => watch.Elapsed.TotalSeconds);
        clock.UseCompositorTicker();
        var monitor = Monitors.Primary();
        var work = monitor?.WorkRect ?? new RectD(0, 0, 1920, 1080);
        var scale = monitor?.Scale ?? 1;
        var half = BoardPlacement.HalfSide(size, scale);
        var inset = (OrbGeometry.DefaultInset + BoardCanvas.Margin) * scale;
        var policy = new BoardEffectsPolicy(EffectsIntensity.Standard);
        var options = new BoardWindowOptions(AlwaysOnTop: true, ClickThrough: true, Opacity: 1, FadeWhenIdle: true);
        var window = new GpuBoardWindow("fade", device, clock, style, size, BoardPainters.Create(style, loc));
        window.Surface.Scene.Dark = dark;
        window.Configure(style, size, policy, options);
        window.MoveTo(new PointD(work.Right - inset - half, work.Bottom - inset - half));

        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = loc, Readouts = BoardReadouts.All };
        feed.Sim.TargetTotal = 700;
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            File.WriteAllText(path, report.ToString());
        }

        Say(string.Create(CultureInfo.InvariantCulture,
            $"adapter: {device.AdapterName}, {BoardStyles.Key(style)} {BoardSizes.Key(size)} {(dark ? "dark" : "light")}, scale {scale}, clock {clock.Source}"));
        var idleAt = double.NaN;
        var activeAt = double.NaN;
        var ticker = new DispatcherTimer(TimeSpan.FromSeconds(BoardSim.TickSeconds), DispatcherPriority.Normal, (_, _) =>
        {
            var tick = feed.Step(style, size);
            window.Apply(tick);
            var active = tick.Tier != SpeedTier.Idle;
            window.NoteActivity(active);
            var now = watch.Elapsed.TotalSeconds;
            if (!active && double.IsNaN(idleAt))
            {
                idleAt = now;
            }
            else if (active && double.IsNaN(activeAt) && !double.IsNaN(idleAt))
            {
                activeAt = now;
            }
        }, dispatcher);
        window.Show();
        ticker.Start();
        FrameStats.Attach();

        string State() => string.Create(CultureInfo.InvariantCulture, $"faded {window.IsFaded}, opacity {window.Surface.Opacity:0.000}");
        (long Built, long Presented, long Busy) Frames() =>
            (BoardSurface.FramesPresented + BoardSurface.FramesSkipped + BoardSurface.FramesBusy, BoardSurface.FramesPresented, BoardSurface.FramesBusy);
        string Since((long Built, long Presented, long Busy) before, double seconds)
        {
            var (built, presented, busy) = Frames();
            return string.Create(CultureInfo.InvariantCulture,
                $"{built - before.Built} built ({(built - before.Built) / seconds:0.0}/s), {presented - before.Presented} presented ({(presented - before.Presented) / seconds:0.0}/s), {busy - before.Busy} busy");
        }

        void Steps(params (double Seconds, Action Action)[] steps)
        {
            var index = 0;
            void Next()
            {
                if (index == steps.Length)
                {
                    return;
                }

                var (seconds, action) = steps[index++];
                After(dispatcher, seconds, () =>
                {
                    action();
                    Next();
                });
            }

            Next();
        }

        var idleFrames = Frames();
        double fadeAt = 0;
        var fadeFrames = Frames();
        var faded = Frames();
        Steps(
            (4, () =>
            {
                Say("busy: " + State());
                Save(window, clock, path + "-normal.png", report);
                feed.Sim.Quiet = true;
                feed.Sim.TargetTotal = 0;
            }),
            (8, () =>
            {
                idleFrames = Frames();
                Say(string.Create(CultureInfo.InvariantCulture, $"idle since {idleAt - 4:0.0} s after going quiet: {State()}"));
            }));

        // Polls until the board fades (60 s after its first idle tick, on the 2 s housekeeping), then samples the fade.
        var poll = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
        poll.Tick += (_, _) =>
        {
            var now = watch.Elapsed.TotalSeconds;
            if (!window.IsFaded)
            {
                if (now > 100)
                {
                    poll.Stop();
                    Say("never faded: " + State());
                    Finish();
                }

                return;
            }

            poll.Stop();
            fadeAt = now;
            fadeFrames = Frames();
            var steady = Since(idleFrames, now - 12);
            Say(string.Create(CultureInfo.InvariantCulture,
                $"fade began {now - idleAt:0.00} s after the first idle tick (idle, not faded: {steady}); at once: {State()}"));
            Steps(
                (0.15, () => Say("+0.15 s: " + State())),
                (0.15, () => Say("+0.30 s: " + State())),
                (0.15, () => Say("+0.45 s: " + State())),
                (0.35, () =>
                {
                    Say(string.Create(CultureInfo.InvariantCulture, $"+0.80 s: {State()}; while fading: {Since(fadeFrames, watch.Elapsed.TotalSeconds - fadeAt)}"));
                    faded = Frames();
                }),
                (5, () =>
                {
                    Say("faded, next 5 s: " + Since(faded, 5));
                    Save(window, clock, path + "-faded.png", report);
                    var center = window.Surface.Pixels / 2;
                    window.OnMouseMove(center, center);
                    Say("hover: " + State());
                }),
                (3, () =>
                {
                    Say("still hovered after 3 s: " + State());
                    window.OnMouseLeave();
                    window.Configure(style, size, policy with { ReduceMotion = true }, options with { ReduceMotion = true });
                    faded = Frames();
                }),
                (3, () =>
                {
                    Say("3 s after the pointer left, Reduce Motion on: " + State() + "; " + Since(faded, 3));
                    feed.Sim.Quiet = false;
                    feed.Sim.TargetTotal = 700;
                    activeAt = double.NaN;
                }),
                (4, () =>
                {
                    Say(string.Create(CultureInfo.InvariantCulture,
                        $"sources active again (first active tick {(double.IsNaN(activeAt) ? "none" : (watch.Elapsed.TotalSeconds - activeAt).ToString("0.0", CultureInfo.InvariantCulture) + " s ago")}): {State()}"));
                    Finish();
                }));
        };
        After(dispatcher, 12, poll.Start);

        Dispatcher.Run();
        return 0;

        void Finish()
        {
            ticker.Stop();
            window.Dispose();
            Say("done");
            dispatcher.InvokeShutdown();
        }
    }

    private static void After(Dispatcher dispatcher, double seconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private static void Save(GpuBoardWindow window, OrbClock clock, string path, StringBuilder report)
    {
        using var target = new OffscreenTarget();
        if (window.Surface.Capture(clock.Now, target) is not { } bitmap)
        {
            report.AppendLine("capture failed: " + Path.GetFileName(path));
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
