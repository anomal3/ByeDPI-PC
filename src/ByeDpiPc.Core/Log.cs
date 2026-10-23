using System.Text.RegularExpressions;

namespace ByeDpiPc.Core;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} [{Level.ToString().ToUpperInvariant()[..4]}] {Source}: {Message}";
}

/// <summary>Простой журнал: кольцевой буфер в памяти + файл с ротацией по дням.</summary>
public static partial class Log
{
    private const int Capacity = 3000;
    private static readonly object Sync = new();
    private static readonly LinkedList<LogEntry> Buffer = new();
    private static StreamWriter? _writer;
    private static DateOnly _writerDate;

    public static event Action<LogEntry>? Added;

    /// <summary>false — не писать в файл (скриншоты, тесты).</summary>
    public static bool FileEnabled { get; set; } = true;

    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public static void Warn(string source, string message) => Write(LogLevel.Warning, source, message);
    public static void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Sync) return Buffer.ToList();
    }

    public static void Clear()
    {
        lock (Sync) Buffer.Clear();
    }

    /// <summary>Строка из вывода движка: уровень угадывается по тексту.</summary>
    public static void Engine(string source, string line)
    {
        line = AnsiRegex().Replace(line, "").TrimEnd();
        if (line.Length == 0) return;
        var level = line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Error
            : line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warning
            : line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) || line.Contains("TRACE", StringComparison.OrdinalIgnoreCase) ? LogLevel.Debug
            : LogLevel.Info;
        Write(level, source, line);
    }

    public static void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        lock (Sync)
        {
            Buffer.AddLast(entry);
            while (Buffer.Count > Capacity) Buffer.RemoveFirst();
            WriteFile(entry);
        }
        Added?.Invoke(entry);
    }

    private static void WriteFile(LogEntry entry)
    {
        if (!FileEnabled) return;
        try
        {
            var today = DateOnly.FromDateTime(entry.Time);
            if (_writer == null || _writerDate != today)
            {
                _writer?.Dispose();
                var dir = AppPaths.LogsDir;
                _writer = new StreamWriter(Path.Combine(dir, $"byedpi-pc-{today:yyyyMMdd}.log"), append: true) { AutoFlush = true };
                _writerDate = today;
                Cleanup(dir);
            }
            _writer.WriteLine($"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] {entry.Source}: {entry.Message}");
        }
        catch
        {
            // Журнал не должен ронять программу.
        }
    }

    private static void Cleanup(string dir)
    {
        foreach (var file in new DirectoryInfo(dir).GetFiles("byedpi-pc-*.log").OrderByDescending(f => f.Name).Skip(7))
        {
            try { file.Delete(); } catch { }
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}
