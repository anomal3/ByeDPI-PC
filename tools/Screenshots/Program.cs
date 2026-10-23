using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using ByeDpiPc.App;
using ByeDpiPc.App.Services;
using ByeDpiPc.App.ViewModels;
using ByeDpiPc.App.Views;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Platform;
using ByeDpiPc.Core.Routing;
using ByeDpiPc.Core.Testing;

// Скриншоты для README: dotnet run --project tools/Screenshots -- <папка>
var outDir = args.Length > 0 ? args[0] : Path.Combine(FindRoot(), "docs", "screenshots");
Directory.CreateDirectory(outDir);
Environment.SetEnvironmentVariable("BYEDPI_PC_HOME", Path.Combine(Path.GetTempPath(), "byedpi-pc-screens"));
Log.FileEnabled = false;
App.DemoMode = true;

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

foreach (var theme in new[] { "light", "dark" })
{
    Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
    var settings = new AppSettings { Theme = theme == "dark" ? ThemeMode.Dark : ThemeMode.Light };
    Presets.EnsureDefaults(settings);
    settings.Rules.Add(new AppRule { Name = "qBittorrent", Icon = "app", Description = @"C:\Program Files\qBittorrent\qbittorrent.exe", Processes = { "qbittorrent.exe" } });
    settings.LastTuned = DateTime.Today.AddHours(14).AddMinutes(32);
    settings.LastTuneSummary = "Обход работает: открываются 14 из 14 заблокированных сайтов.";

    var host = new AppHost(new SettingsStore(Path.Combine(Path.GetTempPath(), "byedpi-pc-screens.json")), settings, demoMode: true);
    var vm = new MainWindowViewModel(host);
    var window = new MainWindow { DataContext = vm, Width = 1100, Height = 740 };
    window.Show();

    // Главная: подключено, идёт автоподбор
    host.SetDemoState(ConnectionState.Connected);
    host.IsTuning = true;
    host.TuneText = "Подбираю стратегию: 23 из 64";
    host.TuneHasProgress = true;
    host.TuneProgress = 0.36;
    Shot(window, vm, PageKind.Home, $"home-tuning-{theme}");

    host.IsTuning = false;
    host.TuneText = "";
    Shot(window, vm, PageKind.Home, $"home-{theme}");

    host.SetDemoState(ConnectionState.Disconnected);
    Shot(window, vm, PageKind.Home, $"home-off-{theme}");
    host.SetDemoState(ConnectionState.Connected);

    host.Update = new ByeDpiPc.Core.Update.UpdateInfo(new Version(1, 2, 0), "v1.2.0", "", "https://github.com", null, null, 0);
    host.StrategyBroken = true;
    Shot(window, vm, PageKind.Home, $"home-banners-{theme}");
    host.Update = null;
    host.StrategyBroken = false;

    var shareSettings = new AppSettings();
    Presets.EnsureDefaults(shareSettings);
    shareSettings.UpstreamEnabled = true;
    shareSettings.UpstreamUri = "vless://49959f50-96be-49f3-b023-31dc2e4f2478@203.0.113.10:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=SLRh56AUOWqx2LIYunJklMRpOzRaS5QsWYUJ0B9WAy0&sid=b4f1c68d8440ecff&flow=xtls-rprx-vision&type=tcp#Telegram-Ivan";
    shareSettings.Rules.First(r => r.Id == "preset.telegram").Route = RouteMode.Upstream;
    var code = ByeDpiPc.Core.Sharing.ShareBundle.Create(shareSettings, "Telegram для Ивана", new ByeDpiPc.Core.Sharing.ShareBundle.Options { Strategy = false, Upstream = true }).ToCode();
    vm.ImportText(code);
    Shot(window, vm, PageKind.Home, $"share-import-{theme}", ticks: 150);
    vm.ShowDialog(null);

    var telegram = vm.Apps.Items.First(i => i.Rule.Id == "preset.telegram");
    telegram.IsExpanded = true;
    Shot(window, vm, PageKind.Apps, $"apps-{theme}");
    telegram.IsExpanded = false;

    vm.ShowDialog(new AppPickerViewModel("Запущенные программы", DemoApps, vm.Apps, () => vm.ShowDialog(null)));
    Shot(window, vm, PageKind.Apps, $"apps-picker-{theme}", ticks: 150);
    vm.ShowDialog(null);

    Shot(window, vm, PageKind.Lists, $"lists-{theme}");
    vm.Editor.SelectedProfile = "OOB + TLS-запись";
    Shot(window, vm, PageKind.Editor, $"editor-{theme}");

    FillTester(vm.Tester);
    Shot(window, vm, PageKind.Tester, $"tester-{theme}");

    FillLogs();
    Shot(window, vm, PageKind.Logs, $"logs-{theme}");
    Shot(window, vm, PageKind.Settings, $"settings-{theme}");
    vm.ShareExport();
    Shot(window, vm, PageKind.Settings, $"share-export-{theme}", ticks: 150);
    vm.ShowDialog(null);
    window.Close();
}
Console.WriteLine("Скриншоты: " + outDir);

void Shot(MainWindow window, MainWindowViewModel vm, PageKind page, string name, int ticks = 150)
{
    vm.Navigate(page);
    for (int i = 0; i < ticks; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame();
    frame?.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine(name);
}

static List<DiscoveredApp> DemoApps() => new()
{
    new("Discord", "Discord.exe", null),
    new("Google Chrome", "chrome.exe", null),
    new("Meta Quest Link", "OculusClient.exe", null),
    new("qBittorrent", "qbittorrent.exe", null),
    new("Steam", "steam.exe", null),
    new("Telegram Desktop", "Telegram.exe", null),
    new("Visual Studio Code", "Code.exe", null),
    new("Яндекс Браузер", "browser.exe", null),
};

static void FillTester(TesterViewModel t)
{
    var sites = Presets.LoadTestSites();
    t.DirectSummary = "Напрямую не открываются 14 из 22 сайтов.";
    t.DirectSites.Clear();
    var blocked = new HashSet<string> { "youtube.com", "i.ytimg.com", "manifest.googlevideo.com", "discord.com", "discord.gg", "discordapp.com",
        "instagram.com", "static.cdninstagram.com", "facebook.com", "rutracker.org", "hdrezka.ag", "x.com", "meta.com", "oculus.com" };
    foreach (var s in sites) t.DirectSites.Add(new SiteChip(s, !blocked.Contains(s), blocked.Contains(s) ? "таймаут" : null));
    t.PhaseText = "Шаг 2 из 2: проверяю 64 стратегий через обход…";
    t.ProgressText = "38 из 64 · 00:52";
    t.Progress = 38 / 64.0;
    t.IsRunning = true;
    var demo = new (string Args, int Ok, int Ms)[]
    {
        ("-o1 -r-5+se -a1 -At,r,s -s1+s -d3+s -r1+s -a1 -At,r,s -q1 -r1+s -a1", 22, 412),
        ("-o1 -a1 -r-5+se", 22, 455),
        ("--split 1 --disorder 3+s --mod-http=h,d --auto=torst --tlsrec 1+s", 21, 520),
        ("-d1 -s1+s -d3+s -s6+s -d9+s -s12+s -d15+s -s20+s -d25+s -s30+s -d35+s -a1", 19, 610),
        ("-q1 -r25+s -a1", 15, 498),
        ("--disorder 1 --fake -1 --ttl 8 -a1", 10, 980),
        ("-f-1 -t8 -n {sni} -s1+s -a1", 6, 1210),
    };
    t.Results.Clear();
    foreach (var d in demo)
    {
        var r = new StrategyResult { Arguments = d.Args, Success = d.Ok, Total = 22, AverageTime = TimeSpan.FromMilliseconds(d.Ms) };
        t.Results.Add(new StrategyResultItem(r, _ => { }) { Rank = t.Results.Count + 1 });
    }
    t.HasResults = true;
}

static void FillLogs()
{
    Log.Info("Программа", "ByeDPI PC 1.0.0 запущена с правами администратора");
    Log.Info("Движки", "TCP timestamps (для стратегий zapret): включены");
    Log.Info("zapret", "Запуск: winws --wf-tcp=80,443 --wf-udp=443 --filter-tcp=80,443 --hostlist=hosts.txt --dpi-desync=hostfakesplit --dpi-desync-fooling=ts,md5sig …");
    Log.Info("zapret", "windivert initialized. capture is started.");
    Log.Info("Подключение", "Подключено (zapret)");
    Log.Info("Автоподбор", "Напрямую не открываются 14 из 22: youtube.com, discord.com, instagram.com, rutracker.org …");
    Log.Info("Автоподбор", "Текущая стратегия открывает 14 из 14 — подбор не нужен");
    Log.Warn("Маршруты", "«Telegram»: внешний прокси не настроен, используется обход DPI.");
    Log.Info("sing-box", "INFO [3815032105 0ms] outbound/socks[byedpi]: outbound connection to www.youtube.com:443");
    Log.Info("sing-box", "INFO [1188042357 0ms] outbound/socks[byedpi]: outbound connection to rr3---sn-4axm-n8vs.googlevideo.com:443");
    Log.Error("sing-box", "ERROR [2410944433 5.0s] connection: open outbound connection: dial tcp 157.240.0.35:443: i/o timeout");
    Log.Info("sing-box", "INFO [4044193620 0ms] outbound/direct[direct]: outbound connection to yandex.ru:443");
}

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ByeDpiPc.sln"))) dir = dir.Parent;
    return dir?.FullName ?? Environment.CurrentDirectory;
}
