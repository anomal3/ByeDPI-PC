using ByeDpiPc.Core.Strategy;

namespace ByeDpiPc.Core.Engine;

/// <summary>Запуск ciadpi (byedpi) как локального SOCKS5-прокси.</summary>
public static class CiadpiEngine
{
    public const string Source = "ciadpi";

    public static string? FindExecutable() => AppPaths.FindEngine("ciadpi");

    public static string RequireExecutable() =>
        FindExecutable() ?? throw new UserFacingException(
            "Не найден движок ciadpi.",
            "Скачайте сборку программы целиком (в архиве есть папка engine) или запустите scripts/fetch-engines — он положит ciadpi рядом с программой.");

    /// <summary>Запускает ciadpi и ждёт, пока порт начнёт принимать подключения.</summary>
    public static async Task<ChildProcess> StartAsync(string strategy, int port, string fakeSni, bool logOutput = true, string source = Source, CancellationToken ct = default)
    {
        var exe = RequireExecutable();
        var args = CiadpiCommand.BuildArguments(strategy, port, fakeSni);
        if (logOutput) Log.Info(source, $"Запуск: ciadpi {CiadpiCommand.Join(args)}");

        var lastLines = new Queue<string>();
        var child = ChildProcess.Start(exe, args, source, logOutput);
        child.Output += line =>
        {
            lock (lastLines)
            {
                lastLines.Enqueue(line);
                if (lastLines.Count > 5) lastLines.Dequeue();
            }
        };

        var ok = await NetUtil.WaitForPortAsync(port, TimeSpan.FromSeconds(5), () => child.HasExited, ct);
        if (!ok)
        {
            await child.StopAsync();
            child.Dispose();
            string details;
            lock (lastLines) details = string.Join("\n", lastLines);
            throw new UserFacingException(
                "ciadpi не запустился с этой стратегией.",
                (details.Length > 0 ? "Ответ ciadpi: " + details + "\n\n" : "") +
                "Проверьте аргументы стратегии в «Редакторе» или выберите другую. Также убедитесь, что порт " + port + " не занят другой программой.");
        }
        return child;
    }

    /// <summary>Пробный запуск стратегии на свободном порту (кнопка «Проверить» в редакторе).</summary>
    public static async Task<string?> ValidateAsync(string strategy, string fakeSni, CancellationToken ct = default)
    {
        var port = NetUtil.GetFreePort();
        try
        {
            using var child = await StartAsync(strategy, port, fakeSni, logOutput: false, ct: ct);
            await Task.Delay(400, ct);
            if (child.HasExited) return "ciadpi завершился сразу после запуска.";
            await child.StopAsync();
            return null;
        }
        catch (UserFacingException ex)
        {
            return ex.Message + "\n" + ex.Advice;
        }
    }
}

/// <summary>Ошибка с понятным текстом и советом «Что делать».</summary>
public sealed class UserFacingException : Exception
{
    public string Advice { get; }
    public UserFacingException(string message, string advice, Exception? inner = null) : base(message, inner) => Advice = advice;
}
