using System.Reflection;
using System.Text.Json;

namespace ByeDpiPc.Core;

/// <summary>Встроенные пресеты правил, стратегий и сайтов для проверки.</summary>
public static class Presets
{
    public const string AutoProfileName = "Автоподбор";

    /// <summary>
    /// Первая — универсальная: группы --auto переключаются сами, если провайдер оборвал соединение
    /// (OOB + TLS-запись → split + disorder → disoob). Без --fake: на Windows он нестабилен.
    /// </summary>
    public static readonly StrategyProfile[] DefaultStrategies =
    {
        new("Универсальная", "-o1 -r-5+se -a1 -At,r,s -s1+s -d3+s -r1+s -a1 -At,r,s -q1 -r1+s -a1"),
        new("OOB + TLS-запись", "-o1 -a1 -r-5+se"),
        new("Windows: split + disorder", "--split 1 --disorder 3+s --mod-http=h,d --auto=torst --tlsrec 1+s"),
        new("Fake + disorder", "--disorder 1 --fake -1 --ttl 8 -a1"),
        new("Только TLS-запись", "--tlsrec 1+s -a1"),
        new("YouTube: сайт, API и видео", "-H :googleapis.com -d1 -d3+s -a1 -An -o1 -r-5+se -a1"),
    };

    /// <summary>
    /// Стратегии, которые у провайдера с жёсткой фильтрацией (ihome, Москва) открывали ВСЁ, что нужно YouTube:
    /// сайт, картинки, API youtubei.googleapis.com и видеоузлы googlevideo (по 12 из 12, проверка системным curl).
    /// Одной стратегии не хватает: «-o1 -r-5+se» открывает сайт и видео, но не API (googleapis.com),
    /// а split/disorder открывают API, но не видеоузлы. Поэтому две группы: для googleapis.com своя, для остального OOB.
    /// </summary>
    public static readonly string[] ProvenStrategies =
    {
        "-H :googleapis.com -d1 -d3+s -a1 -An -o1 -r-5+se -a1",
        "-H :googleapis.com -r5+s -s25+s -a1 -An -o1 -r-5+se -a1",
        "-H :googleapis.com -r5+s -s25+s -a1 -An -o1 -r-5+se -a1 -At,r,s -s1 -d3+s -r1+s -a1",
        "-o1 -r-5+se -a1",
    };

    public static IReadOnlyList<AppRule> LoadRules()
    {
        using var stream = Open("apps.json");
        var rules = JsonSerializer.Deserialize<List<AppRule>>(stream, SettingsStore.JsonOptions) ?? new();
        foreach (var r in rules) r.IsPreset = true;
        return rules;
    }

    /// <summary>Набор стратегий для «Подбора» из ByeByeDPI.</summary>
    public static IReadOnlyList<string> LoadTestStrategies() => ReadLines("strategies.txt");

    /// <summary>Стратегии zapret (winws): первая — универсальная по умолчанию.</summary>
    public static IReadOnlyList<StrategyProfile> LoadZapretStrategies() =>
        ReadLines("zapret-strategies.txt")
            .Select(l => l.Split('|', 2))
            .Where(p => p.Length == 2)
            .Select(p => new StrategyProfile(p[0].Trim(), p[1].Trim(), EngineKind.Zapret))
            .ToList();

    /// <summary>Кандидаты для автоподбора текущего движка.</summary>
    public static List<string> Candidates(AppSettings s, EngineKind engine, bool includeFullSet = true)
    {
        var list = new List<string>();
        // Проверенные стратегии, которые открывают и сайт, и видеоузлы YouTube, идут первыми:
        // с StopOnFirstPerfect подбор заканчивается на первой же подошедшей.
        if (engine == EngineKind.ByeDpi) list.AddRange(ProvenStrategies);
        if (s.GetActiveStrategy(engine) is { } active) list.Add(active.Arguments);
        list.AddRange(s.EngineStrategies(engine).Select(p => p.Arguments));
        if (engine == EngineKind.Zapret)
        {
            list.AddRange(LoadZapretStrategies().Select(p => p.Arguments));
        }
        else
        {
            list.AddRange(DefaultStrategies.Select(p => p.Arguments));
            if (includeFullSet) list.AddRange(LoadTestStrategies());
        }
        return list.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
    }

    public static IReadOnlyList<string> LoadTestSites() => ReadLines("sites.txt");

    /// <summary>Заполняет пустые настройки значениями по умолчанию и добавляет новые пресеты.</summary>
    public static void EnsureDefaults(AppSettings s)
    {
        if (!OperatingSystem.IsWindows()) s.Engine = EngineKind.ByeDpi;
        if (!s.EngineStrategies(EngineKind.ByeDpi).Any())
            s.Strategies.AddRange(DefaultStrategies.Select(p => new StrategyProfile(p.Name, p.Arguments)));
        if (!s.EngineStrategies(EngineKind.Zapret).Any())
            s.Strategies.AddRange(LoadZapretStrategies());
        foreach (var engine in new[] { EngineKind.ByeDpi, EngineKind.Zapret })
        {
            var name = s.GetActiveName(engine);
            if (string.IsNullOrWhiteSpace(name) || s.EngineStrategies(engine).All(p => p.Name != name))
                s.SetActiveName(engine, s.EngineStrategies(engine).First().Name);
        }
        if (s.SchemaVersion < 2)
        {
            // 1.0 → 1.1: на Windows по умолчанию движок zapret, в сайты для проверки — API YouTube.
            s.SchemaVersion = 2;
        }
        if (s.SchemaVersion < 3)
        {
            // 1.2 → 1.3: в проверку добавлен видеоузел googlevideo (сайт YouTube может открываться без видео),
            // а в профили — стратегия, открывающая сайт, API и видео одновременно.
            s.SchemaVersion = 3;
            foreach (var p in DefaultStrategies)
                if (s.EngineStrategies(EngineKind.ByeDpi).All(x => x.Name != p.Name))
                    s.Strategies.Add(new StrategyProfile(p.Name, p.Arguments));
            if (s.TestSites.Count > 0 && !s.TestSites.Any(Testing.CurlProbe.IsVideoNode))
                s.TestSites.Add(Testing.CurlProbe.VideoNodeHost);
        }
        if (s.TestSites.Count == 0)
            s.TestSites.AddRange(LoadTestSites());

        var known = s.Rules.Select(r => r.Id).ToHashSet();
        foreach (var preset in LoadRules())
        {
            if (known.Contains(preset.Id) || s.RemovedPresets.Contains(preset.Id)) continue;
            s.Rules.Add(preset);
        }
        if (s.SocksPort is < 1 or > 65535) s.SocksPort = 1080;
        if (s.TestTimeoutSec is < 1 or > 30) s.TestTimeoutSec = 5;
        if (s.TestParallel is < 1 or > 16) s.TestParallel = 4;
    }

    /// <summary>Исходный вариант пресета (для кнопки «Сбросить»).</summary>
    public static AppRule? FindPreset(string id) => LoadRules().FirstOrDefault(r => r.Id == id);

    private static List<string> ReadLines(string name)
    {
        using var stream = Open(name);
        using var reader = new StreamReader(stream);
        var list = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            list.Add(line);
        }
        return list;
    }

    private static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("ByeDpiPc.Presets." + name)
        ?? throw new InvalidOperationException("Нет встроенного ресурса " + name);
}
