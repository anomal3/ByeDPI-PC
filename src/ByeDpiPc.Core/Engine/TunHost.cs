using System.IO.Pipes;

namespace ByeDpiPc.Core.Engine;

/// <summary>
/// Помощник с правами администратора: запускается как «ByeDPI-PC --tun-host &lt;pipe&gt; &lt;sing-box&gt; &lt;config&gt;»,
/// держит sing-box и слушает команды основной программы по именованному каналу.
/// Если основная программа закрылась или упала (канал оборвался) — sing-box останавливается, TUN убирается.
/// </summary>
public static class TunHost
{
    public const string Flag = "--tun-host";

    public static string PipePathForClient(string pipeName) =>
        OperatingSystem.IsWindows() ? pipeName : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);

    public static async Task<int> RunAsync(string[] args)
    {
        // args: --tun-host <pipe> <sing-box> <config> [<tmp>]
        if (args.Length < 4) return 2;
        var pipeName = args[1];
        var singBox = args[2];
        var config = args[3];

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15000);
        }
        catch
        {
            return 3;
        }

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

        ChildProcess? box = null;
        var stopping = false;

        async Task StartBox()
        {
            stopping = false;
            var current = ChildProcess.Start(singBox, new[] { "run", "-c", config, "--disable-color" }, "sing-box", logOutput: false);
            box = current;
            current.Output += line => _ = Send("LOG " + line);
            current.Exited += code =>
            {
                if (!stopping && ReferenceEquals(box, current)) _ = Send("EXITED " + code);
            };
            await Send("STARTED " + current.Id);
        }

        async Task StopBox()
        {
            stopping = true;
            var b = box;
            box = null;
            if (b != null)
            {
                await b.StopAsync();
                b.Dispose();
            }
        }

        try
        {
            await StartBox();
            while (await reader.ReadLineAsync() is { } cmd)
            {
                switch (cmd.Trim())
                {
                    case "STOP":
                        await StopBox();
                        await Send("STOPPED");
                        return 0;
                    case "RESTART":
                        await StopBox();
                        await StartBox();
                        break;
                    case "PING":
                        await Send("PONG");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            await Send("ERROR " + ex.Message);
        }
        finally
        {
            await StopBox();
        }
        return 0;
    }
}
