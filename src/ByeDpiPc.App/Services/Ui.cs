using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ByeDpiPc.Core;

namespace ByeDpiPc.App.Services;

/// <summary>Доступ к окну из ViewModel: диалоги файлов, буфер обмена, открытие ссылок, иконки.</summary>
public static class Ui
{
    public static TopLevel? Root { get; set; }

    private static readonly FilePickerFileType TextFiles = new("Текстовые файлы") { Patterns = new[] { "*.txt" } };

    public static async Task<string?> PickExecutableAsync()
    {
        if (Root == null) return null;
        var types = OperatingSystem.IsWindows()
            ? new[] { new FilePickerFileType("Программы") { Patterns = new[] { "*.exe" } } }
            : new[] { FilePickerFileTypes.All };
        var files = await Root.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите программу",
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> ReadTextFileAsync()
    {
        if (Root == null) return null;
        var files = await Root.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт списка",
            AllowMultiple = false,
            FileTypeFilter = new[] { TextFiles, FilePickerFileTypes.All },
        });
        var file = files.FirstOrDefault();
        if (file == null) return null;
        await using var s = await file.OpenReadAsync();
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }

    public static async Task SaveTextFileAsync(string suggestedName, string content)
    {
        if (Root == null) return;
        var file = await Root.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт списка",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = new[] { TextFiles },
        });
        if (file == null) return;
        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s);
        await w.WriteAsync(content);
    }

    public static async Task CopyAsync(string text)
    {
        if (Root?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    public static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            else
                Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", new[] { target })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Программа", $"Не удалось открыть {target}: {ex.Message}");
        }
    }

    private static readonly Dictionary<string, Bitmap?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Иконка программы по пути к .exe (только Windows), иначе null.</summary>
    public static Bitmap? ExeIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows()) return null;
        lock (IconCache)
        {
            if (IconCache.TryGetValue(path, out var cached)) return cached;
        }
        Bitmap? bmp = null;
        try
        {
            if (File.Exists(path) && OperatingSystem.IsWindowsVersionAtLeast(6, 1)) bmp = ExtractIcon(path);
        }
        catch
        {
            bmp = null;
        }
        lock (IconCache) IconCache[path] = bmp;
        return bmp;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
    private static Bitmap? ExtractIcon(string path)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon == null) return null;
        using var gdi = icon.ToBitmap();
        using var ms = new MemoryStream();
        gdi.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    public static StreamGeometry? Geometry(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var res) == true ? res as StreamGeometry : null;

    public static Color BrandColor(string key) =>
        Avalonia.Application.Current?.TryFindResource("BrandColor." + key, out var res) == true && res is Color c ? c : Color.Parse("#7C8798");
}
