using System.Text.Encodings.Web;
using System.Text.Json;

namespace ByeDpiPc.Core;

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Файл настроек локальный и не встраивается в HTML — экранировать «+» и кириллицу незачем.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _lock = new();

    public SettingsStore(string? path = null) => _path = path ?? AppPaths.SettingsFile;

    public AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(_path))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn("Настройки", $"Не удалось прочитать {_path}: {ex.Message}. Используются значения по умолчанию.");
            TryBackup();
        }
        settings ??= new AppSettings();
        Presets.EnsureDefaults(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private void TryBackup()
    {
        try { File.Copy(_path, _path + ".broken", overwrite: true); } catch { /* не критично */ }
    }
}
