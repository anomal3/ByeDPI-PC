using ByeDpiPc.Core.Routing;
using ByeDpiPc.Core.Strategy;

namespace ByeDpiPc.Core.Engine;

/// <summary>Файлы списков для winws.</summary>
public sealed record ZapretLists(string? Hosts, string? HostsExclude, string? Ipset, string? IpsetExclude);

/// <summary>
/// Сборка командной строки winws (zapret) из правил. Профили (--new):
/// QUIC 443 → фейковый QUIC Initial; голос Discord (UDP) → фейк discord/stun; TCP 80/443 → выбранная стратегия.
/// Структура повторяет general.bat из https://github.com/Flowseal/zapret-discord-youtube (MIT).
/// </summary>
public static class ZapretCommand
{
    public const string DiscordUdpPorts = "19294-19344,50000-50100";
    public const string DiscordTcpPorts = "2053,2083,2087,2096,8443";

    /// <summary>Домены YouTube/Google для отдельного профиля.</summary>
    public static readonly string[] GoogleDomains =
    {
        "youtube.com", "youtu.be", "yt.be", "youtube-nocookie.com", "youtubekids.com", "googlevideo.com", "ytimg.com", "ggpht.com",
        "youtubei.googleapis.com", "youtube.googleapis.com", "jnn-pa.googleapis.com", "googleusercontent.com",
    };

    public static bool IsGoogleSite(string site)
    {
        var d = Routing.RoutePlan.NormalizeDomain(site) ?? site;
        return GoogleDomains.Any(g => d == g || d.EndsWith("." + g, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Пишет списки в папку и возвращает пути (пустые списки не создаются: пустой hostlist у winws = «все сайты»).</summary>
    public static ZapretLists WriteLists(RoutePlan plan, FilterMode filter, string dir)
    {
        Directory.CreateDirectory(dir);
        string? Write(string name, IEnumerable<string> lines)
        {
            var path = Path.Combine(dir, name);
            var list = lines.ToList();
            if (list.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return null;
            }
            File.WriteAllLines(path, list);
            return path;
        }

        if (filter == FilterMode.Whitelist)
            return new ZapretLists(
                Write("hosts.txt", plan.ByeDpi.Domains), null,
                Write("ipset.txt", plan.ByeDpi.IpCidrs), null);

        return new ZapretLists(
            null, Write("hosts-exclude.txt", plan.Direct.Domains.Concat(plan.Upstream.Domains)),
            null, Write("ipset-exclude.txt", plan.Direct.IpCidrs.Concat(plan.Upstream.IpCidrs)));
    }

    public static List<string> Build(RoutePlan plan, FilterMode filter, string strategy, ZapretLists lists, string? googleStrategy = null)
    {
        var strat = CiadpiCommand.Split(strategy);
        var whitelist = filter == FilterMode.Whitelist;
        var discord = whitelist
            ? plan.ByeDpi.Domains.Contains("discord.com")
            : !plan.Direct.Domains.Contains("discord.com") && !plan.Upstream.Domains.Contains("discord.com");

        var hostFilter = new List<string>();
        if (whitelist)
        {
            if (lists.Hosts != null) hostFilter.Add($"--hostlist={lists.Hosts}");
        }
        else
        {
            if (lists.HostsExclude != null) hostFilter.Add($"--hostlist-exclude={lists.HostsExclude}");
            if (lists.IpsetExclude != null) hostFilter.Add($"--ipset-exclude={lists.IpsetExclude}");
        }
        var hasDomains = !whitelist || lists.Hosts != null;
        var hasIps = whitelist && lists.Ipset != null;
        if (!hasDomains && !hasIps)
            throw new UserFacingException("Нечего направлять через обход.",
                "Включите хотя бы одно правило с доменами на вкладке «Приложения» или выберите режим «Всё, кроме выбранного».");

        var profiles = new List<List<string>>();
        if (hasDomains)
        {
            profiles.Add(new List<string> { "--filter-udp=443" }
                .Concat(hostFilter)
                .Concat(new[] { "--dpi-desync=fake", "--dpi-desync-repeats=6", "--dpi-desync-fake-quic=quic_initial_www_google_com.bin" })
                .ToList());
        }
        if (discord)
        {
            profiles.Add(new List<string>
            {
                $"--filter-udp={DiscordUdpPorts}", "--filter-l7=discord,stun", "--dpi-desync=fake",
                "--dpi-desync-fake-discord=ACTIVE_DISCORD_UDP.bin", "--dpi-desync-fake-stun=ACTIVE_DISCORD_UDP.bin", "--dpi-desync-repeats=6",
            });
            profiles.Add(new List<string> { $"--filter-tcp={DiscordTcpPorts}", "--hostlist-domains=discord.media" }.Concat(strat).ToList());
        }
        var google = !string.IsNullOrWhiteSpace(googleStrategy) &&
                     (!whitelist || plan.ByeDpi.Domains.Any(d => GoogleDomains.Contains(d)));
        if (google)
        {
            // Отдельный профиль для YouTube/Google — до общего, чтобы сработал первым.
            var domains = whitelist ? GoogleDomains.Where(d => plan.ByeDpi.Domains.Contains(d)) : GoogleDomains;
            profiles.Add(new List<string> { "--filter-tcp=80,443", "--hostlist-domains=" + string.Join(',', domains) }
                .Concat(whitelist ? Array.Empty<string>() : hostFilter.Where(h => h.StartsWith("--ipset-exclude")))
                .Concat(CiadpiCommand.Split(googleStrategy!)).ToList());
        }
        if (hasDomains)
            profiles.Add(new List<string> { "--filter-tcp=80,443" }.Concat(hostFilter).Concat(strat).ToList());
        if (hasIps)
            profiles.Add(new List<string> { "--filter-tcp=80,443", $"--ipset={lists.Ipset}" }.Concat(strat).ToList());

        var args = new List<string>
        {
            "--wf-tcp=80,443" + (discord ? "," + DiscordTcpPorts : ""),
            "--wf-udp=443" + (discord ? "," + DiscordUdpPorts : ""),
        };
        for (int i = 0; i < profiles.Count; i++)
        {
            if (i > 0) args.Add("--new");
            args.AddRange(profiles[i]);
        }
        return args;
    }

    /// <summary>Команда для проверки одной стратегии на списке сайтов (подбор).</summary>
    public static List<string> BuildForTest(string strategy, string hostsFile, string? googleStrategy = null)
    {
        var args = new List<string>
        {
            "--wf-tcp=80,443", "--wf-udp=443",
            "--filter-udp=443", $"--hostlist={hostsFile}", "--dpi-desync=fake", "--dpi-desync-repeats=6",
            "--dpi-desync-fake-quic=quic_initial_www_google_com.bin", "--new",
        };
        if (!string.IsNullOrWhiteSpace(googleStrategy))
        {
            args.Add("--filter-tcp=80,443");
            args.Add("--hostlist-domains=" + string.Join(',', GoogleDomains));
            args.AddRange(CiadpiCommand.Split(googleStrategy));
            args.Add("--new");
        }
        args.Add("--filter-tcp=80,443");
        args.Add($"--hostlist={hostsFile}");
        args.AddRange(CiadpiCommand.Split(strategy));
        return args;
    }
}

/// <summary>Запуск winws через <see cref="EngineHost"/> (WinDivert требует прав администратора).</summary>
public sealed class ZapretController
{
    public const string Source = "zapret";
    private readonly string _name;
    private readonly Queue<string> _recent = new();
    private TaskCompletionSource<int>? _exitTcs;
    private volatile bool _running;
    private string? _lastArgs;

    public event Action<int>? ExitedUnexpectedly;
    public bool IsRunning => _running;

    public ZapretController(string name = "winws") => _name = name;

    public static string Dir => Path.Combine(AppPaths.EngineDir, "zapret");

    public static string? FindExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Path.Combine(Dir, "winws.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static bool IsAvailable => FindExecutable() != null;

    public static string RequireExecutable() =>
        FindExecutable() ?? throw new UserFacingException(
            "Не найден движок zapret (winws).",
            OperatingSystem.IsWindows()
                ? "Скачайте сборку программы целиком (папка engine\\zapret) или выберите движок byedpi в настройках."
                : "Движок zapret есть только для Windows. Выберите byedpi в настройках.");

    public async Task StartAsync(IReadOnlyList<string> args, CancellationToken ct = default, bool log = true)
    {
        var exe = RequireExecutable();
        await EngineHost.Instance.EnsureTcpTimestampsAsync(ct);
        lock (_recent) _recent.Clear();
        _exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (log) Log.Info(Source, "Запуск: winws " + CiadpiCommand.Join(args));
        await EngineHost.Instance.StartAsync(_name, exe, args, Dir, line => OnLine(line, log), OnExited, ct);
        _running = true;
        _lastArgs = string.Join('\u0001', args);

        // winws не сообщает надёжно о готовности (вывод буферизуется) — считаем, что он работает,
        // если не завершился за пару секунд (ошибки в параметрах и драйвере проявляются сразу).
        var finished = await Task.WhenAny(_exitTcs.Task, Task.Delay(TimeSpan.FromSeconds(2.5), ct));
        if (finished == _exitTcs.Task)
        {
            _running = false;
            string details;
            lock (_recent) details = string.Join("\n", _recent);
            throw new UserFacingException("zapret (winws) не запустился.",
                Explain(details) + (details.Length > 0 ? "\n\nОтвет winws:\n" + details : $"\n\nКод выхода: {_exitTcs.Task.Result}"));
        }
    }

    /// <summary>Перезапуск, только если командная строка изменилась (списки winws перечитывает сам).</summary>
    public async Task ApplyAsync(IReadOnlyList<string> args)
    {
        if (_running && _lastArgs == string.Join('\u0001', args)) return;
        await StopAsync();
        await StartAsync(args);
    }

    public async Task StopAsync()
    {
        _running = false;
        _lastArgs = null;
        await EngineHost.Instance.StopAsync(_name);
    }

    private void OnLine(string line, bool log)
    {
        if (log) Log.Engine(Source, line);
        lock (_recent)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 8) _recent.Dequeue();
        }
    }

    private void OnExited(int code)
    {
        _exitTcs?.TrySetResult(code);
        if (!_running) return;
        _running = false;
        Log.Error(Source, $"winws завершился с кодом {code}.");
        ExitedUnexpectedly?.Invoke(code);
    }

    private static string Explain(string log)
    {
        if (log.Contains("Access", StringComparison.OrdinalIgnoreCase) && log.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return "Не хватает прав администратора для драйвера WinDivert.";
        if (log.Contains("windivert", StringComparison.OrdinalIgnoreCase))
            return "Не удалось загрузить драйвер WinDivert. Возможно, его заблокировал антивирус или запущена другая копия zapret/GoodbyeDPI — закройте её и попробуйте снова.";
        return "Проверьте, не запущены ли другие программы обхода (zapret, GoodbyeDPI), и попробуйте другую стратегию.";
    }
}
