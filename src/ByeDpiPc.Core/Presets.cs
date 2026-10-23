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
