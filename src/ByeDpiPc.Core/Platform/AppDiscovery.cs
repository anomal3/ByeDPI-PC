using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ByeDpiPc.Core.Platform;

public sealed record DiscoveredApp(string Name, string ProcessName, string? Path);

/// <summary>Поиск программ для «Приложений»: ярлыки меню «Пуск» / .desktop / .app и запущенные процессы.</summary>
public static partial class AppDiscovery
{
    private static readonly string[] SkipWords = { "uninstall", "удал", "setup", "readme", "help", "documentation", "website" };

    public static List<DiscoveredApp> Running()
    {
        var self = Environment.ProcessId;
        var result = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* чужой/системный процесс */ }
                    if (OperatingSystem.IsWindows())
                    {
                        if (path == null && p.MainWindowHandle == IntPtr.Zero) continue;
                        if (path != null && path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else if (path == null) continue;
                    var process = OperatingSystem.IsWindows() ? p.ProcessName + ".exe" : p.ProcessName;
                    if (result.ContainsKey(process)) continue;
                    var name = path != null ? FriendlyName(path) ?? p.ProcessName : p.ProcessName;
                    result[process] = new DiscoveredApp(name, process, path);
                }
                catch { }
            }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<DiscoveredApp> Installed()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsStartMenu();
            if (OperatingSystem.IsLinux()) return LinuxDesktopFiles();
            if (OperatingSystem.IsMacOS()) return MacApplications();
        }
        catch (Exception ex)
        {
            Log.Warn("Приложения", "Не удалось получить список программ: " + ex.Message);
        }
        return new();
    }

    public static DiscoveredApp FromExecutable(string path)
    {
        var process = System.IO.Path.GetFileName(path);
        return new DiscoveredApp(FriendlyName(path) ?? System.IO.Path.GetFileNameWithoutExtension(path), process, path);
    }

    private static string? FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<DiscoveredApp> WindowsStartMenu()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return new();
        dynamic shell = Activator.CreateInstance(shellType)!;
        var result = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                var title = System.IO.Path.GetFileNameWithoutExtension(lnk);
                if (SkipWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                string? target;
                try { target = (string?)shell.CreateShortcut(lnk).TargetPath; } catch { continue; }
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                var process = System.IO.Path.GetFileName(target);
                if (SkipWords.Any(w => process.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                result.TryAdd(process, new DiscoveredApp(title, process, target));
            }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<DiscoveredApp> LinuxDesktopFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[] { "/usr/share/applications", "/usr/local/share/applications", System.IO.Path.Combine(home, ".local/share/applications"), "/var/lib/flatpak/exports/share/applications" };
        var result = new Dictionary<string, DiscoveredApp>(StringComparer.Ordinal);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                string? name = null, exec = null;
                bool noDisplay = false;
                foreach (var line in File.ReadLines(file))
                {
                    if (line.StartsWith('[') && line != "[Desktop Entry]" && name != null) break;
                    if (name == null && line.StartsWith("Name=")) name = line[5..];
                    else if (exec == null && line.StartsWith("Exec=")) exec = line[5..];
                    else if (line is "NoDisplay=true" or "Hidden=true") noDisplay = true;
                }
                if (noDisplay || name == null || exec == null) continue;
                var bin = Regex.Split(exec.Trim(), @"\s+")[0].Trim('"');
                if (bin is "env" or "sh" or "flatpak") continue;
                var process = System.IO.Path.GetFileName(bin);
                if (process.Length == 0) continue;
                result.TryAdd(process, new DiscoveredApp(name, process, bin.StartsWith('/') ? bin : null));
            }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<DiscoveredApp> MacApplications()
    {
        var result = new List<DiscoveredApp>();
        foreach (var root in new[] { "/Applications", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var app in Directory.EnumerateDirectories(root, "*.app"))
            {
                var plist = System.IO.Path.Combine(app, "Contents", "Info.plist");
                if (!File.Exists(plist)) continue;
                var m = PlistExecutable().Match(File.ReadAllText(plist));
                if (!m.Success) continue;
                var exe = m.Groups[1].Value;
                result.Add(new DiscoveredApp(System.IO.Path.GetFileNameWithoutExtension(app), exe, System.IO.Path.Combine(app, "Contents", "MacOS", exe)));
            }
        }
        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [GeneratedRegex(@"<key>CFBundleExecutable</key>\s*<string>([^<]+)</string>")]
    private static partial Regex PlistExecutable();
}
