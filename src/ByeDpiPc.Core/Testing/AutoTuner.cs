using ByeDpiPc.Core.Engine;

namespace ByeDpiPc.Core.Testing;

public enum TunePhase { Idle, CheckingDirect, Verifying, Searching, Done, Failed }

public sealed record TuneStatus(TunePhase Phase, string Text, double? Progress = null);

/// <param name="Working">Обход работает (текущая или найденная стратегия открывает заблокированные сайты и не ломает остальные).</param>
/// <param name="NewArguments">Новая стратегия, если найдена лучше текущей.</param>
/// <param name="OpenedSites">Заблокированные напрямую сайты, которые открылись через стратегию (для быстрой проверки).</param>
/// <param name="GoogleArguments">zapret: отдельная стратегия для YouTube/Google (если она лучше общей).</param>
public sealed record TuneOutcome(bool Working, string? NewArguments, string Summary, IReadOnlyList<string> OpenedSites, string? GoogleArguments = null);

/// <summary>
/// Автоподбор: 1) какие сайты блокируются напрямую; 2) как работает текущая стратегия на ВСЕХ сайтах
/// (стратегия не должна ломать то, что открывалось и без неё — например, API YouTube);
/// 3) если плохо — перебирает стратегии текущего движка и возвращает лучшую.
/// </summary>
public sealed class AutoTuner
{
    public async Task<TuneOutcome> RunAsync(AppSettings s, int? runningProxyPort, bool forceSearch,
        IProgress<TuneStatus>? progress, CancellationToken ct)
    {
        var engine = s.Engine;
        var timeout = TimeSpan.FromSeconds(s.TestTimeoutSec);
        var tester = new StrategyTester
        {
            Engine = engine, Parallel = s.TestParallel, Timeout = timeout, FakeSni = s.FakeSni, StopOnFirstPerfect = true,
        };
        var sites = s.TestSites.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sites.Count == 0) sites = Presets.LoadTestSites().ToList();

        progress?.Report(new TuneStatus(TunePhase.CheckingDirect, "Проверяю, какие сайты блокируются…"));
        var direct = await tester.CheckDirectAsync(sites, ct);
        var blocked = direct.Where(d => !d.Ok).Select(d => d.Site).ToList();
        var open = direct.Where(d => d.Ok).Select(d => d.Site).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Log.Info("Автоподбор", $"Напрямую не открываются {blocked.Count} из {sites.Count}: {string.Join(", ", blocked)}");

        if (blocked.Count == 0 && !forceSearch)
            return new TuneOutcome(true, null,
                "Блокировок не найдено: все сайты открываются напрямую. Если включён VPN, выключите его, чтобы проверка была честной.",
                Array.Empty<string>());

        var currentProfile = s.GetActiveStrategy(engine);
        var current = currentProfile?.Arguments ?? "";
        if (!forceSearch && current.Length > 0)
        {
            progress?.Report(new TuneStatus(TunePhase.Verifying, "Проверяю текущую стратегию…"));
            List<SiteResult> via;
            if (engine == EngineKind.ByeDpi && runningProxyPort is { } port)
            {
                using var checker = new SiteChecker(port, timeout);
                via = await checker.CheckAllAsync(sites, tester.SiteConcurrency, ct);
            }
            else
            {
                via = (await tester.TestOneAsync(current, sites, ct, engine == EngineKind.Zapret ? currentProfile?.GoogleArguments : null)).Sites;
            }
            var (good, opened, broken) = Evaluate(via, blocked, open);
            Log.Info("Автоподбор", $"Текущая стратегия: открыла {opened.Count} из {blocked.Count} заблокированных, сломала {broken.Count}: {string.Join(", ", broken)}");
            if (good)
                return new TuneOutcome(true, null,
                    $"Обход работает: открываются {opened.Count} из {blocked.Count} заблокированных сайтов.", opened);
        }

        var candidates = Presets.Candidates(s, engine);
        progress?.Report(new TuneStatus(TunePhase.Searching, $"Подбираю стратегию: 0 из {candidates.Count}", 0));
        var results = await tester.RunAsync(candidates, sites,
            new Progress<TestProgress>(p =>
            {
                if (p.Finished != null)
                    progress?.Report(new TuneStatus(TunePhase.Searching, $"Подбираю стратегию: {p.Done} из {p.Total}", (double)p.Done / p.Total));
            }), ct);

        var best = results.FirstOrDefault();
        if (best == null || best.Sites.Count == 0)
            return new TuneOutcome(false, null, "Не удалось проверить стратегии: " + (best?.Error ?? "нет результатов"), Array.Empty<string>());

        // zapret: у YouTube/Google часто свой фильтр — берём для них отдельную лучшую стратегию.
        string? googleArgs = null;
        var combined = best.Sites;
        if (engine == EngineKind.Zapret && sites.Any(ZapretCommand.IsGoogleSite))
        {
            int General(StrategyResult r) => r.Sites.Count(x => x.Ok && !ZapretCommand.IsGoogleSite(x.Site));
            int Google(StrategyResult r) => r.Sites.Count(x => x.Ok && ZapretCommand.IsGoogleSite(x.Site));
            var bestGeneral = results.OrderByDescending(General).ThenByDescending(r => r.Success).First();
            var bestGoogle = results.OrderByDescending(Google).ThenByDescending(r => r.Success).First();
            best = bestGeneral;
            if (bestGoogle != bestGeneral && Google(bestGoogle) > Google(bestGeneral))
            {
                googleArgs = bestGoogle.Arguments;
                Log.Info("Автоподбор", $"Для YouTube/Google отдельная стратегия: {googleArgs}");
            }
            combined = bestGeneral.Sites.Where(x => !ZapretCommand.IsGoogleSite(x.Site))
                .Concat((googleArgs != null ? bestGoogle : bestGeneral).Sites.Where(x => ZapretCommand.IsGoogleSite(x.Site)))
                .ToList();
        }

        var (bestGood, bestOpened, _) = Evaluate(combined, blocked, open);
        if (bestOpened.Count == 0 && blocked.Count > 0)
            return new TuneOutcome(false, null,
                "Ни одна стратегия не открыла заблокированные сайты. Скорее всего, они заблокированы по IP — обход DPI тут не поможет, нужен внешний прокси.",
                Array.Empty<string>());

        var stillBlocked = blocked.Except(bestOpened, StringComparer.OrdinalIgnoreCase).ToList();
        var summary = $"открываются {bestOpened.Count} из {blocked.Count} заблокированных сайтов";
        if (stillBlocked.Count > 0)
            summary += $". Не открылись: {string.Join(", ", stillBlocked)} — вероятно, блокировка по IP, для них нужен внешний прокси";

        var combinedSuccess = combined.Count(x => x.Ok);
        var currentResult = results.FirstOrDefault(r => r.Arguments == current);
        if (currentResult != null && googleArgs == null && currentProfile?.GoogleArguments == null && currentResult.Success >= combinedSuccess)
            return new TuneOutcome(bestGood, null, "Текущая стратегия уже лучшая: " + summary + ".", bestOpened);
        return new TuneOutcome(bestGood, best.Arguments, "Найдена новая стратегия: " + summary + ".", bestOpened, googleArgs);
    }

    /// <summary>
    /// Хорошо, если стратегия сломала не больше одного сайта, открывавшегося напрямую,
    /// и открыла хотя бы 60% заблокированных.
    /// </summary>
    private static (bool Good, List<string> Opened, List<string> Broken) Evaluate(
        IEnumerable<SiteResult> via, IReadOnlyCollection<string> blocked, HashSet<string> open)
    {
        var ok = via.Where(r => r.Ok).Select(r => r.Site).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var opened = blocked.Where(ok.Contains).ToList();
        var broken = open.Where(x => !ok.Contains(x)).ToList();
        var good = broken.Count <= 1 && opened.Count >= Math.Ceiling(blocked.Count * 0.6);
        return (good, opened, broken);
    }

    /// <summary>
    /// Быстрая проверка запомненной стратегии при подключении: 2–3 сайта, которые она раньше открыла.
    /// </summary>
    public static async Task<bool> QuickCheckAsync(AppSettings s, IReadOnlyList<string> sites, int? proxyPort, CancellationToken ct)
    {
        var sample = sites.Take(3).ToList();
        if (sample.Count == 0) return true;
        using var checker = new SiteChecker(s.Engine == EngineKind.ByeDpi ? proxyPort : null, TimeSpan.FromSeconds(Math.Max(4, s.TestTimeoutSec)));
        var results = await checker.CheckAllAsync(sample, sample.Count, ct);
        var ok = results.Count(r => r.Ok);
        Log.Info("Автоподбор", $"Быстрая проверка: открылось {ok} из {sample.Count} ({string.Join(", ", sample)})");
        return ok >= Math.Ceiling(sample.Count * 0.6);
    }
}
