namespace ByeDpiPc.Core;

public static class AppPaths
{
    public const string AppName = "ByeDPI-PC";

    /// <summary>Папка настроек. Переменная BYEDPI_PC_HOME переопределяет её (тесты, скриншоты, портативный режим).</summary>
    public static string ConfigDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("BYEDPI_PC_HOME");
            var dir = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string LogsDir => Ensure(Path.Combine(ConfigDir, "logs"));
    public static string RuntimeDir => Ensure(Path.Combine(ConfigDir, "run"));
    public static string SingBoxConfig => Path.Combine(RuntimeDir, "sing-box.json");
    public static string ProxyMarkerFile => Path.Combine(RuntimeDir, "system-proxy.json");

    /// <summary>Папка движков; BYEDPI_PC_ENGINE_DIR переопределяет её (разработка).</summary>
    public static string EngineDir =>
        Environment.GetEnvironmentVariable("BYEDPI_PC_ENGINE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "engine");

    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Ищет движок рядом с программой (engine/), затем в PATH.</summary>
    public static string? FindEngine(string name)
    {
        var file = ExeName(name);
        var local = Path.Combine(EngineDir, file);
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
