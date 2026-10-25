using System.Net;
using Avalonia.Threading;
using ByeDpiPc.Core;
using ByeDpiPc.Core.VkRelay;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ByeDpiPc.App.Services;

/// <summary>
/// Сторона ПК для режима «точка выхода для телефона» через звонки VK. Держит связку, показывает
/// её состояние для UI и переподключается. Данные TURN присылает телефон при связке — ПК их не
/// запрашивает и браузер ему не нужен. Весь выход в интернет даёт этот ПК.
/// </summary>
public sealed partial class RelayPcHost : ObservableObject
{
    readonly AppSettings _settings;
    readonly Action _save;

    RelayEndpoint? _endpoint;
    RelayLan? _lan;
    RelayPcService? _service;
    DispatcherTimer? _poll;

    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _paired;
    [ObservableProperty] private string _status = "Выключено";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private long _rttMs = -1;
    [ObservableProperty] private int _peerRelays;

    public RelayPcHost(AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    public void Start()
    {
        if (Running) return;
        Running = true;

        var secret = string.IsNullOrEmpty(_settings.VkRelaySecret)
            ? null : TryDecodeSecret(_settings.VkRelaySecret);
        var pairing = secret is not null
            ? new RelayPairing { Secret = _settings.VkRelaySecret, Lan = LanAddresses() }
            : RelayPairing.Create(LanAddresses());
        _settings.VkRelaySecret = pairing.Secret;
        _save();

        var opts = new RelayOptions { RelayCount = Math.Clamp(_settings.VkRelayCount, 1, 8) };
        _endpoint = new RelayEndpoint(pairing.SecretBytes, RelayRole.Pc, opts);
        _endpoint.Log += m => Log.Info("VK-выход", m);
        _service = new RelayPcService(_endpoint);
        _service.Log += m => Log.Info("VK-выход", m);

        _lan = new RelayLan();
        pairing.Lan = LanAddresses(_lan.Port); // RelayPcService в конструкторе уже задал endpoint.OnOpen
        pairing.CallLink = _settings.VkRelayCallLink.Trim();
        _pairing = pairing;
        _lan.OnPacket += (from, data) => _ = _endpoint.FeedLanAsync(data, from, _lan);
        UpdateCode();

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
    }

    RelayPairing? _pairing;

    void UpdateCode()
    {
        if (_pairing is null) return;
        _pairing.CallLink = _settings.VkRelayCallLink.Trim();
        PairingCode = string.IsNullOrWhiteSpace(_pairing.CallLink) ? "" : _pairing.ToCode();
        Status = string.IsNullOrWhiteSpace(_pairing.CallLink)
            ? "Вставьте ссылку на звонок VK — тогда появится QR-код для телефона."
            : "Ожидаю связку: отсканируйте QR-код в приложении на телефоне.";
    }

    /// <summary>Пользователь изменил ссылку на звонок — пересобрать QR.</summary>
    public void SetCallLink(string link)
    {
        _settings.VkRelayCallLink = link;
        _save();
        UpdateCode();
    }

    void Refresh()
    {
        if (_endpoint is null) return;
        Paired = _endpoint.Connected;
        RttMs = _endpoint.LastRttMs;
        PeerRelays = _endpoint.PeerRelays.Count;
        if (_endpoint.Connected)
        {
            Status = $"Телефон на связи: {PeerRelays} ретрансляторов, задержка {(RttMs < 0 ? "—" : RttMs + " мс")}";
            if (++_statTick % 5 == 0) Log.Info("VK-выход", _endpoint.DebugStats());
        }
    }

    int _statTick;

    public void Stop()
    {
        _poll?.Stop();
        _poll = null;
        _endpoint?.Dispose();
        _endpoint = null;
        _lan?.Dispose();
        _lan = null;
        _service = null;
        Running = false;
        Paired = false;
        PairingCode = "";
        Status = "Выключено";
    }

    /// <summary>Создать новый секрет связки (например, если телефон потерян) и перезапустить.</summary>
    public void Reset()
    {
        _settings.VkRelaySecret = "";
        _save();
        if (Running) { Stop(); Start(); }
    }

    static List<string> LanAddresses(int port = 0) =>
        RelayLan.LocalAddresses().Select(a => $"{a}:{port}").ToList();

    static byte[]? TryDecodeSecret(string s)
    {
        try { return new RelayPairing { Secret = s }.SecretBytes; }
        catch { return null; }
    }
}
