using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Platform;
using ByeDpiPc.Core.Routing;
using ByeDpiPc.Core.Testing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly Action<ThemeMode> _applyTheme;
    private AppSettings S => _host.Settings;

    public string[] DnsNames { get; } = { "Cloudflare 1.1.1.1 (DoH)", "Google 8.8.8.8 (DoH)", "Quad9 9.9.9.9 (DoH)", "Системный DNS", "Свой адрес" };

    [ObservableProperty] private string? _portWarning;
    [ObservableProperty] private string? _autostartMessage;
    [ObservableProperty] private bool _autostartBusy;
    [ObservableProperty] private string? _upstreamCheck;
    [ObservableProperty] private bool _upstreamCheckOk;
    [ObservableProperty] private bool _isCheckingUpstream;
    [ObservableProperty] private bool _showAdvanced;

    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string ConfigDir => AppPaths.ConfigDir;
    public string EngineInfo =>
        $"ciadpi: {(CiadpiEngine.FindExecutable() ?? "не найден")}\nsing-box: {(TunController.FindExecutable() ?? "не найден")}" +
        (OperatingSystem.IsWindows() ? $"\nzapret: {(ZapretController.FindExecutable() ?? "не найден")}" : "");

    private readonly Action _shareExport;
    private readonly Action _shareImport;

    public SettingsViewModel(AppHost host, Action<ThemeMode> applyTheme, Action shareExport, Action shareImport)
    {
        _host = host;
        _applyTheme = applyTheme;
        _shareExport = shareExport;
        _shareImport = shareImport;
        ShowAdvanced = host.Settings.UpstreamEnabled;
        _host.VkRelay.PropertyChanged += OnVkRelayChanged;
        if (!string.IsNullOrEmpty(_host.VkRelay.PairingCode))
            VkRelayQr = QrImage.Create(_host.VkRelay.PairingCode);
        _ = RefreshAutostartAsync();
    }

    private async Task RefreshAutostartAsync()
    {
        if (_host.DemoMode) return;
        var enabled = await Task.Run(Autostart.IsEnabled);
        if (enabled != S.AutoStart)
        {
            S.AutoStart = enabled;
            OnPropertyChanged(nameof(AutoStart));
        }
    }

    public void RefreshTheme()
    {
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
    }

    private void Set(Action change, bool apply = true, params string[] props)
    {
        change();
        _host.Save(apply);
        foreach (var p in props) OnPropertyChanged(p);
    }

    // Тема
    public bool ThemeSystem { get => S.Theme == ThemeMode.System; set { if (value) SetTheme(ThemeMode.System); } }
    public bool ThemeLight { get => S.Theme == ThemeMode.Light; set { if (value) SetTheme(ThemeMode.Light); } }
    public bool ThemeDark { get => S.Theme == ThemeMode.Dark; set { if (value) SetTheme(ThemeMode.Dark); } }

    private void SetTheme(ThemeMode mode)
    {
        if (S.Theme == mode) return;
        S.Theme = mode;
        _host.Save(apply: false);
        _applyTheme(mode);
        RefreshTheme();
    }

    public AppHost Host => _host;
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool ZapretMissing => OperatingSystem.IsWindows() && !ZapretController.IsAvailable;

    // Движок
    public bool EngineZapret
    {
        get => S.Engine == EngineKind.Zapret;
        set { if (value && S.Engine != EngineKind.Zapret) SetEngine(EngineKind.Zapret); }
    }

    public bool EngineByeDpi
    {
        get => S.Engine == EngineKind.ByeDpi;
        set { if (value && S.Engine != EngineKind.ByeDpi) SetEngine(EngineKind.ByeDpi); }
    }

    private void SetEngine(EngineKind engine)
    {
        S.Engine = engine;
        _host.StrategyBroken = false;
        _host.Save();
        _host.NotifyStrategiesChanged();
        OnPropertyChanged(nameof(EngineZapret));
        OnPropertyChanged(nameof(EngineByeDpi));
    }

    // Обновления
    public bool CheckUpdates { get => S.CheckUpdates; set => Set(() => S.CheckUpdates = value, false, nameof(CheckUpdates)); }

    [RelayCommand]
    private Task CheckUpdatesNow() => _host.CheckUpdatesAsync(manual: true);

    [RelayCommand]
    private Task InstallUpdate() => _host.InstallUpdateAsync(_host.ExitApp ?? (() => Task.CompletedTask));

    // Режим
    public bool ModeTun
    {
        get => S.Mode == EngineMode.Tun;
        set { if (value && S.Mode != EngineMode.Tun) Set(() => S.Mode = EngineMode.Tun, true, nameof(ModeTun), nameof(ModeProxy)); }
    }

    public bool ModeProxy
    {
        get => S.Mode == EngineMode.ProxyOnly;
        set { if (value && S.Mode != EngineMode.ProxyOnly) Set(() => S.Mode = EngineMode.ProxyOnly, true, nameof(ModeTun), nameof(ModeProxy)); }
    }

    public bool SetSystemProxy { get => S.SetSystemProxy; set => Set(() => S.SetSystemProxy = value, true, nameof(SetSystemProxy)); }
    public bool UsePac { get => S.UsePac; set => Set(() => S.UsePac = value, true, nameof(UsePac)); }

    // Сеть
    public decimal SocksPort
    {
        get => S.SocksPort;
        set
        {
            var port = (int)value;
            if (port is < 1024 or > 65535 || port == S.SocksPort) return;
            PortWarning = !_host.Connection.IsConnected && NetUtil.IsPortBusy(port) ? $"Порт {port} сейчас занят другой программой." : null;
            Set(() => S.SocksPort = port, true, nameof(SocksPort));
        }
    }

    public int DnsIndex
    {
        get => (int)S.Dns;
        set => Set(() => S.Dns = (DnsPreset)value, true, nameof(DnsIndex), nameof(IsCustomDns));
    }

    public bool IsCustomDns => S.Dns == DnsPreset.Custom;
    public string CustomDns { get => S.CustomDns; set => Set(() => S.CustomDns = value, true, nameof(CustomDns)); }
    public bool Ipv6 { get => S.Ipv6; set => Set(() => S.Ipv6 = value, true, nameof(Ipv6)); }
    public bool BlockQuic { get => S.BlockQuic; set => Set(() => S.BlockQuic = value, true, nameof(BlockQuic)); }
    public string FakeSni { get => S.FakeSni; set => Set(() => S.FakeSni = value, true, nameof(FakeSni)); }

    // Запуск
    public bool AutoStart
    {
        get => S.AutoStart;
        set
        {
            if (S.AutoStart == value || AutostartBusy) return;
            _ = ChangeAutostartAsync(value);
        }
    }

    private async Task ChangeAutostartAsync(bool enable)
    {
        AutostartBusy = true;
        AutostartMessage = enable ? "Создаю задачу автозапуска — подтвердите запрос прав администратора…" : null;
        try
        {
            if (enable)
            {
                AutostartMessage = await Autostart.EnableAsync();
            }
            else
            {
                await Autostart.DisableAsync();
            }
            S.AutoStart = await Task.Run(Autostart.IsEnabled);
            _host.Save(apply: false);
        }
        catch (Exception ex)
        {
            AutostartMessage = "Не удалось изменить автозапуск: " + ex.Message;
        }
        finally
        {
            AutostartBusy = false;
            OnPropertyChanged(nameof(AutoStart));
        }
    }

    public bool StartMinimized { get => S.StartMinimized; set => Set(() => S.StartMinimized = value, false, nameof(StartMinimized)); }
    public bool AutoConnect { get => S.AutoConnect; set => Set(() => S.AutoConnect = value, false, nameof(AutoConnect)); }
    public bool CloseToTray { get => S.CloseToTray; set => Set(() => S.CloseToTray = value, false, nameof(CloseToTray)); }
    public bool AutoTune { get => S.AutoTune; set => Set(() => S.AutoTune = value, false, nameof(AutoTune)); }

    // Внешний прокси
    public bool UpstreamEnabled { get => S.UpstreamEnabled; set => Set(() => S.UpstreamEnabled = value, true, nameof(UpstreamEnabled)); }
    public string UpstreamUri { get => S.UpstreamUri; set => Set(() => S.UpstreamUri = value.Trim(), true, nameof(UpstreamUri), nameof(UpstreamDescription)); }

    [RelayCommand]
    private async Task CheckUpstream()
    {
        UpstreamCheck = null;
        if (!UpstreamProxy.TryParse(S.UpstreamUri, out var proxy, out var error) || proxy == null)
        {
            UpstreamCheckOk = false;
            UpstreamCheck = "Ошибка в адресе: " + error;
            return;
        }
        IsCheckingUpstream = true;
        UpstreamCheck = "Проверяю: " + proxy.Describe() + "…";
        try
        {
            var r = await Task.Run(() => UpstreamChecker.CheckAsync(proxy, new[] { "https://telegram.org" }));
            UpstreamCheckOk = r[0].Ok;
            UpstreamCheck = r[0].Ok
                ? $"Прокси работает: telegram.org открылся за {r[0].Elapsed.TotalMilliseconds:F0} мс."
                : "Через прокси telegram.org не открылся: " + r[0].Error;
        }
        catch (Exception ex)
        {
            UpstreamCheckOk = false;
            UpstreamCheck = ex is UserFacingException u ? u.Message + " " + u.Advice : ex.Message;
        }
        finally
        {
            IsCheckingUpstream = false;
        }
    }

    // Точка выхода для телефона (звонки VK)
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _vkRelayQr;

    public RelayPcHost VkRelay => _host.VkRelay;

    public bool VkRelayEnabled
    {
        get => S.VkRelayEnabled;
        set
        {
            if (S.VkRelayEnabled == value) return;
            S.VkRelayEnabled = value;
            _host.Save(apply: false);
            if (value) _host.VkRelay.Start(); else _host.VkRelay.Stop();
            OnPropertyChanged(nameof(VkRelayEnabled));
        }
    }

    public int VkRelayCount
    {
        get => S.VkRelayCount;
        set => Set(() => S.VkRelayCount = Math.Clamp(value, 1, 8), false, nameof(VkRelayCount));
    }

    public string VkRelayCallLink
    {
        get => S.VkRelayCallLink;
        set { _host.VkRelay.SetCallLink(value.Trim()); OnPropertyChanged(nameof(VkRelayCallLink)); }
    }

    [RelayCommand]
    private void ResetVkRelay() => _host.VkRelay.Reset();

    void OnVkRelayChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RelayPcHost.PairingCode)) return;
        var code = _host.VkRelay.PairingCode;
        VkRelayQr = string.IsNullOrEmpty(code) ? null : QrImage.Create(code);
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand]
    private void Export() => _shareExport();

    [RelayCommand]
    private void Import() => _shareImport();

    public string UpstreamDescription =>
        Core.Routing.UpstreamProxy.TryParse(S.UpstreamUri, out var p, out var err) ? p!.Describe() : string.IsNullOrWhiteSpace(S.UpstreamUri) ? "" : "Ошибка: " + err;

    [RelayCommand]
    private void OpenConfigDir() => Ui.Open(AppPaths.ConfigDir);

    [RelayCommand]
    private void OpenUrl(string url) => Ui.Open(url);
}
