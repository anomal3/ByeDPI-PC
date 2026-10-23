using System.IO.Pipes;
using ByeDpiPc.Core;

namespace ByeDpiPc.App.Services;

/// <summary>Одна копия программы: повторный запуск просто показывает уже открытое окно.</summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = "ByeDPI-PC-" + Environment.UserName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public static event Action? ActivationRequested;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _ = ListenAsync();
    }

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, "Local\\" + Id, out var created);
        if (created) return new SingleInstance(mutex);
        try
        {
            // Предыдущая копия могла завершиться аварийно и оставить мьютекс «брошенным».
            if (mutex.WaitOne(0)) return new SingleInstance(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstance(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public static void ActivateExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            w.WriteLine("SHOW");
        }
        catch (Exception ex)
        {
            Log.Debug("Программа", "Не удалось активировать запущенную копию: " + ex.Message);
        }
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var r = new StreamReader(server);
                if (await r.ReadLineAsync() == "SHOW") ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("Программа", "Канал активации: " + ex.Message);
                await Task.Delay(1000);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
