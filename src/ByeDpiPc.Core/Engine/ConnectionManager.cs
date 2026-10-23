using ByeDpiPc.Core.Routing;

namespace ByeDpiPc.Core.Engine;

public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting, Error }

/// <summary>
/// Оркестратор подключения.
/// byedpi: ciadpi (SOCKS) → TUN через sing-box или PAC + системный прокси.
/// zapret: winws правит пакеты сам; sing-box поднимается только для правил «Внешний прокси».
/// Следит за процессами, перезапускает упавший ciadpi и всегда убирает за собой TUN, WinDivert и системный прокси.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private const string Source = "Подключение";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TunController _tun = new();
    private readonly ZapretController _zapret = new();
    private ChildProcess? _ciadpi;
    private PacServer? _pac;
    private ChildProcess? _bridge;
    private int _bridgePort;
    private string? _bridgeConfig;
    private AppSettings? _applied;
    private int _ciadpiRestarts;
    private bool _wantConnected;
    private bool _suspended;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public EngineKind? ActiveEngine { get; private set; }
    public EngineMode? ActiveMode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? ErrorAdvice { get; private set; }
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    public event Action? StateChanged;

    public ConnectionManager()
    {
        _tun.ExitedUnexpectedly += code => _ = OnEngineFailedAsync("sing-box неожиданно остановился.",
            "Обычно это значит, что сеть сменилась или адаптер TUN был удалён. Нажмите кнопку подключения ещё раз.");
        _zapret.ExitedUnexpectedly += code => _ = OnEngineFailedAsync("zapret (winws) неожиданно остановился.",
            "Возможно, драйвер WinDivert выгрузил антивирус или другая программа обхода. Нажмите кнопку подключения ещё раз.");
    }

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>Текстовое описание режима для интерфейса.</summary>
    public string ModeTitle => ActiveEngine == EngineKind.Zapret ? "zapret" : ActiveMode == EngineMode.Tun ? "TUN" : "Прокси";

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
    /// Применяет новые настройки к работающему подключению: перегенерирует конфиги и перезапускает
    /// только то, что нужно (без повторного запроса прав — помощник уже запущен).
    /// </summary>
    public async Task ApplyAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (State != ConnectionState.Connected || _applied == null || _suspended) return;
            var old = _applied;
            if (old.Engine != settings.Engine || (settings.Engine == EngineKind.ByeDpi && old.Mode != settings.Mode))
            {
                SetState(ConnectionState.Connecting);
                await StopCoreAsync();
                await StartCoreAsync(settings, CancellationToken.None);
                SetState(ConnectionState.Connected);
                return;
            }

            var plan = BuildPlan(settings);
            if (settings.Engine == EngineKind.Zapret)
            {
                await _zapret.ApplyAsync(ZapretArgs(settings, plan));
                await ApplyUpstreamTunAsync(settings, plan);
            }
            else
            {
                var strategyChanged = old.GetActiveStrategy()?.Arguments != settings.GetActiveStrategy()?.Arguments
                                      || old.SocksPort != settings.SocksPort || old.FakeSni != settings.FakeSni;
                if (strategyChanged)
                {
                    await StopCiadpiAsync();
                    await StartCiadpiAsync(settings, CancellationToken.None);
                }
                if (settings.Mode == EngineMode.Tun) await WriteAndRestartTunAsync(settings, plan);
                else ApplyProxy(settings, plan);
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

    /// <summary>
    /// Пауза для подбора стратегий zapret: два winws одновременно мешают друг другу,
    /// а проверка «напрямую» должна идти без обхода.
    /// </summary>
    public async Task SuspendAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != ConnectionState.Connected || ActiveEngine != EngineKind.Zapret || _suspended) return;
            _suspended = true;
            await _zapret.StopAsync();
            Log.Info(Source, "Обход приостановлен на время подбора");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_suspended) return;
            _suspended = false;
            if (State != ConnectionState.Connected) return;
            var plan = BuildPlan(settings);
            await _zapret.StartAsync(ZapretArgs(settings, plan));
            _applied = Snapshot(settings);
            Log.Info(Source, "Обход возобновлён");
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

    private RoutePlan BuildPlan(AppSettings settings)
    {
        var plan = RoutePlan.Build(settings);
        var useTun = settings.Engine == EngineKind.ByeDpi ? settings.Mode == EngineMode.Tun : plan.HasUpstreamTargets;
        if (VpnDetector.Warning() is { } vpn)
            plan.Warnings.Insert(0, useTun || settings.Engine == EngineKind.Zapret ? vpn : vpn.Replace("режим «Только прокси»", "другой режим"));
        if (settings.Engine == EngineKind.Zapret && settings.Rules.Any(r => r.Enabled && r.Processes.Count > 0 && r.Domains.Count == 0 && r.IpCidrs.Count == 0))
            plan.Warnings.Add("Движок zapret не различает программы: правила только с программами (без доменов) не работают. Добавьте им домены или выберите движок byedpi в режиме TUN.");
        Warnings = plan.Warnings;
        return plan;
    }

    private async Task StartCoreAsync(AppSettings settings, CancellationToken ct)
    {
        ErrorMessage = null;
        ErrorAdvice = null;
        _ciadpiRestarts = 0;
        _suspended = false;
        SystemProxy.Restore(); // на случай, если прошлый сеанс не успел убрать за собой

        var plan = BuildPlan(settings);
        foreach (var w in plan.Warnings) Log.Warn(Source, w);

        if (settings.Engine == EngineKind.Zapret)
        {
            ZapretController.RequireExecutable();
            await _zapret.StartAsync(ZapretArgs(settings, plan), ct);
            if (plan.HasUpstreamTargets)
            {
                await File.WriteAllTextAsync(AppPaths.SingBoxConfig, SingBoxConfigBuilder.Build(settings, plan.UpstreamOnly()), ct);
                await _tun.StartAsync(AppPaths.SingBoxConfig, ct);
            }
            ActiveEngine = EngineKind.Zapret;
            ActiveMode = null;
            Log.Info(Source, "Подключено (zapret)");
        }
        else
        {
            if (settings.Mode == EngineMode.Tun) TunController.RequireExecutable();
            if (NetUtil.IsPortBusy(settings.SocksPort))
                throw new UserFacingException($"Порт {settings.SocksPort} уже занят другой программой.",
                    "Закройте программу, которая его использует (возможно, ещё одна копия ByeDPI или другой прокси), или выберите другой порт в настройках.");

            await StartCiadpiAsync(settings, ct);
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
            ActiveEngine = EngineKind.ByeDpi;
            ActiveMode = settings.Mode;
        }
        _applied = Snapshot(settings);
    }

    private static List<string> ZapretArgs(AppSettings settings, RoutePlan plan)
    {
        var lists = ZapretCommand.WriteLists(plan, settings.Filter, Path.Combine(AppPaths.RuntimeDir, "zapret"));
        var active = settings.GetActiveStrategy(EngineKind.Zapret);
        return ZapretCommand.Build(plan, settings.Filter, active?.Arguments ?? "", lists, active?.GoogleArguments);
    }

    private async Task ApplyUpstreamTunAsync(AppSettings settings, RoutePlan plan)
    {
        if (!plan.HasUpstreamTargets)
        {
            if (_tun.IsRunning) await _tun.StopAsync();
            return;
        }
        var config = SingBoxConfigBuilder.Build(settings, plan.UpstreamOnly());
        var old = File.Exists(AppPaths.SingBoxConfig) ? await File.ReadAllTextAsync(AppPaths.SingBoxConfig) : "";
        if (_tun.IsRunning && config == old) return;
        await File.WriteAllTextAsync(AppPaths.SingBoxConfig, config);
        if (_tun.IsRunning) await _tun.RestartAsync(AppPaths.SingBoxConfig);
        else await _tun.StartAsync(AppPaths.SingBoxConfig);
    }

    private async Task WriteAndRestartTunAsync(AppSettings settings, RoutePlan plan)
    {
        var config = SingBoxConfigBuilder.Build(settings, plan);
        var old = File.Exists(AppPaths.SingBoxConfig) ? await File.ReadAllTextAsync(AppPaths.SingBoxConfig) : "";
        if (config == old) return;
        await File.WriteAllTextAsync(AppPaths.SingBoxConfig, config);
        Log.Info(Source, "Правила изменились — перезапускаю sing-box");
        await _tun.RestartAsync(AppPaths.SingBoxConfig);
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
            int? bridge = null;
            if (plan.HasUpstreamTargets && plan.UpstreamProxy!.NeedsLocalBridge) bridge = EnsureBridge(plan.UpstreamProxy);
            else StopBridge();
            var pac = PacBuilder.Build(plan, settings.SocksPort, bridge);
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

    /// <summary>Локальный мост sing-box (без прав администратора) к VLESS для PAC в режиме «Только прокси».</summary>
    private int EnsureBridge(UpstreamProxy upstream)
    {
        if (_bridgePort == 0) _bridgePort = NetUtil.GetFreePort();
        var config = SingBoxConfigBuilder.BuildLocalBridge(upstream, _bridgePort);
        if (_bridge is { HasExited: false } && config == _bridgeConfig) return _bridgePort;
        StopBridge();
        var exe = TunController.RequireExecutable();
        var path = Path.Combine(AppPaths.RuntimeDir, "bridge.json");
        File.WriteAllText(path, config);
        _bridge = ChildProcess.Start(exe, new[] { "run", "-c", path, "--disable-color" }, "sing-box (мост)");
        _bridgeConfig = config;
        Log.Info(Source, $"Мост к внешнему прокси: 127.0.0.1:{_bridgePort}");
        return _bridgePort;
    }

    private void StopBridge()
    {
        _bridge?.Dispose();
        _bridge = null;
        _bridgeConfig = null;
    }

    private async Task StartCiadpiAsync(AppSettings settings, CancellationToken ct)
    {
        var strategy = settings.GetActiveStrategy(EngineKind.ByeDpi)?.Arguments ?? "";
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
            if (!_wantConnected || _suspended) return;
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
        try { await _zapret.StopAsync(); } catch (Exception ex) { Log.Warn(Source, "Остановка zapret: " + ex.Message); }
        try { await _tun.StopAsync(); } catch (Exception ex) { Log.Warn(Source, "Остановка TUN: " + ex.Message); }
        await StopCiadpiAsync();
        StopBridge();
        _pac?.Dispose();
        _pac = null;
        SystemProxy.Restore();
        ActiveEngine = null;
        ActiveMode = null;
        _applied = null;
        _suspended = false;
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
        await EngineHost.Instance.DisposeAsync();
    }

    /// <summary>Синхронная аварийная уборка при выходе процесса.</summary>
    public void EmergencyCleanup()
    {
        try { EngineHost.Instance.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _ciadpi?.Dispose(); } catch { }
        try { SystemProxy.Restore(); } catch { }
    }
}
