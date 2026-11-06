using System.Windows.Threading;
using AgentSpeed.Presentation.Updates;
using AgentSpeed.Updates;

namespace AgentSpeed.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Traffic.HelperExtraction.TryRun(args) is { } extracted)
        {
            return extracted;
        }

        StartupPhases.MarkMain();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error($"Unhandled exception (terminating: {e.IsTerminating}).", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        var launch = LaunchArguments.Parse(args);
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES") is { Length: > 0 } frames)
        {
            return Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_FRAMES_LED") is { Length: > 0 }
                ? Orb.Gpu.OrbLedCaptures.Run(frames)
                : Orb.Gpu.OrbFrameHarness.Run(frames);
        }

        if (Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_BENCH") is { Length: > 0 } bench)
        {
            return Orb.Gpu.OrbBenchHarness.Run(bench);
        }

        if (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FRAMES") is { Length: > 0 } boardFrames)
        {
            return Boards.Harness.BoardFrameHarness.Run(boardFrames);
        }

        if (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_BENCH") is { Length: > 0 } boardBench)
        {
            return Boards.Harness.BoardBenchHarness.Run(boardBench);
        }

        if (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_WINDOW") is { Length: > 0 } boardWindow)
        {
            return Boards.Harness.BoardWindowHarness.Run(boardWindow);
        }

        if (Environment.GetEnvironmentVariable("AGENT_SPEED_BOARD_FADE") is { Length: > 0 } boardFade)
        {
            return Boards.Harness.BoardFadeHarness.Run(boardFade);
        }

        using var instance = Acquire(launch);
        if (instance is null)
        {
            return 0;
        }

        AppLog.Info($"Agent Speed {AppPaths.Version} starting on Windows {Environment.OSVersion.Version}.");
        if (launch.AfterUpdate)
        {
            AppLog.Info($"Relaunched after an update from {launch.PreviousVersion ?? "an unknown version"}.");
        }

        var app = new App();
        var guard = new CrashGuard();
        app.DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled UI exception.", e.Exception);
            e.Handled = guard.ShouldContinue();
            if (!e.Handled)
            {
                AppLog.Error("Too many errors in a short time; shutting down.");
            }
        };

        using var controller = new AppController(app, instance, launch);
        app.Startup += (_, _) =>
        {
            try
            {
                controller.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("Startup failed.", ex);
                app.Shutdown(1);
            }
        };
        var exitCode = app.Run();
        AppLog.Info("Agent Speed exited.");
        return exitCode;
    }

    /// <summary>
    /// The relaunch after an update first waits for the old process to exit, then waits for the mutex, before the
    /// usual second-instance handling applies (UPDATES section 5).
    /// </summary>
    private static SingleInstance? Acquire(LaunchArguments launch)
    {
        if (launch.WaitPid is not { } pid)
        {
            return SingleInstance.TryAcquire();
        }

        var exited = UpdateRelaunch.WaitForExit(pid);
        var instance = SingleInstance.TryAcquire(UpdateRelaunch.MutexWait);
        AppLog.Info($"Relaunch: previous instance {(exited ? "exited" : "still running")}, single-instance lock {(instance is null ? "not acquired" : "acquired")}.");
        return instance;
    }

    /// <summary>Keeps running after isolated UI errors, but stops a crash loop.</summary>
    private sealed class CrashGuard
    {
        private readonly Queue<long> _recent = new();

        public bool ShouldContinue()
        {
            var now = Environment.TickCount64;
            _recent.Enqueue(now);
            while (_recent.Count > 0 && now - _recent.Peek() > 10_000)
            {
                _recent.Dequeue();
            }

            return _recent.Count <= 20;
        }
    }
}

internal sealed class App : System.Windows.Application
{
    public App()
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
    }

    public static new App Current => (App)System.Windows.Application.Current;

    public static Dispatcher UiDispatcher => Current.Dispatcher;
}
