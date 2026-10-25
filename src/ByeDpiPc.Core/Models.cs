using System.Text.Json.Serialization;

namespace ByeDpiPc.Core;

/// <summary>Куда отправлять трафик правила.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RouteMode>))]
public enum RouteMode { Direct, ByeDpi, Upstream }

/// <summary>Как применяются правила (как фильтр приложений в ByeByeDPI).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FilterMode>))]
public enum FilterMode
{
    /// <summary>Через обход идёт всё.</summary>
    None,
    /// <summary>Через обход идут только отмеченные правила (по умолчанию).</summary>
    Whitelist,
    /// <summary>Через обход идёт всё, кроме отмеченных правил.</summary>
    Blacklist,
}

/// <summary>Движок обхода.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EngineKind>))]
public enum EngineKind
{
    /// <summary>byedpi (ciadpi): локальный SOCKS-прокси, работает везде.</summary>
    ByeDpi,
    /// <summary>zapret (winws + WinDivert): правит пакеты напрямую, только Windows.</summary>
    Zapret,
}

[JsonConverter(typeof(JsonStringEnumConverter<EngineMode>))]
public enum EngineMode
{
    /// <summary>Виртуальный адаптер sing-box: правила по программам и доменам.</summary>
    Tun,
    /// <summary>Только локальный SOCKS-прокси (+ системный прокси/PAC).</summary>
    ProxyOnly,
}

[JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
public enum ThemeMode { System, Light, Dark }

[JsonConverter(typeof(JsonStringEnumConverter<DnsPreset>))]
public enum DnsPreset { Cloudflare, Google, Quad9, System, Custom }

/// <summary>
/// Правило: программа и/или набор доменов и подсетей, которые идут по выбранному маршруту.
/// Пресеты (YouTube, Discord, …) — тоже правила, просто с флагом <see cref="IsPreset"/>.
/// </summary>
public sealed class AppRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Ключ встроенной иконки (youtube, discord, …) или null.</summary>
    public string? Icon { get; set; }
    /// <summary>Путь к .exe, из которого берётся иконка (для добавленных вручную программ).</summary>
    public string? IconPath { get; set; }
    public string? Description { get; set; }
    /// <summary>Имена процессов: Telegram.exe, Discord.exe (на Linux/macOS — без .exe).</summary>
    public List<string> Processes { get; set; } = new();
    /// <summary>Домены: совпадение по самому домену и всем поддоменам. «*.» в начале допускается.</summary>
    public List<string> Domains { get; set; } = new();
    /// <summary>IP-подсети в формате CIDR.</summary>
    public List<string> IpCidrs { get; set; } = new();
    public RouteMode Route { get; set; } = RouteMode.ByeDpi;
    public bool Enabled { get; set; } = true;
    public bool IsPreset { get; set; }
    /// <summary>Подсказка, которая показывается у правила (например про блокировку по IP).</summary>
    public string? Hint { get; set; }

    public AppRule Clone() => new()
    {
        Id = Id, Name = Name, Icon = Icon, IconPath = IconPath, Description = Description,
        Processes = new(Processes), Domains = new(Domains), IpCidrs = new(IpCidrs),
        Route = Route, Enabled = Enabled, IsPreset = IsPreset, Hint = Hint,
    };
}

/// <summary>Сохранённая стратегия byedpi: имя и аргументы командной строки.</summary>
public sealed class StrategyProfile
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
    public EngineKind Engine { get; set; } = EngineKind.ByeDpi;
    /// <summary>zapret: отдельная стратегия для YouTube/Google (у Google свой DPI-профиль у многих провайдеров).</summary>
    public string? GoogleArguments { get; set; }

    public StrategyProfile() { }
    public StrategyProfile(string name, string arguments, EngineKind engine = EngineKind.ByeDpi)
    {
        Name = name;
        Arguments = arguments;
        Engine = engine;
    }
}

/// <summary>Запомненный результат автоподбора: чтобы не перебирать стратегии при каждом подключении.</summary>
public sealed class TunedStrategy
{
    public EngineKind Engine { get; set; }
    public string Arguments { get; set; } = "";
    /// <summary>Сайты, которые были заблокированы напрямую и открылись через стратегию (для быстрой проверки).</summary>
    public List<string> Sites { get; set; } = new();
    public DateTime Time { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public EngineKind Engine { get; set; } = OperatingSystem.IsWindows() ? EngineKind.Zapret : EngineKind.ByeDpi;
    public EngineMode Mode { get; set; } = EngineMode.Tun;
    public FilterMode Filter { get; set; } = FilterMode.Whitelist;
    public int SocksPort { get; set; } = 1080;

    public DnsPreset Dns { get; set; } = DnsPreset.Cloudflare;
    /// <summary>Свой DoH-адрес (https://…/dns-query) или IP DNS-сервера.</summary>
    public string CustomDns { get; set; } = "";
    public bool Ipv6 { get; set; }
    /// <summary>Отклонять QUIC (UDP 443) для трафика через обход, чтобы браузер перешёл на TCP.</summary>
    public bool BlockQuic { get; set; } = true;

    /// <summary>В режиме «Только прокси» прописывать системный прокси.</summary>
    public bool SetSystemProxy { get; set; } = true;
    /// <summary>Системный прокси через PAC: через обход только сайты из правил.</summary>
    public bool UsePac { get; set; } = true;

    public bool AutoStart { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>SNI для фейковых пакетов (подставляется вместо {sni} в стратегиях).</summary>
    public string FakeSni { get; set; } = "www.iana.org";
    public string ActiveStrategy { get; set; } = "";
    public string ActiveZapretStrategy { get; set; } = "";
    public List<StrategyProfile> Strategies { get; set; } = new();

    public List<AppRule> Rules { get; set; } = new();
    /// <summary>Встроенные пресеты, которые пользователь удалил (чтобы не возвращать их при обновлении).</summary>
    public List<string> RemovedPresets { get; set; } = new();

    /// <summary>Свой список доменов («Списки» → «Мои домены»).</summary>
    public List<string> CustomDomains { get; set; } = new();
    public RouteMode CustomDomainsRoute { get; set; } = RouteMode.ByeDpi;
    public bool CustomDomainsEnabled { get; set; } = true;

    /// <summary>Сайты для подбора стратегии.</summary>
    public List<string> TestSites { get; set; } = new();
    public int TestTimeoutSec { get; set; } = 5;
    public int TestParallel { get; set; } = 4;

    /// <summary>После подключения проверять обход и, если он не работает, подбирать стратегию автоматически.</summary>
    public bool AutoTune { get; set; } = true;
    /// <summary>Когда последний раз проверяли/подбирали стратегию (для показа на главной).</summary>
    public DateTime? LastTuned { get; set; }
    public string LastTuneSummary { get; set; } = "";
    public List<TunedStrategy> Tuned { get; set; } = new();

    /// <summary>Проверять обновления в релизах GitHub при запуске.</summary>
    public bool CheckUpdates { get; set; } = true;

    public bool UpstreamEnabled { get; set; }
    /// <summary>socks5://[user:pass@]host:port или http(s)://…</summary>
    public string UpstreamUri { get; set; } = "";

    /// <summary>Режим «точка выхода для телефона» через звонки VK (см. docs/vk-relay.md).</summary>
    public bool VkRelayEnabled { get; set; }
    /// <summary>Ссылка на звонок VK, по которой берутся данные ретрансляторов.</summary>
    public string VkRelayCallLink { get; set; } = "";
    /// <summary>Сколько ретрансляторов занимать с каждой стороны (скорость vs. нагрузка на VK).</summary>
    public int VkRelayCount { get; set; } = 3;
    /// <summary>Сохранённый секрет связки (base64url), чтобы телефон не пересвязывать после перезапуска.</summary>
    public string VkRelaySecret { get; set; } = "";

    /// <summary>Стратегии текущего движка.</summary>
    public IEnumerable<StrategyProfile> EngineStrategies(EngineKind? engine = null) =>
        Strategies.Where(s => s.Engine == (engine ?? Engine));

    public string GetActiveName(EngineKind engine) => engine == EngineKind.Zapret ? ActiveZapretStrategy : ActiveStrategy;

    public void SetActiveName(EngineKind engine, string name)
    {
        if (engine == EngineKind.Zapret) ActiveZapretStrategy = name;
        else ActiveStrategy = name;
    }

    public StrategyProfile? GetActiveStrategy(EngineKind? engine = null)
    {
        var e = engine ?? Engine;
        var name = GetActiveName(e);
        return EngineStrategies(e).FirstOrDefault(s => s.Name == name) ?? EngineStrategies(e).FirstOrDefault();
    }

    public TunedStrategy? GetTuned(EngineKind? engine = null) => Tuned.FirstOrDefault(t => t.Engine == (engine ?? Engine));
}
