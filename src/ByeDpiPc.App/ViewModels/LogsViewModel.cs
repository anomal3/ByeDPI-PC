using System.Collections.ObjectModel;
using Avalonia.Threading;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Testing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public sealed partial class LogsViewModel : ViewModelBase
{
    private const int MaxShown = 2000;
    private readonly AppHost _host;
    private readonly Action<ViewModelBase?> _showDialog;
    private readonly List<LogEntry> _pending = new();
    private readonly DispatcherTimer _flush;

    public ObservableCollection<LogItem> Items { get; } = new();
    public string[] Levels { get; } = { "Все", "Сообщения", "Предупреждения", "Ошибки" };

    [ObservableProperty] private int _levelIndex = 1;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>Просьба к представлению прокрутить список вниз.</summary>
    public event Action? ScrollToEnd;

    public LogsViewModel(AppHost host, Action<ViewModelBase?> showDialog)
    {
        _host = host;
        _showDialog = showDialog;
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        Log.Added += e => { lock (_pending) _pending.Add(e); };
        Rebuild();
    }

    private LogLevel MinLevel => LevelIndex switch { 0 => LogLevel.Debug, 1 => LogLevel.Info, 2 => LogLevel.Warning, _ => LogLevel.Error };

    private bool Pass(LogEntry e) =>
        e.Level >= MinLevel && (Search.Length == 0 || e.Message.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(Search, StringComparison.OrdinalIgnoreCase));

    partial void OnLevelIndexChanged(int value) => Rebuild();
    partial void OnSearchChanged(string value) => Rebuild();

    private void Rebuild()
    {
        lock (_pending) _pending.Clear();
        Items.Clear();
        foreach (var e in Log.Snapshot().Where(Pass).TakeLast(MaxShown)) Items.Add(new LogItem(e));
        if (AutoScroll) ScrollToEnd?.Invoke();
    }

    private void Flush()
    {
        List<LogEntry> batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToList();
            _pending.Clear();
        }
        foreach (var e in batch.Where(Pass)) Items.Add(new LogItem(e));
        while (Items.Count > MaxShown) Items.RemoveAt(0);
        if (AutoScroll) ScrollToEnd?.Invoke();
    }

    [RelayCommand]
    private Task Copy() => Ui.CopyAsync(string.Join(Environment.NewLine, Items.Select(i => i.Entry.ToString())));

    [RelayCommand]
    private void OpenFolder() => Ui.Open(AppPaths.LogsDir);

    [RelayCommand]
    private void Clear()
    {
        Log.Clear();
        Items.Clear();
    }

    [RelayCommand]
    private void Diagnose() => _showDialog(new DiagnosticsViewModel(_host, () => _showDialog(null)));
}

public sealed class LogItem
{
    public LogItem(LogEntry e) => Entry = e;
    public LogEntry Entry { get; }
    public string Time => Entry.Time.ToString("HH:mm:ss");
    public string Source => Entry.Source;
    public string Message => Entry.Message;
    public bool IsWarning => Entry.Level == LogLevel.Warning;
    public bool IsError => Entry.Level == LogLevel.Error;
    public bool IsDebug => Entry.Level == LogLevel.Debug;
}

public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly Action _close;
    private List<DiagnosticItem> _items = new();

    public ObservableCollection<DiagnosticRow> Rows { get; } = new();
    [ObservableProperty] private bool _isRunning;

    public DiagnosticsViewModel(AppHost host, Action close)
    {
        _host = host;
        _close = close;
        _ = Run();
    }

    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning) return;
        IsRunning = true;
        Rows.Clear();
        try
        {
            _items = await Task.Run(() => Diagnostics.RunAsync(_host.Settings, _host.Connection, CancellationToken.None));
            foreach (var i in _items) Rows.Add(new DiagnosticRow(i));
            Log.Info("Диагностика", "\n" + Diagnostics.ToText(_items));
        }
        catch (Exception ex)
        {
            Rows.Add(new DiagnosticRow(new DiagnosticItem(false, "Диагностика не завершилась", ex.Message)));
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private Task Copy() => Ui.CopyAsync(Diagnostics.ToText(_items));

    [RelayCommand]
    private void Close() => _close();
}

public sealed record DiagnosticRow(DiagnosticItem Item)
{
    public string Title => Item.Title;
    public string Details => Item.Details;
    public bool IsOk => Item.Ok == true;
    public bool IsFail => Item.Ok == false;
    public bool IsInfo => Item.Ok == null;
}
