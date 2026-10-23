using System.Text.RegularExpressions;

namespace ByeDpiPc.Core.Engine;

/// <summary>Управляет sing-box в режиме TUN (через <see cref="EngineHost"/>: права администратора нужны для адаптера).</summary>
public sealed partial class TunController
{
    public const string Source = "sing-box";

    private readonly Queue<string> _recent = new();
    private TaskCompletionSource<bool>? _startedTcs;
    private string? _config;
    private volatile bool _running;

    /// <summary>sing-box завершился сам (не по нашей команде).</summary>
    public event Action<int>? ExitedUnexpectedly;

    public bool IsRunning => _running;

    public static string? FindExecutable()
    {
        var local = Path.Combine(AppPaths.EngineDir, AppPaths.ExeName("sing-box"));
        return File.Exists(local) ? local : null;
    }

    public static string RequireExecutable() =>
        FindExecutable() ?? throw new UserFacingException(
            "Не найден движок sing-box (нужен для режима TUN).",
            "Скачайте сборку программы целиком (с папкой engine) или переключитесь в режим «Только прокси» в настройках.");

    /// <summary>sing-box check: ошибку конфига показываем человеческим текстом.</summary>
    public static async Task CheckConfigAsync(string singBox, string config)
    {
        var lines = new List<string>();
        using var p = ChildProcess.Start(singBox, new[] { "check", "-c", config, "--disable-color" }, Source, logOutput: false);
        p.Output += l => { lock (lines) lines.Add(l); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);
        await Task.Delay(50);
        string text;
        lock (lines) text = string.Join("\n", lines).Trim();
        if (text.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || text.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Конфиг маршрутизации не прошёл проверку sing-box.",
                "Скорее всего, в правилах есть ошибка (неверный домен или подсеть). Ответ sing-box:\n" + text);
    }

    public async Task StartAsync(string config, CancellationToken ct = default)
    {
        var singBox = RequireExecutable();
        await CheckConfigAsync(singBox, config);
        _config = config;
        lock (_recent) _recent.Clear();
        _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EngineHost.Instance.StartAsync(Source, singBox, new[] { "run", "-c", config, "--disable-color" },
            AppPaths.EngineDir, OnLine, OnExited, ct);

        // sing-box пишет «sing-box started», когда TUN поднят.
        var finished = await Task.WhenAny(_startedTcs.Task, Task.Delay(TimeSpan.FromSeconds(25), ct));
        if (finished != _startedTcs.Task || !_startedTcs.Task.Result)
        {
            var details = RecentLog();
            await StopAsync();
            throw new UserFacingException("sing-box не смог поднять TUN.",
                Explain(details) + (details.Length > 0 ? "\n\nПоследние сообщения sing-box:\n" + details : ""));
        }
        _running = true;
    }

    /// <summary>Перезапуск после изменения конфига (без повторного UAC: помощник уже запущен).</summary>
    public async Task RestartAsync(string config)
    {
        await StopAsync();
        await StartAsync(config);
    }

    public async Task StopAsync()
    {
        _running = false;
        await EngineHost.Instance.StopAsync(Source);
    }

    private void OnLine(string line)
    {
        Log.Engine(Source, line);
        lock (_recent)
        {
            _recent.Enqueue(AnsiRegex().Replace(line, ""));
            while (_recent.Count > 8) _recent.Dequeue();
        }
        if (line.Contains("sing-box started", StringComparison.OrdinalIgnoreCase)) _startedTcs?.TrySetResult(true);
        if (line.Contains("FATAL", StringComparison.Ordinal)) _startedTcs?.TrySetResult(false);
    }

    private void OnExited(int code)
    {
        _startedTcs?.TrySetResult(false);
        if (!_running) return;
        _running = false;
        Log.Error(Source, $"sing-box завершился с кодом {code}.");
        ExitedUnexpectedly?.Invoke(code);
    }

    private string RecentLog()
    {
        lock (_recent) return string.Join("\n", _recent);
    }

    private static string Explain(string log)
    {
        if (log.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || log.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
            return "Не хватает прав администратора для создания сетевого адаптера.";
        if (log.Contains("wintun", StringComparison.OrdinalIgnoreCase))
            return "Не удалось загрузить драйвер wintun. Перезагрузите компьютер и попробуйте снова.";
        if (log.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || log.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "Адаптер или порт уже заняты — возможно, запущен другой VPN/TUN-клиент. Закройте его и попробуйте снова.";
        return "Проверьте, не запущены ли другие VPN- или прокси-программы, и попробуйте ещё раз. Если не помогает — переключитесь в режим «Только прокси».";
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}
