using System.IO.Pipes;
using System.Text.RegularExpressions;

namespace ByeDpiPc.Core.Engine;

/// <summary>
/// Управляет sing-box в режиме TUN. Если программа уже запущена с правами администратора —
/// sing-box запускается напрямую, иначе через помощника <see cref="TunHost"/> (один запрос UAC на подключение).
/// </summary>
public sealed partial class TunController : IAsyncDisposable
{
    public const string Source = "sing-box";

    private ChildProcess? _direct;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCts;
    private TaskCompletionSource<bool>? _startedTcs;
    private readonly Queue<string> _recent = new();
    private volatile bool _stopping;

    /// <summary>sing-box завершился сам (не по нашей команде).</summary>
    public event Action<int>? ExitedUnexpectedly;

    public bool IsRunning { get; private set; }

    public static string? FindExecutable() => AppPaths.FindEngine("sing-box");

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
        _stopping = false;
        lock (_recent) _recent.Clear();
        _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (Elevation.IsElevated)
        {
            var p = ChildProcess.Start(singBox, new[] { "run", "-c", config, "--disable-color" }, Source, logOutput: false);
            _direct = p;
            p.Output += OnLine;
            p.Exited += code => OnExited(code);
        }
        else
        {
            var name = $"byedpi-pc-tun-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40];
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var (file, args) = Elevation.SelfCommand();
            args.AddRange(new[] { TunHost.Flag, TunHost.PipePathForClient(name), singBox, config });
            Log.Info(Source, "Запрашиваю права администратора для TUN…");
            Elevation.StartElevated(file, args);

            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    await _pipe.WaitForConnectionAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await DisposePipeAsync();
                    throw new UserFacingException("Помощник TUN не запустился.",
                        "Не дождался ответа от процесса с правами администратора. Возможно, окно UAC было закрыто. Попробуйте подключиться ещё раз.");
                }
            }
            _writer = new StreamWriter(_pipe) { AutoFlush = true };
            _readCts = new CancellationTokenSource();
            _ = ReadLoopAsync(new StreamReader(_pipe), _readCts.Token);
        }

        // sing-box пишет «sing-box started», когда TUN поднят.
        var finished = await Task.WhenAny(_startedTcs.Task, Task.Delay(TimeSpan.FromSeconds(25), ct));
        if (finished != _startedTcs.Task || !_startedTcs.Task.Result)
        {
            var details = RecentLog();
            await StopAsync();
            throw new UserFacingException("sing-box не смог поднять TUN.",
                Explain(details) + (details.Length > 0 ? "\n\nПоследние сообщения sing-box:\n" + details : ""));
        }
        IsRunning = true;
    }

    /// <summary>Перезапуск после изменения конфига (без повторного UAC).</summary>
    public async Task RestartAsync(string config)
    {
        if (_writer != null)
        {
            await CheckConfigAsync(RequireExecutable(), config);
            _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = true;
            await _writer.WriteLineAsync("RESTART");
            var finished = await Task.WhenAny(_startedTcs.Task, Task.Delay(TimeSpan.FromSeconds(25)));
            _stopping = false;
            if (finished != _startedTcs.Task || !_startedTcs.Task.Result)
                throw new UserFacingException("sing-box не перезапустился.", RecentLog());
            return;
        }
        await StopAsync();
        await StartAsync(config);
    }

    public async Task StopAsync()
    {
        _stopping = true;
        IsRunning = false;
        if (_direct != null)
        {
            await _direct.StopAsync();
            _direct.Dispose();
            _direct = null;
        }
        if (_writer != null)
        {
            try
            {
                await _writer.WriteLineAsync("STOP");
                // Ждём, пока помощник закроет канал (он сам дождётся остановки sing-box).
                for (int i = 0; i < 50 && _pipe is { IsConnected: true }; i++) await Task.Delay(100);
            }
            catch { /* помощник уже закрыт */ }
        }
        await DisposePipeAsync();
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("LOG ")) OnLine(line[4..]);
                else if (line.StartsWith("EXITED ")) OnExited(int.TryParse(line[7..], out var c) ? c : -1);
                else if (line.StartsWith("ERROR ")) Log.Error(Source, line[6..]);
            }
        }
        catch { /* канал закрыт */ }
        if (!_stopping && IsRunning)
        {
            IsRunning = false;
            Log.Error(Source, "Помощник TUN неожиданно завершился.");
            ExitedUnexpectedly?.Invoke(-1);
        }
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
        if (_stopping) return;
        if (IsRunning)
        {
            IsRunning = false;
            Log.Error(Source, $"sing-box завершился с кодом {code}.");
            ExitedUnexpectedly?.Invoke(code);
        }
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

    private async Task DisposePipeAsync()
    {
        _readCts?.Cancel();
        _readCts = null;
        if (_writer != null)
        {
            try { await _writer.DisposeAsync(); } catch { }
            _writer = null;
        }
        if (_pipe != null)
        {
            try { await _pipe.DisposeAsync(); } catch { }
            _pipe = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}
