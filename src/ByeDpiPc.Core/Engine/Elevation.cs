using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace ByeDpiPc.Core.Engine;

/// <summary>Запуск себя же с правами администратора (UAC / pkexec / osascript).</summary>
public static class Elevation
{
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>Как запустить текущую программу: exe или «dotnet ByeDPI-PC.dll».</summary>
    public static (string File, List<string> Args) SelfCommand()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Неизвестен путь к программе");
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Неизвестен путь к сборке");
            return (path, new List<string> { dll });
        }
        return (path, new List<string>());
    }

    /// <summary>Запускает команду с повышением прав и не ждёт её завершения.</summary>
    public static void StartElevated(string file, IReadOnlyList<string> args)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = string.Join(' ', args.Select(QuoteWindows)),
                };
                Process.Start(psi)?.Dispose();
            }
            else if (OperatingSystem.IsMacOS())
            {
                var cmd = string.Join(' ', new[] { file }.Concat(args).Select(QuoteShell)) + " >/dev/null 2>&1 &";
                var script = $"do shell script \"{cmd.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges";
                var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);
                Process.Start(psi)?.Dispose();
            }
            else
            {
                var psi = new ProcessStartInfo("pkexec") { UseShellExecute = false };
                psi.ArgumentList.Add(file);
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi)?.Dispose();
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new UserFacingException(
                "Запрос прав администратора отклонён.",
                "Режиму TUN нужны права администратора, чтобы создать виртуальный сетевой адаптер. " +
                "Нажмите «Да» в окне контроля учётных записей или переключитесь в режим «Только прокси» в настройках. " +
                "Если включить автозапуск, программа будет стартовать с нужными правами без вопросов.", ex);
        }
    }

    /// <summary>Выполняет команду с повышением прав и ждёт завершения (для schtasks и т.п.).</summary>
    public static async Task<int> RunElevatedAsync(string file, IReadOnlyList<string> args)
    {
        if (IsElevated)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(' ', args.Select(QuoteWindows)),
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new UserFacingException("Запрос прав администратора отклонён.",
                "Чтобы программа запускалась вместе с Windows с правами для режима TUN, нужно один раз подтвердить запрос UAC.", ex);
        }
    }

    public static string QuoteWindows(string a)
    {
        if (a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"')) return a;
        return "\"" + a.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"") + (a.EndsWith('\\') ? "\\" : "") + "\"";
    }

    private static string QuoteShell(string a) => "'" + a.Replace("'", "'\\''") + "'";
}
