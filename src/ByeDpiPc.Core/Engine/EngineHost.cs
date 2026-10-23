using System.Collections.Concurrent;
using System.IO.Pipes;

namespace ByeDpiPc.Core.Engine;

/// <summary>
/// Запуск движков, которым нужны права администратора (sing-box, winws).
/// Если программа уже запущена с правами (автозапуск через Планировщик) — процессы стартуют напрямую.
/// Иначе один раз за сеанс поднимается помощник <see cref="ElevatedHost"/> (один запрос UAC),
/// и дальше движки запускаются и перезапускаются через него без новых запросов.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    public static EngineHost Instance { get; } = new();

    private sealed class Handle
    {
        public required Action<string> OnLine { get; init; }
        public required Action<int> OnExit { get; init; }
        public TaskCompletionSource<string?> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChildProcess? Local { get; set; }
        public bool Stopping { get; set; }
    }

    private readonly ConcurrentDictionary<string, Handle> _handles = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;

    public static bool RunsLocally => Elevation.IsElevated;

    public async Task StartAsync(string name, string exe, IEnumerable<string> args, string cwd,
        Action<string> onLine, Action<int> onExit, CancellationToken ct = default)
    {
        await StopAsync(name);
        var handle = new Handle { OnLine = onLine, OnExit = onExit };
        _handles[name] = handle;

        if (RunsLocally)
        {
            var p = ChildProcess.Start(exe, args, name, logOutput: false, workingDirectory: cwd);
            handle.Local = p;
            p.Output += onLine;
            p.Exited += code =>
            {
                if (handle.Stopping) return;
                _handles.TryRemove(new KeyValuePair<string, Handle>(name, handle));
                onExit(code);
            };
            return;
        }

        await EnsureHelperAsync(ct);
        await SendAsync(string.Join('\t', new[] { "START", name, exe, cwd }.Concat(args)));
        var done = await Task.WhenAny(handle.Started.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
        if (done != handle.Started.Task)
        {
            _handles.TryRemove(name, out _);
            throw new UserFacingException($"Не удалось запустить {name}.", "Помощник с правами администратора не ответил. Попробуйте подключиться ещё раз.");
        }
        if (handle.Started.Task.Result is { } error)
        {
            _handles.TryRemove(name, out _);
            throw new UserFacingException($"Не удалось запустить {name}.", error);
        }
    }

    public async Task StopAsync(string name)
    {
        if (!_handles.TryGetValue(name, out var handle)) return;
        handle.Stopping = true;
        if (handle.Local != null)
        {
            await handle.Local.StopAsync();
            handle.Local.Dispose();
        }
        else if (_writer != null)
        {
            try
            {
                await SendAsync($"STOP\t{name}");
                await Task.WhenAny(handle.Stopped.Task, Task.Delay(TimeSpan.FromSeconds(8)));
            }
            catch { /* помощник уже закрыт */ }
        }
        _handles.TryRemove(new KeyValuePair<string, Handle>(name, handle));
    }

    public bool IsRunning(string name) => _handles.ContainsKey(name);

    private TaskCompletionSource<string>? _tcpTs;
    private bool _tcpTsDone;

    /// <summary>Включить TCP timestamps (один раз за сеанс; нужны стратегиям zapret с fooling=ts).</summary>
    public async Task EnsureTcpTimestampsAsync(CancellationToken ct = default)
    {
        if (_tcpTsDone || !OperatingSystem.IsWindows()) return;
        string result;
        if (RunsLocally)
        {
            result = await Task.Run(ElevatedHost.EnableTcpTimestamps, ct);
        }
        else
        {
            await EnsureHelperAsync(ct);
            _tcpTs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendAsync("TCPTS");
            var done = await Task.WhenAny(_tcpTs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            result = done == _tcpTs.Task ? _tcpTs.Task.Result : "нет ответа";
        }
        _tcpTsDone = true;
        Log.Info("Движки", "TCP timestamps (для стратегий zapret): " + (result == "ok" ? "включены" : result));
    }

    private async Task EnsureHelperAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_pipe is { IsConnected: true } && _writer != null) return;
            await DisposePipeAsync();

            var name = $"byedpi-pc-host-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40];
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var (file, args) = Elevation.SelfCommand();
            args.AddRange(new[] { ElevatedHost.Flag, ElevatedHost.PipePathForClient(name), AppPaths.EngineDir });
            Log.Info("Движки", "Запрашиваю права администратора…");
            try
            {
                Elevation.StartElevated(file, args);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }

            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    await pipe.WaitForConnectionAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await pipe.DisposeAsync();
                    throw new UserFacingException("Помощник с правами администратора не запустился.",
                        "Не дождался ответа после запроса UAC. Возможно, окно было закрыто. Попробуйте ещё раз.");
                }
            }
            _pipe = pipe;
            _writer = new StreamWriter(pipe) { AutoFlush = true };
            _ = ReadLoopAsync(new StreamReader(pipe), pipe);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task SendAsync(string line)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_writer == null) throw new IOException("Помощник не подключён");
            await _writer.WriteLineAsync(line);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, NamedPipeServerStream pipe)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var parts = line.Split('\t', 3);
                var kind = parts[0];
                var name = parts.Length > 1 ? parts[1] : "";
                var rest = parts.Length > 2 ? parts[2] : "";
                _handles.TryGetValue(name, out var h);
                switch (kind)
                {
                    case "STARTED": h?.Started.TrySetResult(null); break;
                    case "FAILED": h?.Started.TrySetResult(rest); break;
                    case "LOG": h?.OnLine(rest); break;
                    case "STOPPED": h?.Stopped.TrySetResult(); break;
                    case "EXITED":
                        if (h != null && !h.Stopping)
                        {
                            _handles.TryRemove(new KeyValuePair<string, Handle>(name, h));
                            h.OnExit(int.TryParse(rest, out var c) ? c : -1);
                        }
                        break;
                    case "ERROR": Log.Error("Движки", line[6..]); break;
                    case "TCPTS": _tcpTs?.TrySetResult(rest); break;
                }
            }
        }
        catch { /* канал закрыт */ }

        if (!ReferenceEquals(pipe, _pipe)) return;
        Log.Warn("Движки", "Помощник с правами администратора завершился");
        _writer = null;
        foreach (var (name, h) in _handles.ToArray())
        {
            _handles.TryRemove(name, out _);
            h.Started.TrySetResult("помощник завершился");
            h.Stopped.TrySetResult();
            if (!h.Stopping) h.OnExit(-1);
        }
    }

    private async Task DisposePipeAsync()
    {
        _writer = null;
        if (_pipe != null)
        {
            try { await _pipe.DisposeAsync(); } catch { }
            _pipe = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _handles.Keys.ToList()) await StopAsync(name);
        await DisposePipeAsync();
    }
}
