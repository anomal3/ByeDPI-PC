using Avalonia.Threading;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Routing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public sealed partial class ListsViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;
    private bool _syncing;

    public string[] RouteNames { get; } = { "Обход DPI", "Напрямую", "Внешний прокси" };

    [ObservableProperty] private string _customDomainsText = "";
    [ObservableProperty] private string _testSitesText = "";
    [ObservableProperty] private string _customInfo = "";
    [ObservableProperty] private string _sitesInfo = "";

    public ListsViewModel(AppHost host)
    {
        _host = host;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => { _timer.Stop(); Commit(); };
        Load();
    }

    public override void OnShown() => Load();

    private void Load()
    {
        _syncing = true;
        CustomDomainsText = string.Join("\n", _host.Settings.CustomDomains);
        TestSitesText = string.Join("\n", _host.Settings.TestSites);
        _syncing = false;
        UpdateInfo();
        OnPropertyChanged(nameof(CustomEnabled));
        OnPropertyChanged(nameof(CustomRouteIndex));
    }

    public bool CustomEnabled
    {
        get => _host.Settings.CustomDomainsEnabled;
        set
        {
            _host.Settings.CustomDomainsEnabled = value;
            OnPropertyChanged();
            _host.Save();
        }
    }

    public int CustomRouteIndex
    {
        get => _host.Settings.CustomDomainsRoute switch { RouteMode.Direct => 1, RouteMode.Upstream => 2, _ => 0 };
        set
        {
            _host.Settings.CustomDomainsRoute = value switch { 1 => RouteMode.Direct, 2 => RouteMode.Upstream, _ => RouteMode.ByeDpi };
            OnPropertyChanged();
            _host.Save();
        }
    }

    partial void OnCustomDomainsTextChanged(string value) => Edited();
    partial void OnTestSitesTextChanged(string value) => Edited();

    private void Edited()
    {
        if (_syncing) return;
        _timer.Stop();
        _timer.Start();
    }

    private void Commit()
    {
        _host.Settings.CustomDomains = Parse(CustomDomainsText);
        _host.Settings.TestSites = Parse(TestSitesText);
        UpdateInfo();
        _host.Save();
    }

    private void UpdateInfo()
    {
        var bad = Lines(CustomDomainsText).Count(l => RoutePlan.NormalizeDomain(l) == null);
        CustomInfo = RuleItemViewModel.Plural(_host.Settings.CustomDomains.Count, "домен", "домена", "доменов")
                     + (bad > 0 ? $" · {bad} строк не похожи на домен и пропущены" : "");
        SitesInfo = RuleItemViewModel.Plural(_host.Settings.TestSites.Count, "сайт", "сайта", "сайтов") + " для проверки";
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'));

    private static List<string> Parse(string text) =>
        Lines(text).Select(RoutePlan.NormalizeDomain).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [RelayCommand]
    private async Task ImportCustom()
    {
        var text = await Ui.ReadTextFileAsync();
        if (text == null) return;
        var merged = Parse(CustomDomainsText + "\n" + text);
        CustomDomainsText = string.Join("\n", merged);
    }

    [RelayCommand]
    private Task ExportCustom() => Ui.SaveTextFileAsync("my-domains.txt", string.Join("\n", _host.Settings.CustomDomains));

    [RelayCommand]
    private async Task ImportSites()
    {
        var text = await Ui.ReadTextFileAsync();
        if (text == null) return;
        TestSitesText = string.Join("\n", Parse(TestSitesText + "\n" + text));
    }

    [RelayCommand]
    private Task ExportSites() => Ui.SaveTextFileAsync("test-sites.txt", string.Join("\n", _host.Settings.TestSites));

    [RelayCommand]
    private void ResetSites() => TestSitesText = string.Join("\n", Presets.LoadTestSites());

    /// <summary>Добавить в сайты для проверки домены включённых правил (по одному-два на правило).</summary>
    [RelayCommand]
    private void SitesFromRules()
    {
        var extra = _host.Settings.Rules.Where(r => r.Enabled).SelectMany(r => r.Domains.Take(2));
        TestSitesText = string.Join("\n", Parse(TestSitesText + "\n" + string.Join("\n", extra)));
    }
}
