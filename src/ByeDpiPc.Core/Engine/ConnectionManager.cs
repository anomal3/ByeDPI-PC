using ByeDpiPc.Core.Routing;

namespace ByeDpiPc.Core.Engine;

public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting, Error }

/// <summary>
/// Оркестратор: ciadpi → (TUN через sing-box | PAC + системный прокси). Следит за процессами,
/// перезапускает упавший ciadpi и всегда убирает за собой TUN и системный прокси.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private const string Source = "Подключение";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TunController _tun = new();
    private ChildProcess? _ciadpi;
    private PacServer? _pac;
    private AppSettings? _applied;
    private int _ciadpiRestarts;
    private bool _wantConnected;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public EngineMode? ActiveMode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? ErrorAdvice { get; private set; }
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    public event Action? StateChanged;

    public ConnectionManager()
    {
        _tun.ExitedUnexpectedly += code => _ = OnEngineFailedAsync("sing-box неожиданно остановился.",
            "Обычно это значит, что сеть сменилась или адаптер TUN был удалён. Нажмите кнопку подключения ещё раз.");
    }

    public bool IsConnected => State == ConnectionState.Connected;

    public async Task ConnectAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State is ConnectionState.Connected or ConnectionState.Connecting) return;
            _wantConnected = true;
            SetState(ConnectionState.Connecting);
            await StartCoreAsync(settings, ct);
            SetState(ConnectionState.Connected);
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            _wantConnected = false;
            SetError(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _wantConnected = false;
            if (State == ConnectionState.Disconnected) return;
            SetState(ConnectionState.Disconnecting);
            await StopCoreAsync();
            SetState(ConnectionState.Disconnected);
            Log.Info(Source, "Отключено");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Применяет новые настройки к работающему подключению: перегенерирует конфиг и перезапускает
    /// только то, что нужно (без повторного запроса прав для TUN).
    /// </summary>
    public async Task ApplyAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (State != ConnectionState.Connected || _applied == null) return;
            var old = _applied;
            var strategyChanged = old.GetActiveStrategy()?.Arguments != settings.GetActiveStrategy()?.Arguments
                                  || old.SocksPort != settings.SocksPort || old.FakeSni != settings.FakeSni;
            if (old.Mode != settings.Mode)
            {
                SetState(ConnectionState.Connecting);
                await StopCoreAsync();
                await StartCoreAsync(settings, CancellationToken.None);
                SetState(ConnectionState.Connected);
                return;
            }

            if (strategyChanged)
            {
                await StopCiadpiAsync();
                await StartCiadpiAsync(settings, CancellationToken.None);
            }

            var plan = RoutePlan.Build(settings);
            Warnings = plan.Warnings;
            if (settings.Mode == EngineMode.Tun)
            {
                var config = SingBoxConfigBuilder.Build(settings, plan);
                var oldConfig = File.Exists(AppPaths.SingBoxConfig) ? await File.ReadAllTextAsync(AppPaths.SingBoxConfig) : "";
                if (config != oldConfig)
                {
                    await File.WriteAllTextAsync(AppPaths.SingBoxConfig, config);
                    Log.Info(Source, "Правила изменились — перезапускаю sing-box");
                    await _tun.RestartAsync(AppPaths.SingBoxConfig);
                }
            }
            else
            {
                ApplyProxy(settings, plan);
            }
            _applied = Snapshot(settings);
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            _wantConnected = false;
            SetError(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(AppSettings settings, CancellationToken ct)
    {
        ErrorMessage = null;
        ErrorAdvice = null;
        _ciadpiRestarts = 0;
        SystemProxy.Restore(); // на случай, если прошлый сеанс не успел убрать за собой

        if (settings.Mode == EngineMode.Tun) TunController.RequireExecutable();
        if (NetUtil.IsPortBusy(settings.SocksPort))
            throw new UserFacingException($"Порт {settings.SocksPort} уже занят другой программой.",
                "Закройте программу, которая его использует (возможно, ещё одна копия ByeDPI или другой прокси), или выберите другой порт в настройках.");

        await StartCiadpiAsync(settings, ct);

        var plan = RoutePlan.Build(settings);
        Warnings = plan.Warnings;
        foreach (var w in plan.Warnings) Log.Warn(Source, w);

        if (settings.Mode == EngineMode.Tun)
        {
            await File.WriteAllTextAsync(AppPaths.SingBoxConfig, SingBoxConfigBuilder.Build(settings, plan), ct);
            await _tun.StartAsync(AppPaths.SingBoxConfig, ct);
            Log.Info(Source, "Подключено (TUN)");
        }
        else
        {
            ApplyProxy(settings, plan);
            Log.Info(Source, $"Подключено (прокси 127.0.0.1:{settings.SocksPort})");
        }
        ActiveMode = settings.Mode;
        _applied = Snapshot(settings);
    }

    private void ApplyProxy(AppSettings settings, RoutePlan plan)
    {
        if (!settings.SetSystemProxy)
        {
            _pac?.Dispose();
            _pac = null;
            SystemProxy.Restore();
            return;
        }
        if (settings.UsePac)
        {
            var pac = PacBuilder.Build(plan, settings.SocksPort);
            if (_pac == null) _pac = new PacServer(pac);
            else _pac.Update(pac);
            SystemProxy.Apply(_pac.Url, settings.SocksPort);
        }
        else
        {
            _pac?.Dispose();
            _pac = null;
            SystemProxy.Apply(null, settings.SocksPort);
        }
    }

    private async Task StartCiadpiAsync(AppSettings settings, CancellationToken ct)
    {
        var strategy = settings.GetActiveStrategy()?.Arguments ?? "";
        var child = await CiadpiEngine.StartAsync(strategy, settings.SocksPort, settings.FakeSni, ct: ct);
        _ciadpi = child;
        child.Exited += code => _ = OnCiadpiExitedAsync(child, code);
    }

    private async Task StopCiadpiAsync()
    {
        var c = _ciadpi;
        _ciadpi = null;
        if (c != null)
        {
            await c.StopAsync();
            c.Dispose();
        }
    }

    private async Task OnCiadpiExitedAsync(ChildProcess child, int code)
    {
        if (!ReferenceEquals(child, _ciadpi) || !_wantConnected) return;
        Log.Warn(CiadpiEngine.Source, $"ciadpi завершился (код {code})");
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(child, _ciadpi) || !_wantConnected || _applied == null) return;
            _ciadpi = null;
            if (++_ciadpiRestarts > 5)
            {
                await StopCoreAsync();
                _wantConnected = false;
                SetError(new UserFacingException("ciadpi постоянно падает.",
                    "Попробуйте другую стратегию в «Редакторе» или подберите её на вкладке «Подбор»."));
                return;
            }
            await Task.Delay(500);
            Log.Info(CiadpiEngine.Source, "Перезапуск ciadpi");
            await StartCiadpiAsync(_applied, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            _wantConnected = false;
            SetError(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnEngineFailedAsync(string message, string advice)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_wantConnected) return;
            await StopCoreAsync();
            _wantConnected = false;
            SetError(new UserFacingException(message, advice));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        try { await _tun.StopAsync(); } catch (Exception ex) { Log.Warn(Source, "Остановка TUN: " + ex.Message); }
        await StopCiadpiAsync();
        _pac?.Dispose();
        _pac = null;
        SystemProxy.Restore();
        ActiveMode = null;
        _applied = null;
    }

    private void SetError(Exception ex)
    {
        if (ex is UserFacingException u)
        {
            ErrorMessage = u.Message;
            ErrorAdvice = u.Advice;
        }
        else
        {
            ErrorMessage = "Не удалось подключиться: " + ex.Message;
            ErrorAdvice = "Откройте «Логи», чтобы увидеть подробности, или запустите «Диагностику».";
        }
        Log.Error(Source, ErrorMessage + (ex is UserFacingException ? "" : "\n" + ex));
        SetState(ConnectionState.Error);
    }

    private void SetState(ConnectionState s)
    {
        State = s;
        StateChanged?.Invoke();
    }

    private static AppSettings Snapshot(AppSettings s)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(s, SettingsStore.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
    }

    public async ValueTask DisposeAsync()
    {
        _wantConnected = false;
        await StopCoreAsync();
    }

    /// <summary>Синхронная аварийная уборка при выходе процесса.</summary>
    public void EmergencyCleanup()
    {
        try { _tun.StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _ciadpi?.Dispose(); } catch { }
        try { SystemProxy.Restore(); } catch { }
    }
}
