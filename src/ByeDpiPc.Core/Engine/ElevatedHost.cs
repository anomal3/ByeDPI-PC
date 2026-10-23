using System.Collections.Concurrent;
using System.IO.Pipes;

namespace ByeDpiPc.Core.Engine;

/// <summary>
/// Помощник с правами администратора: «ByeDPI-PC --elevated-host &lt;pipe&gt; &lt;engine-dir&gt;».
/// Запускает по командам основной программы движки, которым нужны права (sing-box для TUN, winws для zapret),
/// и пересылает их вывод. Если основная программа закрылась или упала (канал оборвался), все движки
/// останавливаются: TUN-адаптер исчезает, WinDivert отпускает трафик.
/// Запускать можно только известные движки из папки engine.
/// </summary>
public static class ElevatedHost
{
    public const string Flag = "--elevated-host";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "sing-box", "sing-box.exe", "winws.exe",
    };

    /// <summary>
    /// Стратегии zapret с fooling=ts требуют TCP timestamps, а в Windows они по умолчанию выключены
    /// (так же делает service.bat в zapret-discord-youtube). Безопасная настройка: в Linux включена всегда.
    /// </summary>
    public static string EnableTcpTimestamps()
    {
        if (!OperatingSystem.IsWindows()) return "skip";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "interface", "tcp", "set", "global", "timestamps=enabled" }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0 ? "ok" : "code " + p.ExitCode;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string PipePathForClient(string pipeName) =>
        OperatingSystem.IsWindows() ? pipeName : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) return 2;
        var pipeName = args[1];
        var engineDir = Path.GetFullPath(args[2]);

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000); }
        catch { return 3; }

        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        var writeLock = new SemaphoreSlim(1, 1);
        async Task Send(string line)
        {
            await writeLock.WaitAsync();
            try { await writer.WriteLineAsync(line.Replace('\n', ' ').Replace('\r', ' ')); }
            catch { /* канал закрыт */ }
            finally { writeLock.Release(); }
        }

        var procs = new ConcurrentDictionary<string, ChildProcess>();
        var stopping = new ConcurrentDictionary<string, bool>();

        async Task Stop(string name)
        {
            if (!procs.TryRemove(name, out var p)) return;
            stopping[name] = true;
            await p.StopAsync();
            p.Dispose();
            stopping.TryRemove(name, out _);
        }

        try
        {
            await Send("READY");
            while (await reader.ReadLineAsync() is { } line)
            {
                var parts = line.Split('\t');
                switch (parts[0])
                {
                    case "START" when parts.Length >= 4:
                    {
                        var name = parts[1];
                        var exe = Path.GetFullPath(parts[2]);
                        var cwd = parts[3];
                        if (!exe.StartsWith(engineDir, StringComparison.OrdinalIgnoreCase) || !Allowed.Contains(Path.GetFileName(exe)))
                        {
                            await Send($"FAILED\t{name}\tзапуск {Path.GetFileName(exe)} не разрешён");
                            break;
                        }
                        await Stop(name);
                        try
                        {
                            var p = ChildProcess.Start(exe, parts.Skip(4), name, logOutput: false, workingDirectory: cwd);
                            procs[name] = p;
                            p.Output += l => _ = Send($"LOG\t{name}\t{l}");
                            p.Exited += code =>
                            {
                                if (stopping.ContainsKey(name) || !procs.TryGetValue(name, out var cur) || !ReferenceEquals(cur, p)) return;
                                _ = Send($"EXITED\t{name}\t{code}");
                            };
                            await Send($"STARTED\t{name}\t{p.Id}");
                        }
                        catch (Exception ex)
                        {
                            await Send($"FAILED\t{name}\t{ex.Message}");
                        }
                        break;
                    }
                    case "STOP" when parts.Length >= 2:
                        await Stop(parts[1]);
                        await Send($"STOPPED\t{parts[1]}");
                        break;
                    case "PING":
                        await Send("PONG");
                        break;
                    case "TCPTS":
                        await Send("TCPTS\t\t" + EnableTcpTimestamps());
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            await Send("ERROR\t" + ex.Message);
        }
        finally
        {
            foreach (var name in procs.Keys.ToList()) await Stop(name);
        }
        return 0;
    }
}
