using Avalonia;

namespace AgentSpeed.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine(LinuxApp.BuildVersion);
            return 0;
        }
        if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("This build requires Linux."); return 1; }
        if (args.Contains("--quit"))
        { Platform.TopBarBridge.ActivateExistingAsync("quit").GetAwaiter().GetResult(); return 0; }
        if (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" && !args.Contains("--allow-unsupported"))
        {
            Console.Error.WriteLine("This release supports GNOME/X11. Log in using Ubuntu on Xorg; --allow-unsupported runs via XWayland without window-placement guarantees.");
            return 1;
        }
        return AppBuilder.Configure<LinuxApp>().UsePlatformDetect().WithInterFont().LogToTrace()
            .With(new Avalonia.Media.FontManagerOptions
            {
                FontFallbacks = [new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK SC") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK TC") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK JP") }]
            })
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }
}
