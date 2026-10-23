using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Routing;

namespace ByeDpiPc.Core.Sharing;

/// <summary>
/// «Файл доступа» (*.bdpi) — набор настроек, которым можно поделиться с другом:
/// стратегия, правила (в том числе маршруты, например «Telegram → внешний прокси») и ключ внешнего прокси.
/// Можно передать файлом или текстовым кодом «bdpi:…» (удобно в мессенджере).
/// </summary>
public sealed class ShareBundle
{
    public const string Format = "byedpi-pc-share";
    public const string Extension = ".bdpi";
    public const string CodePrefix = "bdpi:";

    [JsonPropertyName("format")] public string FormatId { get; set; } = Format;
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Comment { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    public EngineKind? Engine { get; set; }
    public StrategyProfile? Strategy { get; set; }
    public FilterMode? Filter { get; set; }

    /// <summary>Правила целиком (свои) или только маршрут/включение для встроенных наборов.</summary>
    public List<AppRule>? Rules { get; set; }
    public List<string>? CustomDomains { get; set; }

    /// <summary>Ключ внешнего прокси (vless://, socks5://…). Личный — делиться осторожно.</summary>
    public string? Upstream { get; set; }

    public sealed class Options
    {
        public bool Strategy { get; set; } = true;
        public bool Rules { get; set; } = true;
        public bool Upstream { get; set; }
    }

    public static ShareBundle Create(AppSettings s, string name, Options opt, string? comment = null)
    {
        var b = new ShareBundle { Name = name, Comment = comment };
        if (opt.Strategy && s.GetActiveStrategy() is { } st)
        {
            b.Engine = s.Engine;
            b.Strategy = new StrategyProfile(string.IsNullOrWhiteSpace(name) ? st.Name : name, st.Arguments, st.Engine);
        }
        if (opt.Rules)
        {
            b.Filter = s.Filter;
            b.Rules = s.Rules.Select(r =>
            {
                var c = r.Clone();
                c.IconPath = null; // путь к .exe на чужом компьютере бесполезен
                if (c.IsPreset)
                {
                    // Для встроенных наборов достаточно id, маршрута и включения — списки у друга свои.
                    var preset = Presets.FindPreset(c.Id);
                    if (preset != null && preset.Domains.SequenceEqual(c.Domains) && preset.Processes.SequenceEqual(c.Processes) && preset.IpCidrs.SequenceEqual(c.IpCidrs))
                    {
                        c.Domains = new();
                        c.Processes = new();
                        c.IpCidrs = new();
                        c.Hint = null;
                        c.Description = null;
                    }
                }
                return c;
            }).ToList();
            b.CustomDomains = s.CustomDomains.Count > 0 ? s.CustomDomains.ToList() : null;
        }
        if (opt.Upstream && s.UpstreamEnabled && !string.IsNullOrWhiteSpace(s.UpstreamUri)) b.Upstream = s.UpstreamUri;
        return b;
    }

    public string ToJson() => JsonSerializer.Serialize(this, SettingsStore.JsonOptions);

    /// <summary>Короткий текстовый код: «bdpi:» + base64url(gzip(json)).</summary>
    public string ToCode()
    {
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, new JsonSerializerOptions(SettingsStore.JsonOptions) { WriteIndented = false }));
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(raw);
        return CodePrefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Разбирает файл (JSON), код «bdpi:…» или голую ссылку vless:// / socks5://.</summary>
    public static ShareBundle Parse(string text)
    {
        text = text.Trim().Trim('﻿');
        try
        {
            if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var b64 = text[CodePrefix.Length..].Trim().Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            else if (text.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
            {
                if (!UpstreamProxy.TryParse(text, out var p, out var err)) throw new FormatException(err);
                return new ShareBundle { Name = p!.Label ?? "Внешний прокси", Upstream = text };
            }
            var bundle = JsonSerializer.Deserialize<ShareBundle>(text, SettingsStore.JsonOptions);
            if (bundle == null || bundle.FormatId != Format) throw new FormatException("это не файл настроек ByeDPI PC");
            if (bundle.Version > 1) throw new FormatException("файл создан более новой версией программы — обновитесь");
            return bundle;
        }
        catch (Exception ex) when (ex is not UserFacingException)
        {
            throw new UserFacingException("Не удалось прочитать настройки.",
                "Проверьте, что файл или код скопирован целиком. Подробности: " + ex.Message, ex);
        }
    }

    /// <summary>Что изменится при импорте — для окна подтверждения.</summary>
    public List<string> Describe()
    {
        var list = new List<string>();
        if (Strategy != null)
            list.Add($"Стратегия ({(Strategy.Engine == EngineKind.Zapret ? "zapret" : "byedpi")}): «{Strategy.Name}»");
        if (Rules != null)
        {
            var enabled = Rules.Where(r => r.Enabled).Select(r => r.Name + (r.Route == RouteMode.Upstream ? " → внешний прокси" : r.Route == RouteMode.Direct ? " → напрямую" : "")).ToList();
            list.Add($"Правила: включено {enabled.Count} — {string.Join(", ", enabled.Take(8))}{(enabled.Count > 8 ? "…" : "")}");
        }
        if (CustomDomains is { Count: > 0 }) list.Add($"Свои домены: {CustomDomains.Count}");
        if (Upstream != null)
        {
            list.Add("Внешний прокси: " + (UpstreamProxy.TryParse(Upstream, out var p, out _) ? p!.Describe() : "ключ"));
            if (Rules == null) list.Add("Telegram пойдёт через внешний прокси");
        }
        return list;
    }

    /// <summary>Применяет набор к настройкам. Встроенные наборы, которых нет у пользователя, добавляются.</summary>
    public void ApplyTo(AppSettings s)
    {
        if (Strategy != null && !string.IsNullOrWhiteSpace(Strategy.Arguments))
        {
            var engine = Strategy.Engine;
            if (engine == EngineKind.Zapret && !OperatingSystem.IsWindows()) engine = EngineKind.ByeDpi;
            if (engine == Strategy.Engine)
            {
                var name = string.IsNullOrWhiteSpace(Strategy.Name) ? "Импорт" : Strategy.Name;
                var existing = s.EngineStrategies(engine).FirstOrDefault(p => p.Arguments == Strategy.Arguments);
                if (existing == null)
                {
                    while (s.EngineStrategies(engine).Any(p => p.Name == name)) name += " (импорт)";
                    existing = new StrategyProfile(name, Strategy.Arguments, engine);
                    s.Strategies.Insert(0, existing);
                }
                s.SetActiveName(engine, existing.Name);
                s.Engine = engine;
            }
        }
        if (Filter != null) s.Filter = Filter.Value;
        if (Rules != null)
        {
            foreach (var r in Rules)
            {
                var mine = s.Rules.FirstOrDefault(x => x.Id == r.Id);
                if (r.IsPreset)
                {
                    if (mine == null && Presets.FindPreset(r.Id) is { } preset)
                    {
                        mine = preset;
                        s.Rules.Add(mine);
                        s.RemovedPresets.Remove(r.Id);
                    }
                    if (mine == null) continue;
                    mine.Enabled = r.Enabled;
                    mine.Route = r.Route;
                    if (r.Domains.Count > 0 || r.Processes.Count > 0 || r.IpCidrs.Count > 0)
                    {
                        mine.Domains = r.Domains;
                        mine.Processes = r.Processes;
                        mine.IpCidrs = r.IpCidrs;
                    }
                }
                else if (mine != null)
                {
                    var idx = s.Rules.IndexOf(mine);
                    s.Rules[idx] = r.Clone();
                }
                else
                {
                    s.Rules.Add(r.Clone());
                }
            }
        }
        if (CustomDomains != null)
            s.CustomDomains = s.CustomDomains.Concat(CustomDomains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Upstream != null)
        {
            s.UpstreamUri = Upstream;
            s.UpstreamEnabled = true;
            // Голый ключ без правил — типичный случай «доступ к Telegram»: направляем Telegram через него.
            if (Rules == null && s.Rules.FirstOrDefault(r => r.Id == "preset.telegram") is { } tg)
            {
                tg.Enabled = true;
                tg.Route = RouteMode.Upstream;
            }
        }
        s.Tuned.Clear(); // стратегия могла смениться — при подключении проверим заново
    }
}
