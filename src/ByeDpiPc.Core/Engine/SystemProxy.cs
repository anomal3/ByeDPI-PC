using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ByeDpiPc.Core.Engine;

/// <summary>
/// Системный прокси: включаем при подключении в режиме «Только прокси» и обязательно возвращаем как было.
/// Прежние значения сохраняются в файл, чтобы восстановить их даже после падения программы.
/// </summary>
public static class SystemProxy
{
    private const string Source = "Системный прокси";
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private sealed class Saved
    {
        public int? ProxyEnable { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyOverride { get; set; }
        public string? AutoConfigUrl { get; set; }
        public string? GnomeMode { get; set; }
        public string? GnomeAutoconfig { get; set; }
        public List<string>? MacServices { get; set; }
    }

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Включает PAC (pacUrl) или SOCKS-прокси на всю систему.</summary>
    public static void Apply(string? pacUrl, int socksPort)
    {
        if (!File.Exists(AppPaths.ProxyMarkerFile)) SaveCurrent();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, true);
                if (pacUrl != null)
                {
                    key.SetValue("AutoConfigURL", $"{pacUrl}?v={DateTime.UtcNow.Ticks}", RegistryValueKind.String);
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                }
                else
                {
                    key.DeleteValue("AutoConfigURL", false);
                    key.SetValue("ProxyServer", $"socks=127.0.0.1:{socksPort}", RegistryValueKind.String);
                    key.SetValue("ProxyOverride", "<local>;localhost;127.*;10.*;192.168.*", RegistryValueKind.String);
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                }
                NotifyWindows();
            }
            else if (OperatingSystem.IsLinux())
            {
                if (pacUrl != null)
                {
                    Run("gsettings", "set", "org.gnome.system.proxy", "autoconfig-url", pacUrl);
                    Run("gsettings", "set", "org.gnome.system.proxy", "mode", "auto");
                }
                else
                {
                    Run("gsettings", "set", "org.gnome.system.proxy.socks", "host", "127.0.0.1");
                    Run("gsettings", "set", "org.gnome.system.proxy.socks", "port", socksPort.ToString());
                    Run("gsettings", "set", "org.gnome.system.proxy", "mode", "manual");
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                foreach (var service in MacServices())
                {
                    if (pacUrl != null)
                    {
                        Run("networksetup", "-setautoproxyurl", service, pacUrl);
                        Run("networksetup", "-setautoproxystate", service, "on");
                    }
                    else
                    {
                        Run("networksetup", "-setsocksfirewallproxy", service, "127.0.0.1", socksPort.ToString());
                        Run("networksetup", "-setsocksfirewallproxystate", service, "on");
                    }
                }
            }
            Log.Info(Source, pacUrl != null ? "Включён PAC: " + pacUrl : $"Включён SOCKS-прокси 127.0.0.1:{socksPort}");
        }
        catch (Exception ex)
        {
            Log.Error(Source, "Не удалось включить: " + ex.Message);
        }
    }

    /// <summary>Возвращает системный прокси в состояние до подключения. Без сохранённых данных ничего не делает.</summary>
    public static void Restore()
    {
        var marker = AppPaths.ProxyMarkerFile;
        if (!File.Exists(marker)) return;
        Saved? saved = null;
        try { saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(marker)); }
        catch (Exception ex) { Log.Warn(Source, "Повреждён файл с прежними настройками: " + ex.Message); }
        saved ??= new Saved();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, true);
                SetOrDelete(key, "AutoConfigURL", saved.AutoConfigUrl);
                SetOrDelete(key, "ProxyServer", saved.ProxyServer);
                SetOrDelete(key, "ProxyOverride", saved.ProxyOverride);
                key.SetValue("ProxyEnable", saved.ProxyEnable ?? 0, RegistryValueKind.DWord);
                NotifyWindows();
            }
            else if (OperatingSystem.IsLinux())
            {
                Run("gsettings", "set", "org.gnome.system.proxy", "mode", saved.GnomeMode ?? "none");
                if (saved.GnomeAutoconfig != null) Run("gsettings", "set", "org.gnome.system.proxy", "autoconfig-url", saved.GnomeAutoconfig);
            }
            else if (OperatingSystem.IsMacOS())
            {
                foreach (var service in saved.MacServices ?? MacServices())
                {
                    Run("networksetup", "-setautoproxystate", service, "off");
                    Run("networksetup", "-setsocksfirewallproxystate", service, "off");
                }
            }
            File.Delete(marker);
            Log.Info(Source, "Восстановлены прежние настройки прокси");
        }
        catch (Exception ex)
        {
            Log.Error(Source, "Не удалось восстановить: " + ex.Message);
        }
    }

    private static void SaveCurrent()
    {
        var saved = new Saved();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettings);
                saved.ProxyEnable = key?.GetValue("ProxyEnable") as int?;
                saved.ProxyServer = key?.GetValue("ProxyServer") as string;
                saved.ProxyOverride = key?.GetValue("ProxyOverride") as string;
                saved.AutoConfigUrl = key?.GetValue("AutoConfigURL") as string;
                // Если остался наш же адрес от прошлого сеанса — не сохраняем его как «прежний».
                if (saved.AutoConfigUrl?.StartsWith("http://127.0.0.1:") == true && saved.AutoConfigUrl.Contains("/proxy.pac"))
                    saved.AutoConfigUrl = null;
            }
            else if (OperatingSystem.IsLinux())
            {
                saved.GnomeMode = Run("gsettings", "get", "org.gnome.system.proxy", "mode")?.Trim().Trim('\'');
                saved.GnomeAutoconfig = Run("gsettings", "get", "org.gnome.system.proxy", "autoconfig-url")?.Trim().Trim('\'');
            }
            else if (OperatingSystem.IsMacOS())
            {
                saved.MacServices = MacServices();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Source, "Не удалось прочитать текущие настройки: " + ex.Message);
        }
        File.WriteAllText(AppPaths.ProxyMarkerFile, JsonSerializer.Serialize(saved));
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (value == null) key.DeleteValue(name, false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static List<string> MacServices()
    {
        var output = Run("networksetup", "-listallnetworkservices") ?? "";
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("An asterisk") && !l.StartsWith('*'))
            .ToList();
    }

    private static string? Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch (Exception ex)
        {
            Log.Debug(Source, $"{file}: {ex.Message}");
            return null;
        }
    }

    private static void NotifyWindows()
    {
        InternetSetOption(IntPtr.Zero, 39 /* SETTINGS_CHANGED */, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /* REFRESH */, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);
}
