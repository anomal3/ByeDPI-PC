using System.Collections.ObjectModel;
using System.ComponentModel;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Testing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly Action<PageKind> _navigate;
    private bool _syncing;

    public ObservableCollection<string> StrategyNames { get; } = new();
    public ObservableCollection<RuleChip> ActiveRules { get; } = new();

    [ObservableProperty] private string? _selectedStrategy;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusSubtitle = "";
    [ObservableProperty] private string _buttonHint = "";
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isSpinning;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _errorAdvice;
    [ObservableProperty] private bool _showAdvice;
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _modeHint = "";
    [ObservableProperty] private string _proxyAddress = "";
    [ObservableProperty] private string _rulesText = "";
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private bool _isZapret;
    [ObservableProperty] private string _secondTitle = "";
    [ObservableProperty] private string _secondValue = "";
    [ObservableProperty] private string _secondHint = "";

    public string UpdateTitle => _host.Update is { } u ? $"Доступна новая версия {u.Version}" : "";

    public AppHost Host => _host;

    public HomeViewModel(AppHost host, Action<PageKind> navigate)
    {
        _host = host;
        _navigate = navigate;
        host.PropertyChanged += OnHostChanged;
        host.SettingsChanged += Refresh;
        host.StrategiesChanged += RefreshStrategies;
        RefreshStrategies();
        Refresh();
        UpdateState();
    }

    public override void OnShown() => Refresh();

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppHost.State) or nameof(AppHost.IsTuning)) UpdateState();
        if (e.PropertyName is nameof(AppHost.Update)) OnPropertyChanged(nameof(UpdateTitle));
        if (e.PropertyName == nameof(AppHost.TuneText)) OnPropertyChanged(nameof(TuneSummary));
    }

    /// <summary>Строка под индикатором автоподбора.</summary>
    public string TuneSummary =>
        _host.IsTuning || !string.IsNullOrEmpty(_host.TuneText) ? _host.TuneText
        : string.IsNullOrEmpty(_host.Settings.LastTuneSummary) ? "Проверка ещё не проводилась."
        : $"{_host.Settings.LastTuneSummary} ({_host.Settings.LastTuned:dd.MM HH:mm})";

    private void UpdateState()
    {
        var state = _host.State;
        var c = _host.Connection;
        IsOn = state == ConnectionState.Connected;
        IsBusy = state is ConnectionState.Connecting or ConnectionState.Disconnecting;
        IsError = state == ConnectionState.Error;
        IsSpinning = IsBusy || _host.IsTuning;
        var mode = c.IsConnected ? c.ModeTitle
            : _host.Settings.Engine == EngineKind.Zapret ? "zapret"
            : _host.Settings.Mode == EngineMode.Tun ? "TUN" : "Прокси";
        (StatusTitle, StatusSubtitle) = state switch
        {
            ConnectionState.Connected when _host.IsTuning => ($"Подключено ({mode})", "Идёт проверка обхода…"),
            ConnectionState.Connected => ($"Подключено ({mode})", "Выбранные приложения и сайты идут через обход DPI"),
            ConnectionState.Connecting => ("Подключение…",
                _host.Settings.Engine == EngineKind.Zapret ? "Запуск zapret (winws)"
                : _host.Settings.Mode == EngineMode.Tun ? "Запуск ciadpi и sing-box" : "Запуск ciadpi"),
            ConnectionState.Disconnecting => ("Отключение…", "Останавливаю движки и убираю настройки"),
            ConnectionState.Error => ("Ошибка", c.ErrorMessage ?? "Не удалось подключиться"),
            _ => ("Отключено", "Нажмите кнопку, чтобы включить обход"),
        };
        ButtonHint = IsOn ? "Отключить" : IsBusy ? "Подождите…" : "Подключить";
        ErrorMessage = IsError ? c.ErrorMessage : null;
        ErrorAdvice = IsError ? c.ErrorAdvice : null;
        Warning = IsOn && c.Warnings.Count > 0 ? string.Join("\n", c.Warnings) : null;
        ShowAdvice = false;
        OnPropertyChanged(nameof(TuneSummary));
    }

    private void RefreshStrategies()
    {
        _syncing = true;
        StrategyNames.Clear();
        foreach (var s in _host.Settings.EngineStrategies()) StrategyNames.Add(s.Name);
        SelectedStrategy = _host.Settings.GetActiveStrategy()?.Name;
        _syncing = false;
    }

    private void Refresh()
    {
        var s = _host.Settings;
        IsZapret = s.Engine == EngineKind.Zapret;
        ModeText = IsZapret ? "zapret — пакеты напрямую" : s.Mode == EngineMode.Tun ? "byedpi, TUN — все программы" : "byedpi, только прокси";
        ModeHint = IsZapret ? "Правила по доменам и подсетям"
            : s.Mode == EngineMode.Tun ? "Правила по программам и доменам"
            : !s.SetSystemProxy ? "Укажите прокси в нужных программах"
            : s.UsePac ? "Системный прокси + PAC для браузеров" : "Системный SOCKS-прокси";
        ProxyAddress = $"127.0.0.1:{s.SocksPort}";
        SecondTitle = IsZapret ? "Внешний прокси" : "Локальный прокси";
        SecondValue = IsZapret ? (s.UpstreamEnabled && s.UpstreamUri.Length > 0 ? "включён" : "не используется") : ProxyAddress;
        SecondHint = IsZapret ? "Для сервисов, заблокированных по IP" : "SOCKS5 · только этот компьютер";
        FilterText = s.Filter switch
        {
            FilterMode.Whitelist => "Через обход — только выбранное",
            FilterMode.Blacklist => "Через обход — всё, кроме выбранного",
            _ => "Через обход — весь трафик",
        };
        var enabled = s.Rules.Where(r => r.Enabled).ToList();
        RulesText = $"{enabled.Count} из {s.Rules.Count}";
        ActiveRules.Clear();
        foreach (var r in enabled.Take(9)) ActiveRules.Add(new RuleChip(r));
        if (SelectedStrategy != s.GetActiveStrategy()?.Name) RefreshStrategies();
        OnPropertyChanged(nameof(TuneSummary));
    }

    partial void OnSelectedStrategyChanged(string? value)
    {
        if (_syncing || value == null) return;
        _host.SetActiveStrategy(value);
    }

    [RelayCommand]
    private Task Toggle() => _host.ToggleAsync();

    [RelayCommand]
    private Task TuneNow() => _host.RunAutoTuneAsync(force: false);

    [RelayCommand]
    private Task SearchNow() => _host.RunAutoTuneAsync(force: true);

    [RelayCommand]
    private void CancelTune() => _host.CancelTune();

    [RelayCommand]
    private void ToggleAdvice() => ShowAdvice = !ShowAdvice;

    [RelayCommand]
    private Task InstallUpdate() => _host.InstallUpdateAsync(_host.ExitApp ?? (() => Task.CompletedTask));

    [RelayCommand]
    private void OpenRelease()
    {
        if (_host.Update is { } u) Ui.Open(u.HtmlUrl);
    }

    [RelayCommand]
    private void DismissBroken() => _host.StrategyBroken = false;

    [RelayCommand]
    private void Go(PageKind page) => _navigate(page);
}

/// <summary>Значок включённого правила на главной.</summary>
public sealed class RuleChip
{
    public RuleChip(AppRule rule)
    {
        Name = rule.Name;
        var visual = RuleVisual.For(rule);
        Icon = visual.Icon;
        Color = visual.Brush;
        Bitmap = visual.Bitmap;
        RouteText = rule.Route switch { RouteMode.Direct => "напрямую", RouteMode.Upstream => "внешний прокси", _ => "обход DPI" };
    }

    public string Name { get; }
    public Avalonia.Media.StreamGeometry? Icon { get; }
    public Avalonia.Media.IBrush Color { get; }
    public Avalonia.Media.Imaging.Bitmap? Bitmap { get; }
    public bool HasBitmap => Bitmap != null;
    public string RouteText { get; }
    public string Tip => $"{Name}: {RouteText}";
}
