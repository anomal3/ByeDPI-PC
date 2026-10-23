using Avalonia.Threading;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Testing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ByeDpiPc.App.Services;

/// <summary>
/// Общее состояние программы: настройки, подключение и автоподбор стратегии.
/// Все события отсюда приходят в UI-поток.
/// </summary>
public sealed partial class AppHost : ObservableObject
{
    public static AppHost? Current { get; private set; }

    private readonly SettingsStore _store;
    private readonly DispatcherTimer _applyTimer;
    private CancellationTokenSource? _tuneCts;

    public AppSettings Settings { get; }
    public ConnectionManager Connection { get; } = new();

    /// <summary>Режим скриншотов: ничего не запускаем и не сохраняем.</summary>
    public bool DemoMode { get; }

    [ObservableProperty] private ConnectionState _state = ConnectionState.Disconnected;
    [ObservableProperty] private bool _isTuning;
    [ObservableProperty] private TunePhase _tunePhase;
    [ObservableProperty] private string _tuneText = "";
    [ObservableProperty] private double _tuneProgress;
    [ObservableProperty] private bool _tuneHasProgress;

    /// <summary>Настройки изменены (сохранены).</summary>
    public event Action? SettingsChanged;
    /// <summary>Изменился список стратегий или активная стратегия.</summary>
    public event Action? StrategiesChanged;

    public AppHost(SettingsStore store, AppSettings settings, bool demoMode = false)
    {
        _store = store;
        Settings = settings;
        DemoMode = demoMode;
        Current = this;
        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _applyTimer.Tick += async (_, _) =>
        {
            _applyTimer.Stop();
            await Connection.ApplyAsync(Settings);
        };
        Connection.StateChanged += () => Dispatcher.UIThread.Post(() => State = Connection.State);
    }

    /// <summary>Сохранить настройки и (если подключено) применить их через небольшую паузу.</summary>
    public void Save(bool apply = true)
    {
        if (!DemoMode)
        {
            try { _store.Save(Settings); }
            catch (Exception ex) { Log.Error("Настройки", "Не удалось сохранить: " + ex.Message); }
        }
        SettingsChanged?.Invoke();
        if (apply && Connection.IsConnected)
        {
            _applyTimer.Stop();
            _applyTimer.Start();
        }
    }

    public void NotifyStrategiesChanged() => StrategiesChanged?.Invoke();

    public async Task ConnectAsync()
    {
        if (DemoMode) return;
        await Connection.ConnectAsync(Settings);
        if (Connection.IsConnected && Settings.AutoTune) _ = RunAutoTuneAsync(force: false);
    }

    public async Task DisconnectAsync()
    {
        CancelTune();
        await Connection.DisconnectAsync();
    }

    public async Task ToggleAsync()
    {
        if (Connection.State is ConnectionState.Connected or ConnectionState.Connecting) await DisconnectAsync();
        else await ConnectAsync();
    }

    public void SetActiveStrategy(string name)
    {
        if (Settings.ActiveStrategy == name) return;
        Settings.ActiveStrategy = name;
        Save();
        NotifyStrategiesChanged();
    }

    /// <summary>
    /// Проверка обхода и, при необходимости, подбор стратегии. force = сразу перебирать стратегии.
    /// </summary>
    public async Task RunAutoTuneAsync(bool force)
    {
        if (IsTuning || DemoMode) return;
        _tuneCts = new CancellationTokenSource();
        IsTuning = true;
        TuneHasProgress = false;
        TuneProgress = 0;
        try
        {
            var progress = new Progress<TuneStatus>(s =>
            {
                TunePhase = s.Phase;
                TuneText = s.Text;
                TuneHasProgress = s.Progress != null;
                TuneProgress = s.Progress ?? 0;
            });
            var port = Connection.IsConnected ? Settings.SocksPort : (int?)null;
            var outcome = await Task.Run(() => new AutoTuner().RunAsync(Settings, port, force, progress, _tuneCts.Token));

            if (outcome.NewArguments != null)
            {
                var profile = Settings.Strategies.FirstOrDefault(p => p.Name == Presets.AutoProfileName);
                if (profile == null)
                {
                    profile = new StrategyProfile(Presets.AutoProfileName, outcome.NewArguments);
                    Settings.Strategies.Insert(0, profile);
                }
                profile.Arguments = outcome.NewArguments;
                Settings.ActiveStrategy = profile.Name;
                Log.Info("Автоподбор", "Применена стратегия: " + outcome.NewArguments);
            }
            Settings.LastTuned = DateTime.Now;
            Settings.LastTuneSummary = outcome.Summary;
            TunePhase = outcome.Working ? TunePhase.Done : TunePhase.Failed;
            TuneText = outcome.Summary;
            Save(apply: false);
            if (outcome.NewArguments != null)
            {
                NotifyStrategiesChanged();
                await Connection.ApplyAsync(Settings);
            }
        }
        catch (OperationCanceledException)
        {
            TunePhase = TunePhase.Idle;
            TuneText = "Подбор остановлен.";
        }
        catch (Exception ex)
        {
            TunePhase = TunePhase.Failed;
            TuneText = "Подбор не удался: " + ex.Message;
            Log.Error("Автоподбор", ex.ToString());
        }
        finally
        {
            IsTuning = false;
            _tuneCts?.Dispose();
            _tuneCts = null;
        }
    }

    public void CancelTune() => _tuneCts?.Cancel();

    /// <summary>Для скриншотов: показать произвольное состояние.</summary>
    public void SetDemoState(ConnectionState state) => State = state;
}
