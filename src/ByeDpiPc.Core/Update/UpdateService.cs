using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ByeDpiPc.Core.Engine;

namespace ByeDpiPc.Core.Update;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string HtmlUrl, string? AssetUrl, string? AssetName, long AssetSize);

/// <summary>
/// Обновления из релизов GitHub: проверка последней версии, скачивание архива под текущую платформу
/// и замена файлов программы скриптом, который ждёт её завершения и запускает новую версию.
/// </summary>
public static class UpdateService
{
    public const string Repo = "anomal3/ByeDPI-PC";
    private const string Source = "Обновление";

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    public static string Rid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return $"{os}-{arch}";
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ByeDPI-PC", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Последний релиз, если он новее текущей версии, иначе null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        if (version <= CurrentVersion) return null;

        string? url = null, name = null;
        long size = 0;
        var ext = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            if (n.Contains(Rid, StringComparison.OrdinalIgnoreCase) && n.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                url = a.GetProperty("browser_download_url").GetString();
                name = n;
                size = a.GetProperty("size").GetInt64();
                break;
            }
        }
        return new UpdateInfo(version, tag, root.GetProperty("body").GetString() ?? "",
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases", url, name, size);
    }

    /// <summary>
    /// Скачивает и распаковывает обновление, затем запускает скрипт замены файлов.
    /// После успешного вызова программу нужно закрыть — скрипт дождётся этого и запустит новую версию.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (info.AssetUrl == null)
            throw new UserFacingException("Для вашей системы нет готовой сборки в релизе.", "Скачайте обновление вручную со страницы релиза: " + info.HtmlUrl);

        var work = Path.Combine(Path.GetTempPath(), $"byedpi-pc-update-{info.Version}");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var archive = Path.Combine(work, info.AssetName ?? "update");

        Log.Info(Source, $"Скачиваю {info.AssetName}");
        using (var http = CreateClient())
        {
            http.Timeout = TimeSpan.FromMinutes(15);
            using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(archive);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        var extracted = Path.Combine(work, "new");
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, extracted, overwriteFiles: true);
        }
        else
        {
            Directory.CreateDirectory(extracted);
            await using var fs = File.OpenRead(archive);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, extracted, overwriteFiles: true, ct);
        }

        var exeName = AppPaths.ExeName("ByeDPI-PC");
        var newRoot = Directory.EnumerateFiles(extracted, exeName, SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                      ?? throw new UserFacingException("Архив обновления повреждён.", "Скачайте обновление вручную: " + info.HtmlUrl);
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exe = Path.Combine(appDir, exeName);
        StartReplaceScript(work, newRoot!, appDir, exe);
        Log.Info(Source, $"Обновление до {info.Tag} подготовлено, перезапуск");
    }

    private static void StartReplaceScript(string work, string newRoot, string appDir, string exe)
    {
        var pid = Environment.ProcessId;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(work, "update.ps1");
            File.WriteAllText(script, $$"""
                $ErrorActionPreference = 'Continue'
                while (Get-Process -Id {{pid}} -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 400 }
                Start-Sleep -Seconds 1
                for ($i = 0; $i -lt 5; $i++) {
                    try { Copy-Item -Path '{{Esc(newRoot)}}\*' -Destination '{{Esc(appDir)}}' -Recurse -Force -ErrorAction Stop; break }
                    catch { Start-Sleep -Seconds 2 }
                }
                Start-Process -FilePath '{{Esc(exe)}}'
                Start-Sleep -Seconds 2
                Remove-Item -LiteralPath '{{Esc(work)}}' -Recurse -Force -ErrorAction SilentlyContinue
                """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script };
            if (CanWrite(appDir) || Elevation.IsElevated)
            {
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi)?.Dispose();
            }
            else
            {
                Elevation.StartElevated("powershell.exe", args);
            }
        }
        else
        {
            var script = Path.Combine(work, "update.sh");
            File.WriteAllText(script, $"""
                #!/bin/sh
                while kill -0 {pid} 2>/dev/null; do sleep 0.4; done
                sleep 1
                cp -Rf "{newRoot}/." "{appDir}/"
                chmod +x "{exe}" "{appDir}/engine/"* 2>/dev/null
                nohup "{exe}" >/dev/null 2>&1 &
                rm -rf "{work}"
                """);
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add(script);
            Process.Start(psi)?.Dispose();
        }
    }

    private static string Esc(string path) => path.Replace("'", "''");

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
