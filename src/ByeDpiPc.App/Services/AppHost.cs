using Avalonia.Threading;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Testing;
using ByeDpiPc.Core.Update;
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

    /// <summary>Корректный выход из программы (задаёт App).</summary>
    public Func<Task>? ExitApp { get; set; }

    /// <summary>Режим скриншотов: ничего не запускаем и не сохраняем.</summary>
    public bool DemoMode { get; }

    [ObservableProperty] private ConnectionState _state = ConnectionState.Disconnected;
    [ObservableProperty] private bool _isTuning;
    [ObservableProperty] private TunePhase _tunePhase;
    [ObservableProperty] private string _tuneText = "";
    [ObservableProperty] private double _tuneProgress;
    [ObservableProperty] private bool _tuneHasProgress;
    /// <summary>Запомненная стратегия перестала открывать сайты — показать совет подобрать заново.</summary>
    [ObservableProperty] private bool _strategyBroken;
    [ObservableProperty] private UpdateInfo? _update;
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatus = "";

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

    /// <summary>Правила изменились извне (импорт настроек).</summary>
    public event Action? RulesChanged;
    public void NotifyRulesChanged() => RulesChanged?.Invoke();

    public async Task ConnectAsync()
    {
        if (DemoMode) return;
        StrategyBroken = false;
        await Connection.ConnectAsync(Settings);
        if (!Connection.IsConnected || !Settings.AutoTune) return;

        // Подобранная раньше стратегия запоминается: при подключении — только быстрая проверка на 2–3 сайтах.
        var active = Settings.GetActiveStrategy();
        var tuned = Settings.GetTuned();
        if (tuned != null && active != null && tuned.Arguments == active.Arguments)
            _ = QuickCheckAsync(tuned);
        else
            _ = RunAutoTuneAsync(force: false);
    }

    private async Task QuickCheckAsync(TunedStrategy tuned)
    {
        try
        {
            await Task.Delay(1500); // дать движку и DNS «прогреться»
            if (!Connection.IsConnected || IsTuning) return;
            var ok = await Task.Run(() => AutoTuner.QuickCheckAsync(Settings, tuned.Sites, Settings.SocksPort, CancellationToken.None));
            StrategyBroken = !ok;
            if (!ok) Log.Warn("Автоподбор", "Запомненная стратегия перестала открывать сайты — предлагаю подобрать заново");
        }
        catch (Exception ex)
        {
            Log.Debug("Автоподбор", "Быстрая проверка: " + ex.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        CancelTune();
        StrategyBroken = false;
        await Connection.DisconnectAsync();
    }

    public async Task ToggleAsync()
    {
        if (Connection.State is ConnectionState.Connected or ConnectionState.Connecting) await DisconnectAsync();
        else await ConnectAsync();
    }

    public void SetActiveStrategy(string name)
    {
        if (Settings.GetActiveName(Settings.Engine) == name) return;
        Settings.SetActiveName(Settings.Engine, name);
        StrategyBroken = false;
        Save();
        NotifyStrategiesChanged();
    }

    /// <summary>
    /// Проверка обхода и, при необходимости, подбор стратегии. force = сразу перебирать стратегии.
    /// Для zapret основной winws на время подбора приостанавливается.
    /// </summary>
    public async Task RunAutoTuneAsync(bool force)
    {
        if (IsTuning || DemoMode) return;
        _tuneCts = new CancellationTokenSource();
        IsTuning = true;
        StrategyBroken = false;
        TuneHasProgress = false;
        TuneProgress = 0;
        var engine = Settings.Engine;
        var suspended = false;
        try
        {
            var progress = new Progress<TuneStatus>(s =>
            {
                TunePhase = s.Phase;
                TuneText = s.Text;
                TuneHasProgress = s.Progress != null;
                TuneProgress = s.Progress ?? 0;
            });
            if (engine == EngineKind.Zapret && Connection.IsConnected)
            {
                await Connection.SuspendAsync();
                suspended = true;
            }
            var port = Connection.IsConnected && engine == EngineKind.ByeDpi ? Settings.SocksPort : (int?)null;
            var outcome = await Task.Run(() => new AutoTuner().RunAsync(Settings, port, force, progress, _tuneCts.Token));

            if (outcome.NewArguments != null)
            {
                var profile = Settings.EngineStrategies(engine).FirstOrDefault(p => p.Name == Presets.AutoProfileName);
                if (profile == null)
                {
                    profile = new StrategyProfile(Presets.AutoProfileName, outcome.NewArguments, engine);
                    Settings.Strategies.Insert(0, profile);
                }
                profile.Arguments = outcome.NewArguments;
                profile.GoogleArguments = outcome.GoogleArguments;
                Settings.SetActiveName(engine, profile.Name);
                Log.Info("Автоподбор", "Применена стратегия: " + outcome.NewArguments);
            }
            if (outcome.Working && Settings.GetActiveStrategy(engine) is { } active)
            {
                Settings.Tuned.RemoveAll(t => t.Engine == engine);
                Settings.Tuned.Add(new TunedStrategy
                {
                    Engine = engine, Arguments = active.Arguments, Sites = outcome.OpenedSites.Take(6).ToList(), Time = DateTime.Now,
                });
            }
            Settings.LastTuned = DateTime.Now;
            Settings.LastTuneSummary = outcome.Summary;
            TunePhase = outcome.Working ? TunePhase.Done : TunePhase.Failed;
            TuneText = outcome.Summary;
            Save(apply: false);
            if (outcome.NewArguments != null) NotifyStrategiesChanged();
            if (suspended)
            {
                suspended = false;
                await Connection.ResumeAsync(Settings);
            }
            else if (outcome.NewArguments != null)
            {
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
            if (suspended) await Connection.ResumeAsync(Settings);
            IsTuning = false;
            _tuneCts?.Dispose();
            _tuneCts = null;
        }
    }

    /// <summary>Для страницы «Подбор»: приостановить/возобновить основной winws.</summary>
    public Task SuspendEngineAsync() => Settings.Engine == EngineKind.Zapret ? Connection.SuspendAsync() : Task.CompletedTask;
    public Task ResumeEngineAsync() => Connection.ResumeAsync(Settings);

    // ---- Обновления ----

    public async Task CheckUpdatesAsync(bool manual)
    {
        if (DemoMode || IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        try
        {
            Update = await UpdateService.CheckAsync();
            UpdateStatus = Update != null
                ? $"Доступна версия {Update.Version}"
                : $"У вас последняя версия ({UpdateService.CurrentVersion})";
            if (Update != null) Log.Info("Обновление", $"Доступна версия {Update.Tag}");
        }
        catch (Exception ex)
        {
            UpdateStatus = "Не удалось проверить обновления: " + ex.Message;
            if (manual) Log.Warn("Обновление", UpdateStatus);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>Скачать, установить и перезапуститься. exit — корректный выход из программы.</summary>
    public async Task InstallUpdateAsync(Func<Task> exit)
    {
        if (Update == null || IsUpdating) return;
        IsUpdating = true;
        UpdateProgress = 0;
        try
        {
            UpdateStatus = "Скачиваю обновление…";
            await UpdateService.InstallAsync(Update, new Progress<double>(p => UpdateProgress = p));
            UpdateStatus = "Перезапуск…";
            await exit();
        }
        catch (Exception ex)
        {
            UpdateStatus = ex is UserFacingException u ? u.Message + " " + u.Advice : "Не удалось обновиться: " + ex.Message;
            Log.Error("Обновление", UpdateStatus);
            IsUpdating = false;
        }
    }

    public void CancelTune() => _tuneCts?.Cancel();

    /// <summary>Для скриншотов: показать произвольное состояние.</summary>
    public void SetDemoState(ConnectionState state) => State = state;
}
