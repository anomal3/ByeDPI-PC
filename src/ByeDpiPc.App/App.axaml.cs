using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ByeDpiPc.App.Services;
using ByeDpiPc.App.ViewModels;
using ByeDpiPc.App.Views;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;

namespace ByeDpiPc.App;

public partial class App : Application
{
    private AppHost? _host;
    private MainWindow? _window;
    private MainWindowViewModel? _vm;
    private TrayIcon? _tray;
    private bool _exiting;

    /// <summary>Режим скриншотов (tools/Screenshots): без трея, движков и сохранения настроек.</summary>
    public static bool DemoMode { get; set; }
    public static AppSettings? DemoSettings { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var store = new SettingsStore();
        var settings = DemoMode ? DemoSettings ?? new AppSettings() : store.Load();
        if (DemoMode) Presets.EnsureDefaults(settings);
        _host = new AppHost(store, settings, DemoMode);
        _vm = new MainWindowViewModel(_host);
        ActualThemeVariantChanged += (_, _) => _vm.OnSystemThemeChanged();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _window = new MainWindow { DataContext = _vm };
            Ui.Root = _window;
            desktop.MainWindow = _window;

            if (!DemoMode)
            {
                Log.Info("Программа", $"ByeDPI PC {_vm.Version} запущена{(Elevation.IsElevated ? " с правами администратора" : "")}");
                SetupTray();
                SingleInstance.ActivationRequested += () => Dispatcher.UIThread.Post(ShowWindow);
                _host.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(AppHost.State) or nameof(AppHost.IsTuning) or nameof(AppHost.StrategyBroken)) UpdateTray();
                };
                _host.StrategiesChanged += BuildTrayMenu;

                _host.ExitApp = ExitAsync;
                if (settings.CheckUpdates)
                    DispatcherTimer.RunOnce(() => _ = _host.CheckUpdatesAsync(manual: false), TimeSpan.FromSeconds(8));

                var hidden = Program.StartInTray && settings.StartMinimized;
                if (!hidden) _window.Show();
                if (settings.AutoConnect) Dispatcher.UIThread.Post(async () => await _host.ConnectAsync(), DispatcherPriority.Background);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray()
    {
        var icons = TrayIcon.GetIcons(this);
        _tray = icons?.FirstOrDefault();
        if (_tray == null) return;
        _tray.Clicked += (_, _) => ShowWindow();
        BuildTrayMenu();
        UpdateTray();
    }

    private void BuildTrayMenu()
    {
        if (_tray == null || _host == null) return;
        // Меню обновляем на месте: повторное присвоение нового NativeMenu роняет Avalonia на macOS
        // («The menu being updated does not match»).
        var menu = _tray.Menu ?? new NativeMenu();
        menu.Items.Clear();

        var toggle = new NativeMenuItem(_host.Connection.IsConnected ? "Отключить" : "Подключить");
        toggle.Click += async (_, _) => await _host.ToggleAsync();
        menu.Add(toggle);

        var strategies = new NativeMenu();
        foreach (var s in _host.Settings.EngineStrategies())
        {
            var item = new NativeMenuItem(s.Name)
            {
                ToggleType = NativeMenuItemToggleType.Radio,
                IsChecked = s.Name == _host.Settings.GetActiveStrategy()?.Name,
            };
            var name = s.Name;
            item.Click += (_, _) => _host.SetActiveStrategy(name);
            strategies.Add(item);
        }
        menu.Add(new NativeMenuItem("Стратегия") { Menu = strategies });

        var tune = new NativeMenuItem("Подобрать стратегию");
        tune.Click += (_, _) => _ = _host.RunAutoTuneAsync(force: true);
        menu.Add(tune);

        menu.Add(new NativeMenuItemSeparator());
        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => ShowWindow();
        menu.Add(open);
        var logs = new NativeMenuItem("Логи");
        logs.Click += (_, _) =>
        {
            ShowWindow();
            _vm?.Navigate(PageKind.Logs);
        };
        menu.Add(logs);
        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("Выход");
        exit.Click += async (_, _) => await ExitAsync();
        menu.Add(exit);

        if (!ReferenceEquals(_tray.Menu, menu)) _tray.Menu = menu;
    }

    private void UpdateTray()
    {
        if (_tray == null || _host == null) return;
        var (icon, text) = _host.State switch
        {
            ConnectionState.Connected when _host.IsTuning => ("tray-busy", "ByeDPI PC — подбор стратегии…"),
            ConnectionState.Connected when _host.StrategyBroken => ("tray-error", "ByeDPI PC — обход перестал работать, подберите стратегию"),
            ConnectionState.Connected => ("tray-on", "ByeDPI PC — подключено"),
            ConnectionState.Connecting or ConnectionState.Disconnecting => ("tray-busy", "ByeDPI PC — подключение…"),
            ConnectionState.Error => ("tray-error", "ByeDPI PC — ошибка: " + _host.Connection.ErrorMessage),
            _ => ("tray-off", "ByeDPI PC — отключено"),
        };
        try
        {
            _tray.Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://ByeDPI-PC/Assets/{icon}.ico")));
        }
        catch (Exception ex)
        {
            Log.Debug("Трей", ex.Message);
        }
        _tray.ToolTipText = text.Length > 120 ? text[..120] : text;
        BuildTrayMenu();

        // Ошибка, пока окно скрыто, — показываем окно, чтобы пользователь её увидел.
        if (_host.State == ConnectionState.Error && _window is { IsVisible: false }) ShowWindow();
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_host != null)
        {
            _host.CancelTune();
            await _host.Connection.DisposeAsync();
        }
        if (_tray != null) _tray.IsVisible = false;
        if (_window != null) _window.AllowClose = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
