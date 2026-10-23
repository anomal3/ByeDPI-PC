using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public sealed partial class AppsViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly Action<ViewModelBase?> _showDialog;

    public ObservableCollection<RuleItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isProxyMode;
    [ObservableProperty] private bool _hasHidden;

    public AppsViewModel(AppHost host, Action<ViewModelBase?> showDialog)
    {
        _host = host;
        _showDialog = showDialog;
        Reload();
        host.SettingsChanged += UpdateHint;
        host.RulesChanged += Reload;
        UpdateHint();
    }

    [ObservableProperty] private string _processHint = "";

    private void UpdateHint()
    {
        var s = _host.Settings;
        IsProxyMode = s.Engine == EngineKind.Zapret || s.Mode == EngineMode.ProxyOnly;
        ProcessHint = s.Engine == EngineKind.Zapret
            ? "Движок zapret работает по доменам и подсетям, программы он не различает. У встроенных наборов домены уже есть; для своей программы добавьте её домены (раскройте правило). Нужны правила именно по программам — выберите в настройках движок byedpi и режим TUN."
            : "Сейчас режим «Только прокси»: работают только домены (через системный прокси в браузерах). Чтобы направить программу целиком, включите режим TUN в настройках — или укажите в самой программе SOCKS5-прокси 127.0.0.1:1080 (например, Telegram: Настройки → Продвинутые → Тип соединения).";
    }

    public override void OnShown()
    {
        UpdateHint();
        OnPropertyChanged(nameof(IsWhitelist));
        OnPropertyChanged(nameof(IsBlacklist));
        OnPropertyChanged(nameof(IsAll));
    }

    private void Reload()
    {
        Items.Clear();
        foreach (var r in _host.Settings.Rules) Items.Add(new RuleItemViewModel(r, this));
        ApplySearch();
    }

    public bool IsWhitelist
    {
        get => _host.Settings.Filter == FilterMode.Whitelist;
        set { if (value) SetFilter(FilterMode.Whitelist); }
    }

    public bool IsBlacklist
    {
        get => _host.Settings.Filter == FilterMode.Blacklist;
        set { if (value) SetFilter(FilterMode.Blacklist); }
    }

    public bool IsAll
    {
        get => _host.Settings.Filter == FilterMode.None;
        set { if (value) SetFilter(FilterMode.None); }
    }

    public string FilterHint => _host.Settings.Filter switch
    {
        FilterMode.Whitelist => "Через обход идут только отмеченные программы и сайты, остальное — напрямую.",
        FilterMode.Blacklist => "Через обход идёт всё, кроме отмеченных (они — напрямую).",
        _ => "Через обход идёт весь трафик. Маршруты «Напрямую» и «Внешний прокси» у правил продолжают работать.",
    };

    private void SetFilter(FilterMode mode)
    {
        if (_host.Settings.Filter == mode) return;
        _host.Settings.Filter = mode;
        _host.Save();
        OnPropertyChanged(nameof(IsWhitelist));
        OnPropertyChanged(nameof(IsBlacklist));
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(FilterHint));
        OnPropertyChanged(nameof(RouteColumnTitle));
    }

    public string RouteColumnTitle => _host.Settings.Filter == FilterMode.Blacklist ? "В чёрном списке отмеченные идут напрямую" : "";

    partial void OnSearchChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var q = Search.Trim();
        foreach (var i in Items)
            i.IsVisible = q.Length == 0 || i.Matches(q);
        HasHidden = Items.Any(i => !i.IsVisible);
    }

    internal void Changed() => _host.Save();

    internal void Remove(RuleItemViewModel item)
    {
        Items.Remove(item);
        _host.Settings.Rules.Remove(item.Rule);
        if (item.Rule.IsPreset && !_host.Settings.RemovedPresets.Contains(item.Rule.Id))
            _host.Settings.RemovedPresets.Add(item.Rule.Id);
        _host.Save();
    }

    internal void Add(AppRule rule, bool expand = false)
    {
        _host.Settings.Rules.Add(rule);
        var vm = new RuleItemViewModel(rule, this) { IsExpanded = expand };
        Items.Add(vm);
        _host.Save();
    }

    [RelayCommand]
    private async Task AddExecutable()
    {
        var path = await Ui.PickExecutableAsync();
        if (path == null) return;
        AddDiscovered(new[] { AppDiscovery.FromExecutable(path) });
    }

    [RelayCommand]
    private void ShowRunning() => _showDialog(new AppPickerViewModel("Запущенные программы", AppDiscovery.Running, this, () => _showDialog(null)));

    [RelayCommand]
    private void ShowInstalled() => _showDialog(new AppPickerViewModel("Установленные программы", AppDiscovery.Installed, this, () => _showDialog(null)));

    [RelayCommand]
    private void AddCustom() => Add(new AppRule { Name = "Мой набор", Icon = "web", Domains = new() { "example.com" } }, expand: true);

    [RelayCommand]
    private void RestorePresets()
    {
        _host.Settings.RemovedPresets.Clear();
        Presets.EnsureDefaults(_host.Settings);
        _host.Save();
        Reload();
    }

    internal void AddDiscovered(IEnumerable<DiscoveredApp> apps)
    {
        foreach (var app in apps)
        {
            var existing = _host.Settings.Rules.FirstOrDefault(r => r.Processes.Contains(app.ProcessName, StringComparer.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Enabled = true;
                Items.FirstOrDefault(i => i.Rule == existing)?.Refresh();
                continue;
            }
            Add(new AppRule
            {
                Name = app.Name,
                Icon = "app",
                IconPath = app.Path,
                Description = app.Path ?? app.ProcessName,
                Processes = new() { app.ProcessName },
            });
        }
        _host.Save();
    }
}

public sealed partial class RuleItemViewModel : ObservableObject
{
    private readonly AppsViewModel _owner;
    private readonly DispatcherTimer _textTimer;

    public AppRule Rule { get; }
    public string[] RouteNames { get; } = { "Обход DPI", "Напрямую", "Внешний прокси" };

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _processesText = "";
    [ObservableProperty] private string _domainsText = "";
    [ObservableProperty] private string _cidrsText = "";

    public StreamGeometry? Icon { get; }
    public IBrush IconBrush { get; }
    public IBrush IconSoftBrush { get; }
    public Bitmap? Bitmap { get; }
    public bool HasBitmap => Bitmap != null;

    public RuleItemViewModel(AppRule rule, AppsViewModel owner)
    {
        Rule = rule;
        _owner = owner;
        var visual = RuleVisual.For(rule);
        Icon = visual.Icon;
        IconBrush = visual.Brush;
        IconSoftBrush = visual.SoftBrush;
        Bitmap = visual.Bitmap;
        _textTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _textTimer.Tick += (_, _) => { _textTimer.Stop(); CommitTexts(); };
        Refresh();
    }

    public string Name
    {
        get => Rule.Name;
        set
        {
            if (Rule.Name == value) return;
            Rule.Name = value;
            OnPropertyChanged();
            _owner.Changed();
        }
    }

    public string? Description => Rule.Description;
    public string? Hint => Rule.Hint;
    public bool HasHint => !string.IsNullOrEmpty(Rule.Hint);
    public bool IsPreset => Rule.IsPreset;
    public bool ShowUpstreamWarning => Rule.Route == RouteMode.Upstream && !(AppHost.Current?.Settings.UpstreamEnabled ?? false);

    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value) return;
            Rule.Enabled = value;
            OnPropertyChanged();
            _owner.Changed();
        }
    }

    public int RouteIndex
    {
        get => Rule.Route switch { RouteMode.Direct => 1, RouteMode.Upstream => 2, _ => 0 };
        set
        {
            var route = value switch { 1 => RouteMode.Direct, 2 => RouteMode.Upstream, _ => RouteMode.ByeDpi };
            if (Rule.Route == route) return;
            Rule.Route = route;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowUpstreamWarning));
            _owner.Changed();
        }
    }

    public void Refresh()
    {
        _syncing = true;
        ProcessesText = string.Join("\n", Rule.Processes);
        DomainsText = string.Join("\n", Rule.Domains);
        CidrsText = string.Join("\n", Rule.IpCidrs);
        _syncing = false;
        UpdateSummary();
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(RouteIndex));
    }

    private bool _syncing;

    partial void OnProcessesTextChanged(string value) => TextEdited();
    partial void OnDomainsTextChanged(string value) => TextEdited();
    partial void OnCidrsTextChanged(string value) => TextEdited();

    private void TextEdited()
    {
        if (_syncing) return;
        _textTimer.Stop();
        _textTimer.Start();
    }

    private void CommitTexts()
    {
        Rule.Processes = SplitLines(ProcessesText);
        Rule.Domains = SplitLines(DomainsText);
        Rule.IpCidrs = SplitLines(CidrsText);
        UpdateSummary();
        _owner.Changed();
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void UpdateSummary()
    {
        var parts = new List<string>();
        if (Rule.Processes.Count > 0) parts.Add(Plural(Rule.Processes.Count, "программа", "программы", "программ"));
        if (Rule.Domains.Count > 0) parts.Add(Plural(Rule.Domains.Count, "домен", "домена", "доменов"));
        if (Rule.IpCidrs.Count > 0) parts.Add(Plural(Rule.IpCidrs.Count, "подсеть", "подсети", "подсетей"));
        Summary = parts.Count == 0 ? "пусто — добавьте программы или домены" : string.Join(" · ", parts);
    }

    internal static string Plural(int n, string one, string few, string many)
    {
        var m10 = n % 10;
        var m100 = n % 100;
        var word = m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        return $"{n} {word}";
    }

    public bool Matches(string q) =>
        Rule.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Rule.Processes.Any(p => p.Contains(q, StringComparison.OrdinalIgnoreCase))
        || Rule.Domains.Any(d => d.Contains(q, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Delete()
    {
        _textTimer.Stop();
        _owner.Remove(this);
    }

    [RelayCommand]
    private void Reset()
    {
        if (Presets.FindPreset(Rule.Id) is not { } p) return;
        Rule.Processes = p.Processes;
        Rule.Domains = p.Domains;
        Rule.IpCidrs = p.IpCidrs;
        Refresh();
        _owner.Changed();
    }
}

public sealed partial class AppPickerViewModel : ViewModelBase
{
    private readonly AppsViewModel _owner;
    private readonly Action _close;
    private List<PickerItem> _all = new();

    public string Title { get; }
    public ObservableCollection<PickerItem> Items { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _selectedCount;

    public AppPickerViewModel(string title, Func<List<DiscoveredApp>> source, AppsViewModel owner, Action close)
    {
        Title = title;
        _owner = owner;
        _close = close;
        _ = LoadAsync(source);
    }

    private async Task LoadAsync(Func<List<DiscoveredApp>> source)
    {
        var apps = await Task.Run(source);
        _all = apps.Select(a => new PickerItem(a, this)).ToList();
        IsLoading = false;
        Filter();
        // Иконки .exe грузим после показа списка, чтобы окно не подвисало.
        foreach (var item in _all)
        {
            await Task.Yield();
            item.LoadIcon();
        }
    }

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        Items.Clear();
        var q = Search.Trim();
        foreach (var i in _all.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Process.Contains(q, StringComparison.OrdinalIgnoreCase)))
            Items.Add(i);
    }

    internal void UpdateCount() => SelectedCount = _all.Count(i => i.IsChecked);

    [RelayCommand]
    private void AddSelected()
    {
        _owner.AddDiscovered(_all.Where(i => i.IsChecked).Select(i => i.App));
        _close();
    }

    [RelayCommand]
    private void Close() => _close();
}

public sealed partial class PickerItem : ObservableObject
{
    private readonly AppPickerViewModel _owner;

    public PickerItem(DiscoveredApp app, AppPickerViewModel owner)
    {
        App = app;
        _owner = owner;
    }

    public DiscoveredApp App { get; }
    public string Name => App.Name;
    public string Process => App.ProcessName;
    public string? Path => App.Path;

    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private bool _isChecked;

    public bool HasIcon => Icon != null;
    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));
    partial void OnIsCheckedChanged(bool value) => _owner.UpdateCount();

    public void LoadIcon() => Icon = Ui.ExeIcon(App.Path);
}
