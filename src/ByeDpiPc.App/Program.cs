using Avalonia;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;

namespace ByeDpiPc.App;

internal static class Program
{
    public static bool StartInTray { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Режим помощника TUN (запущен с правами администратора): без окна, только sing-box.
        if (args.Length > 0 && args[0] == TunHost.Flag)
        {
            Log.FileEnabled = false;
            return TunHost.RunAsync(args).GetAwaiter().GetResult();
        }

        StartInTray = args.Contains("--tray");
        using var instance = SingleInstance.TryAcquire();
        if (instance == null)
        {
            SingleInstance.ActivateExisting();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Программа", "Необработанная ошибка: " + e.ExceptionObject);
            AppHost.Current?.Connection.EmergencyCleanup();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppHost.Current?.Connection.EmergencyCleanup();

        // Прошлый сеанс мог упасть с включённым системным прокси — возвращаем как было.
        SystemProxy.Restore();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new Win32PlatformOptions { CompositionMode = new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface } })
            .LogToTrace();
}
