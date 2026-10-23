namespace ByeDpiPc.Core.Testing;

public enum TunePhase { Idle, CheckingDirect, Verifying, Searching, Done, Failed }

public sealed record TuneStatus(TunePhase Phase, string Text, double? Progress = null);

public sealed record TuneOutcome(bool Working, string? NewArguments, string Summary);

/// <summary>
/// Автоподбор: 1) какие сайты блокируются напрямую; 2) открываются ли они через текущую стратегию;
/// 3) если нет — перебирает стратегии (универсальные + набор ByeByeDPI) и возвращает лучшую.
/// </summary>
public sealed class AutoTuner
{
    private const double GoodEnough = 0.8;

    public async Task<TuneOutcome> RunAsync(AppSettings s, int? runningProxyPort, bool forceSearch,
        IProgress<TuneStatus>? progress, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(s.TestTimeoutSec);
        var tester = new StrategyTester
        {
            Parallel = s.TestParallel, Timeout = timeout, FakeSni = s.FakeSni, StopOnFirstPerfect = true,
        };
        var sites = s.TestSites.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sites.Count == 0) sites = Presets.LoadTestSites().ToList();

        progress?.Report(new TuneStatus(TunePhase.CheckingDirect, "Проверяю, какие сайты блокируются…"));
        var direct = await tester.CheckDirectAsync(sites, ct);
        var blocked = direct.Where(d => !d.Ok).Select(d => d.Site).ToList();
        var open = direct.Where(d => d.Ok).Select(d => d.Site).ToList();
        Log.Info("Автоподбор", $"Напрямую не открываются {blocked.Count} из {sites.Count}: {string.Join(", ", blocked)}");

        if (blocked.Count == 0 && !forceSearch)
            return new TuneOutcome(true, null,
                "Блокировок не найдено: все сайты открываются напрямую. Если включён VPN, выключите его, чтобы проверка была честной.");

        var current = s.GetActiveStrategy()?.Arguments ?? "";
        var targets = blocked.Count > 0 ? blocked : sites;
        if (!forceSearch)
        {
            progress?.Report(new TuneStatus(TunePhase.Verifying, "Проверяю обход на заблокированных сайтах…"));
            int ok;
            if (runningProxyPort is { } port)
            {
                using var checker = new SiteChecker(port, timeout);
                ok = (await checker.CheckAllAsync(targets, tester.SiteConcurrency, ct)).Count(r => r.Ok);
            }
            else
            {
                ok = (await tester.TestOneAsync(current, targets, ct)).Success;
            }
            if (ok >= Math.Ceiling(targets.Count * GoodEnough))
                return new TuneOutcome(true, null, $"Обход работает: открываются {ok} из {targets.Count} заблокированных сайтов.");
            Log.Info("Автоподбор", $"Текущая стратегия открывает {ok} из {targets.Count} — начинаю подбор");
        }

        // Немного открытых сайтов — чтобы не выбрать стратегию, которая ломает обычные сайты.
        var testSites = targets.Concat(open.Take(3)).ToList();
        var candidates = new List<string> { current };
        candidates.AddRange(Presets.DefaultStrategies.Select(p => p.Arguments));
        candidates.AddRange(Presets.LoadTestStrategies());
        candidates = candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();

        progress?.Report(new TuneStatus(TunePhase.Searching, $"Подбираю стратегию: 0 из {candidates.Count}", 0));
        var results = await tester.RunAsync(candidates, testSites,
            new Progress<TestProgress>(p =>
            {
                if (p.Finished != null)
                    progress?.Report(new TuneStatus(TunePhase.Searching, $"Подбираю стратегию: {p.Done} из {p.Total}", (double)p.Done / p.Total));
            }), ct);

        var best = results.FirstOrDefault();
        if (best == null || best.Success == 0)
            return new TuneOutcome(false, null,
                "Ни одна стратегия не открыла заблокированные сайты. Скорее всего, они заблокированы по IP — обход DPI тут не поможет, нужен внешний прокси.");

        var currentResult = results.FirstOrDefault(r => r.Arguments == current);
        var failed = best.Sites.Where(x => !x.Ok).Select(x => x.Site).ToList();
        var summary = $"Открывает {best.Success} из {best.Total} сайтов";
        if (failed.Count > 0)
            summary += $". Не открываются ни одной стратегией: {string.Join(", ", failed)} — вероятно, блокировка по IP, для них нужен внешний прокси";
        if (currentResult != null && currentResult.Success >= best.Success)
            return new TuneOutcome(best.Percent >= GoodEnough * 100, null, "Текущая стратегия уже лучшая. " + summary + ".");
        return new TuneOutcome(best.Percent >= GoodEnough * 100, best.Arguments, "Найдена новая стратегия. " + summary + ".");
    }
}
