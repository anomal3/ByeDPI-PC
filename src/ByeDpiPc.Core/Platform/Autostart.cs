using System.Diagnostics;
using System.Security;
using System.Text;
using ByeDpiPc.Core.Engine;
using Microsoft.Win32;

namespace ByeDpiPc.Core.Platform;

/// <summary>
/// Автозапуск при входе в систему.
/// Windows: задача Планировщика «ByeDPI-PC» с наивысшими правами — программа стартует без запроса UAC,
/// и режим TUN поднимается сам. Если пользователь отказал в UAC — запасной вариант через HKCU\…\Run.
/// Linux: ~/.config/autostart, macOS: ~/Library/LaunchAgents.
/// </summary>
public static class Autostart
{
    public const string TaskName = "ByeDPI-PC";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Source = "Автозапуск";

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows()) return TaskExists() || RunKeyExists();
        if (OperatingSystem.IsLinux()) return File.Exists(LinuxFile);
        if (OperatingSystem.IsMacOS()) return File.Exists(MacFile);
        return false;
    }

    /// <returns>Текст предупреждения, если включилось не так, как хотелось, иначе null.</returns>
    public static async Task<string?> EnableAsync()
    {
        var (file, args) = Elevation.SelfCommand();
        args.Add("--tray");
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var xml = Path.Combine(AppPaths.RuntimeDir, "autostart-task.xml");
                await File.WriteAllTextAsync(xml, TaskXml(file, string.Join(' ', args.Select(Elevation.QuoteWindows))), Encoding.Unicode);
                var code = await Elevation.RunElevatedAsync("schtasks.exe", new[] { "/Create", "/TN", TaskName, "/XML", xml, "/F" });
                File.Delete(xml);
                if (code == 0)
                {
                    DeleteRunKey();
                    Log.Info(Source, "Создана задача Планировщика (запуск с правами администратора)");
                    return null;
                }
                Log.Warn(Source, $"schtasks вернул код {code}");
            }
            catch (UserFacingException ex)
            {
                Log.Warn(Source, ex.Message);
            }
            SetRunKey(file, args);
            return "Автозапуск включён без прав администратора: в режиме TUN при входе в Windows будет запрос UAC.";
        }
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxFile)!);
            var exec = string.Join(' ', new[] { file }.Concat(args).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            await File.WriteAllTextAsync(LinuxFile,
                $"[Desktop Entry]\nType=Application\nName=ByeDPI PC\nExec={exec}\nX-GNOME-Autostart-enabled=true\nTerminal=false\n");
            return null;
        }
        if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacFile)!);
            var items = string.Join("", new[] { file }.Concat(args).Select(a => $"<string>{SecurityElement.Escape(a)}</string>"));
            await File.WriteAllTextAsync(MacFile, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                <key>Label</key><string>io.github.byedpi-pc</string>
                <key>ProgramArguments</key><array>{items}</array>
                <key>RunAtLoad</key><true/>
                </dict></plist>
                """);
            return null;
        }
        return "Автозапуск на этой системе не поддерживается.";
    }

    public static async Task DisableAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            DeleteRunKey();
            if (TaskExists())
            {
                try { await Elevation.RunElevatedAsync("schtasks.exe", new[] { "/Delete", "/TN", TaskName, "/F" }); }
                catch (UserFacingException ex) { Log.Warn(Source, ex.Message); }
            }
        }
        else if (OperatingSystem.IsLinux()) File.Delete(LinuxFile);
        else if (OperatingSystem.IsMacOS()) File.Delete(MacFile);
    }

    private static string LinuxFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "byedpi-pc.desktop");
    private static string MacFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "io.github.byedpi-pc.plist");

    private static bool TaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/Query", "/TN", TaskName }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunKeyExists()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(TaskName) != null;
    }

    private static void SetRunKey(string file, IEnumerable<string> args)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        key.SetValue(TaskName, string.Join(' ', new[] { file }.Concat(args).Select(a => "\"" + a + "\"")));
    }

    private static void DeleteRunKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(TaskName, false);
    }

    private static string TaskXml(string command, string arguments)
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>ByeDPI PC: обход DPI при входе в систему</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(command)}</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
