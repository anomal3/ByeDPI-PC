using System.Collections.ObjectModel;
using System.Diagnostics;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Testing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public sealed partial class TesterViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private CancellationTokenSource? _cts;

    public ObservableCollection<SiteChip> DirectSites { get; } = new();
    public ObservableCollection<StrategyResultItem> Results { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string? _directSummary;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string? _bestText;
    [ObservableProperty] private bool _includeByeByeDpiSet = true;

    public AppHost Host => _host;

    public TesterViewModel(AppHost host) => _host = host;

    public int Timeout
    {
        get => _host.Settings.TestTimeoutSec;
        set { _host.Settings.TestTimeoutSec = Math.Clamp(value, 1, 30); OnPropertyChanged(); _host.Save(apply: false); }
    }

    public int Parallel
    {
        get => _host.Settings.TestParallel;
        set { _host.Settings.TestParallel = Math.Clamp(value, 1, 16); OnPropertyChanged(); _host.Save(apply: false); }
    }

    public string SitesInfo => RuleItemViewModel.Plural(_host.Settings.TestSites.Count, "сайт", "сайта", "сайтов") + " из «Списки → Сайты для проверки»";

    public override void OnShown() => OnPropertyChanged(nameof(SitesInfo));

    private List<string> Candidates()
    {
        var list = _host.Settings.Strategies.Select(s => s.Arguments).ToList();
        list.AddRange(Presets.DefaultStrategies.Select(s => s.Arguments));
        if (IncludeByeByeDpiSet) list.AddRange(Presets.LoadTestStrategies());
        return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsRunning) return;
        var sites = _host.Settings.TestSites.ToList();
        if (sites.Count == 0)
        {
            PhaseText = "Список сайтов для проверки пуст — заполните его на вкладке «Списки».";
            return;
        }
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Results.Clear();
        DirectSites.Clear();
        HasResults = false;
        BestText = null;
        DirectSummary = null;
        Progress = 0;
        var sw = Stopwatch.StartNew();
        var tester = new StrategyTester
        {
            Parallel = _host.Settings.TestParallel,
            Timeout = TimeSpan.FromSeconds(_host.Settings.TestTimeoutSec),
            FakeSni = _host.Settings.FakeSni,
        };
        try
        {
            PhaseText = "Шаг 1 из 2: проверяю сайты напрямую, без обхода…";
            ProgressText = "";
            var direct = await Task.Run(() => tester.CheckDirectAsync(sites, _cts.Token));
            foreach (var d in direct) DirectSites.Add(new SiteChip(d.Site, d.Ok, d.Error));
            var blocked = direct.Count(d => !d.Ok);
            DirectSummary = blocked == 0
                ? "Напрямую открываются все сайты — блокировок не видно (если включён VPN, выключите его для честной проверки)."
                : $"Напрямую не открываются {blocked} из {direct.Count} сайтов.";

            var candidates = Candidates();
            PhaseText = $"Шаг 2 из 2: проверяю {candidates.Count} стратегий через обход…";
            var progress = new Progress<TestProgress>(p =>
            {
                Progress = (double)p.Done / p.Total;
                ProgressText = $"{p.Done} из {p.Total} · {sw.Elapsed:mm\\:ss}";
                if (p.Finished != null) Insert(p.Finished);
            });
            await Task.Run(() => tester.RunAsync(candidates, sites, progress, _cts.Token));
            PhaseText = $"Готово за {sw.Elapsed:mm\\:ss}.";
            var best = Results.FirstOrDefault();
            BestText = best == null || best.Result.Success == 0
                ? "Ни одна стратегия не помогла. Возможно, сайты заблокированы по IP — тогда нужен внешний прокси."
                : $"Лучшая стратегия открывает {best.Result.Success} из {best.Result.Total} сайтов.";
        }
        catch (OperationCanceledException)
        {
            PhaseText = "Подбор остановлен.";
        }
        catch (Exception ex)
        {
            PhaseText = "Ошибка: " + ex.Message;
            Log.Error("Подбор", ex.ToString());
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Insert(StrategyResult r)
    {
        var item = new StrategyResultItem(r, Apply);
        int i = 0;
        while (i < Results.Count && (Results[i].Result.Success > r.Success ||
               (Results[i].Result.Success == r.Success && Results[i].Result.AverageTime <= r.AverageTime))) i++;
        Results.Insert(i, item);
        for (int k = 0; k < Results.Count; k++) Results[k].Rank = k + 1;
        HasResults = true;
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void ApplyBest()
    {
        if (Results.FirstOrDefault() is { } best) Apply(best);
    }

    private void Apply(StrategyResultItem item)
    {
        var s = _host.Settings;
        var existing = s.Strategies.FirstOrDefault(p => p.Arguments == item.Result.Arguments);
        if (existing == null)
        {
            existing = s.Strategies.FirstOrDefault(p => p.Name == Presets.AutoProfileName);
            if (existing == null)
            {
                existing = new StrategyProfile(Presets.AutoProfileName, item.Result.Arguments);
                s.Strategies.Insert(0, existing);
            }
            existing.Arguments = item.Result.Arguments;
        }
        s.ActiveStrategy = existing.Name;
        s.LastTuned = DateTime.Now;
        s.LastTuneSummary = $"Выбрана в «Подборе»: {item.Result.Success} из {item.Result.Total} сайтов";
        _host.Save();
        _host.NotifyStrategiesChanged();
        foreach (var r in Results) r.IsApplied = r == item;
    }
}

public sealed record SiteChip(string Site, bool Ok, string? Error)
{
    public string Tip => Ok ? $"{Site}: открывается" : $"{Site}: {Error ?? "не открывается"}";
}

public sealed partial class StrategyResultItem : ObservableObject
{
    private readonly Action<StrategyResultItem> _apply;

    public StrategyResultItem(StrategyResult result, Action<StrategyResultItem> apply)
    {
        Result = result;
        _apply = apply;
    }

    public StrategyResult Result { get; }
    public string Arguments => Result.Arguments;
    public double Percent => Result.Percent;
    public string SuccessText => Result.Error != null ? "ошибка" : $"{Result.Success}/{Result.Total}";
    public string TimeText => Result.Success > 0 ? $"{Result.AverageTime.TotalMilliseconds:F0} мс" : "—";
    public string? Error => Result.Error;
    public string Failed => string.Join(", ", Result.Sites.Where(s => !s.Ok).Select(s => s.Site));
    public string Tip => Result.Error ?? (Failed.Length > 0 ? "Не открылись: " + Failed : "Открылись все сайты");
    public bool IsGood => Result.Percent >= 80;
    public bool IsMid => Result.Percent is >= 40 and < 80;
    public bool IsBad => Result.Percent < 40;

    [ObservableProperty] private int _rank;
    [ObservableProperty] private bool _isApplied;

    [RelayCommand]
    private void Apply() => _apply(this);
}
