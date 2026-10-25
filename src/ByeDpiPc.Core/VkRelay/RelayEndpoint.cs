using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using KcpSharp;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>Сколько ретрансляторов занимать с каждой стороны (обход предела ~2 Мбит/с на один).</summary>
public sealed class RelayOptions
{
    public int RelayCount { get; set; } = 3;
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan PermissionRefresh { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Одна сторона туннеля через звонки VK: занимает ретрансляторы, обменивается приветствиями
/// с собеседником, поднимает KCP и мультиплексор, держит связь живой и переподключается.
/// Общий код для ПК и (портируется) телефона.
/// </summary>
public sealed class RelayEndpoint : IDisposable
{
    readonly byte[] _secret;
    readonly RelayRole _role;
    readonly RelayOptions _opts;
    readonly RelayCipher _cipher;
    readonly RelayTransport _transport;
    readonly CancellationTokenSource _cts = new();
    readonly string _node = Guid.NewGuid().ToString("N")[..8];

    TurnCredentials? _creds;
    KcpConversation? _conv;
    RelayMux? _mux;
    uint _session;
    volatile bool _peerSeen;
    long _lastRttMs = -1;

    public RelayRole Role => _role;
    public RelayMux Mux => _mux ?? throw new InvalidOperationException("связка ещё не установлена");
    public long LastRttMs => Interlocked.Read(ref _lastRttMs);
    public bool Connected => _peerSeen && _transport.Pairs.Count > 0;
    /// <summary>Последние известные адреса ретрансляторов собеседника (для переподключения).</summary>
    public IReadOnlyList<string> PeerRelays { get; private set; } = [];

    public event Action<string>? Log;
    /// <summary>Сторона ПК: пришёл запрос открыть соединение к host:port.</summary>
    public Func<string, int, RelayStream, Task>? OnOpen { get; set; }

    public RelayEndpoint(byte[] secret, RelayRole role, RelayOptions? opts = null)
    {
        _secret = secret;
        _role = role;
        _opts = opts ?? new RelayOptions();
        _cipher = new RelayCipher(secret, role);
        _transport = new RelayTransport(_cipher);
        _transport.OnControl += OnControl;
    }

    /// <summary>Задать данные TURN (телефон получает сам; ПК — из приветствия телефона).</summary>
    public void SetCredentials(TurnCredentials creds) => _creds = creds;

    public TurnCredentials? Credentials => _creds;

    bool _allocateStarted;

    /// <summary>Занять ретрансляторы по заданным данным (без добавления в рассылку) — для продления.</summary>
    async Task<List<RelayPair>> AllocatePairsAsync(TurnCredentials creds, CancellationToken ct)
    {
        var servers = creds.Servers;
        var result = new List<RelayPair>();
        for (var i = 0; result.Count < _opts.RelayCount && i < _opts.RelayCount * 3; i++)
        {
            var server = servers[i % servers.Count];
            try
            {
                var client = new TurnClient(server, creds.Username, creds.Password);
                var relay = await client.AllocateAsync(ct);
                foreach (var ip in servers.Select(s => s.Address).Distinct())
                    await client.CreatePermissionAsync(ip, ct);
                result.Add(new RelayPair(client, relay));
            }
            catch (Exception e) { Log?.Invoke($"ретранслятор {server} не занят: {e.Message}"); }
        }
        return result;
    }

    /// <summary>Занять свои ретрансляторы по текущим данным TURN. Выполняется строго один раз.</summary>
    public async Task AllocateAsync(CancellationToken ct = default)
    {
        if (_allocateStarted) return;
        _allocateStarted = true;
        if (_creds is null) throw new InvalidOperationException("нет данных TURN");
        var pairs = await AllocatePairsAsync(_creds, ct);
        if (pairs.Count == 0) throw new TurnException(0, "не удалось занять ни одного ретранслятора");
        foreach (var p in pairs) _transport.AddPair(p);
        Log?.Invoke($"занято ретрансляторов: {pairs.Count}");
    }

    /// <summary>Продление на ПК: телефон прислал свежие данные — занять новые ретрансляторы,
    /// подхватить новые адреса телефона и подменить транспорт, не разрывая KCP.</summary>
    async Task RenewPcAsync(RelayHello hello)
    {
        try
        {
            var newCreds = new TurnCredentials
            {
                Username = hello.Creds!.Username,
                Password = hello.Creds.Password,
                Servers = hello.Creds.Turn.Select(IPEndPoint.Parse).ToList(),
                ExpiresAt = ParseExpiry(hello.Creds.Username),
            };
            var pairs = await AllocatePairsAsync(newCreds, _cts.Token);
            if (pairs.Count == 0) { Log?.Invoke("продление: не удалось занять новые ретрансляторы"); return; }
            foreach (var p in pairs) _transport.HookIncoming(p);
            var peers = hello.Relays.Where(r => IPEndPoint.TryParse(r, out _)).Select(IPEndPoint.Parse).ToList();
            for (var i = 0; i < pairs.Count && peers.Count > 0; i++) pairs[i].PeerRelay = peers[i % peers.Count];
            _creds = newCreds;
            PeerRelays = hello.Relays;
            _transport.ReplacePairs(pairs);   // старые освобождаются, KCP продолжается
            SendHello();                      // HelloAck с новыми адресами ПК по новому транспорту
            Log?.Invoke("продление: переключился на новые ретрансляторы");
        }
        catch (Exception e) { Log?.Invoke($"продление не удалось: {e.Message}"); }
    }

    /// <summary>Наши ретрансляторы (адреса для приветствия собеседнику).</summary>
    public List<string> MyRelays() => _transport.Pairs.Select(p => p.Client.Relay!.ToString()).ToList();

    /// <summary>Отладка: суммарно отправлено/принято пакетов по всем парам и их привязка.</summary>
    public string DebugStats()
    {
        var pairs = _transport.Pairs;
        var sent = pairs.Sum(p => Interlocked.Read(ref p.Sent));
        var recv = pairs.Sum(p => Interlocked.Read(ref p.Received));
        return $"отпр={sent} принято_сыро={Interlocked.Read(ref _transport.RawReceived)} расшифр={recv} сеанс={_session}";
    }

    RelayHello BuildHello() => new()
    {
        Role = _role == RelayRole.Pc ? "pc" : "phone",
        Node = _node,
        Relays = MyRelays(),
        Session = _session,
        Creds = _role == RelayRole.Phone && _creds is not null
            ? new RelayCreds { Username = _creds.Username, Password = _creds.Password, Turn = _creds.Servers.Select(s => s.ToString()).ToList() }
            : null,
    };

    /// <summary>Отправить приветствие: по известным адресам собеседника (или по заданным при связке).</summary>
    public void SendHello(IEnumerable<string>? toRelays = null, IPEndPoint? lanTarget = null)
    {
        if (_session == 0 && _role == RelayRole.Phone) _session = BinaryPrimitives.ReadUInt32LittleEndian(Guid.NewGuid().ToByteArray());
        var hello = BuildHello();
        var body = JsonSerializer.SerializeToUtf8Bytes(hello);
        var kind = _role == RelayRole.Pc ? RelayKind.HelloAck : RelayKind.Hello;
        var sealed_ = _cipher.Seal(kind, body);

        var targets = toRelays?.ToList() ?? PeerRelays.ToList();
        var pairs = _transport.Pairs;
        foreach (var t in targets)
            if (IPEndPoint.TryParse(t, out var ep) && pairs.Count > 0)
                pairs[0].Client.Send(ep, sealed_);
        // Связка дома: приветствие напрямую на ПК в локальной сети (минуя TURN).
        if (lanTarget is not null && pairs.Count > 0)
            pairs[0].Client.Send(lanTarget, sealed_);
    }

    void OnControl(RelayKind kind, byte[] body)
    {
        switch (kind)
        {
            case RelayKind.Ping:
                _transport.SendControl(RelayKind.Pong, body);
                break;
            case RelayKind.Pong when body.Length >= 8:
                var sent = BinaryPrimitives.ReadInt64BigEndian(body);
                Interlocked.Exchange(ref _lastRttMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sent);
                break;
            case RelayKind.Hello or RelayKind.HelloAck:
                HandleHello(kind, body);
                break;
        }
    }

    void HandleHello(RelayKind kind, byte[] body)
    {
        RelayHello? hello;
        try { hello = JsonSerializer.Deserialize<RelayHello>(body); }
        catch { return; }
        if (hello is null || hello.Version != 1) return;
        Log?.Invoke($"приветствие от {hello.Node}: сеанс={hello.Session}, ретрансляторов={hello.Relays.Count}, creds={(hello.Creds is null ? "—" : hello.Creds.Username)}");

        // Продление: тот же сеанс, но телефон прислал новые данные TURN — переключаемся без разрыва.
        if (_role == RelayRole.Pc && _conv is not null && hello.Session == _session &&
            hello.Creds is not null && _creds is not null && hello.Creds.Username != _creds.Username)
        {
            _ = RenewPcAsync(hello);
            return;
        }

        var first = !_peerSeen;
        OnHello(hello);
        if (_role == RelayRole.Pc && kind == RelayKind.Hello)
            SendHello(); // отвечаем HelloAck по уже сохранённым адресам собеседника
        StartTunnel();
        if (first) Log?.Invoke($"собеседник {hello.Node} на связи, ретрансляторов у него: {hello.Relays.Count}");
    }

    /// <summary>Обработать входящее приветствие (вызывает тот, кто читает Data indication напрямую при связке).</summary>
    public void OnHello(RelayHello hello)
    {
        PeerRelays = hello.Relays;
        if (_role == RelayRole.Pc)
        {
            // Новое подключение телефона (другой сеанс) — сбрасываем старый KCP/мультиплексор.
            if (_conv is not null && hello.Session != _session)
            {
                _mux?.Dispose();
                _conv.Dispose();
                _mux = null;
                _conv = null;
            }
            _session = hello.Session;
            if (hello.Creds is not null && _creds is null)
                _creds = new TurnCredentials
                {
                    Username = hello.Creds.Username,
                    Password = hello.Creds.Password,
                    Servers = hello.Creds.Turn.Select(IPEndPoint.Parse).ToList(),
                    ExpiresAt = ParseExpiry(hello.Creds.Username),
                };
        }
        BindPeerRelays(hello.Relays);
        _peerSeen = true;
    }

    /// <summary>Зашифровать наше приветствие (для прямой отправки по локальной сети при связке).</summary>
    public byte[] SealHello()
    {
        if (_session == 0 && _role == RelayRole.Phone)
            _session = BinaryPrimitives.ReadUInt32LittleEndian(Guid.NewGuid().ToByteArray());
        var kind = _role == RelayRole.Pc ? RelayKind.HelloAck : RelayKind.Hello;
        return _cipher.Seal(kind, JsonSerializer.SerializeToUtf8Bytes(BuildHello()));
    }

    /// <summary>
    /// Связка по локальной сети: расшифровать прямой пакет. Если это Hello (на ПК) — занять свои
    /// ретрансляторы и ответить HelloAck на <paramref name="from"/>. Если HelloAck (на телефоне) — поднять туннель.
    /// </summary>
    public async Task FeedLanAsync(byte[] packet, IPEndPoint from, RelayLan lan, CancellationToken ct = default)
    {
        if (!_cipher.TryOpen(packet, out var kind, out var body)) return;
        if (kind is not (RelayKind.Hello or RelayKind.HelloAck)) return;
        RelayHello? hello;
        try { hello = JsonSerializer.Deserialize<RelayHello>(body); } catch { return; }
        if (hello is null || hello.Version != 1) return;

        OnHello(hello);
        if (_role == RelayRole.Pc && kind == RelayKind.Hello)
        {
            if (_transport.Pairs.Count == 0) await AllocateAsync(ct);
            RebindPeers();
            lan.Send(from, SealHello()); // HelloAck по локальной сети
        }
        StartTunnel();
    }

    /// <summary>Пересопоставить наши ретрансляторы с адресами собеседника (после занятия ретрансляторов).</summary>
    public void RebindPeers() => BindPeerRelays(PeerRelays.ToList());

    static DateTimeOffset ParseExpiry(string username)
        => long.TryParse(username.Split(':')[0], out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow.AddHours(8);

    /// <summary>Сопоставить наши ретрансляторы с адресами собеседника (парами для раскладки нагрузки).</summary>
    void BindPeerRelays(List<string> peerRelays)
    {
        var peers = peerRelays.Where(r => IPEndPoint.TryParse(r, out _)).Select(IPEndPoint.Parse).ToList();
        if (peers.Count == 0) return;
        var pairs = _transport.Pairs;
        for (var i = 0; i < pairs.Count; i++)
            pairs[i].PeerRelay = peers[i % peers.Count];
    }

    /// <summary>Поднять KCP и мультиплексор после того, как узнали сеанс и собеседника.</summary>
    public void StartTunnel()
    {
        if (_conv is not null) return;
        var options = new KcpConversationOptions
        {
            Mtu = 1100,
            NoDelay = true,
            UpdateInterval = 10,
            FastResend = 2,
            // Трафик рассыпан по нескольким ретрансляторам → порядок пакетов «рваный»; обычный
            // контроль перегрузки принял бы это за потери и резал скорость. Отключаем.
            DisableCongestionControl = true,
            SendWindow = 1024,
            ReceiveWindow = 1024,
            RemoteReceiveWindow = 1024,
            SendQueueSize = 2048,
            ReceiveQueueSize = 2048,
        };
        _conv = new KcpConversation(_transport, (int)_session, options);
        _transport.Conversation = _conv;
        _mux = new RelayMux(_conv, initiatorSide: _role == RelayRole.Phone) { OnOpen = OnOpen };
        _ = KeepAliveAsync(_cts.Token);
        Log?.Invoke("туннель поднят");
    }

    async Task KeepAliveAsync(CancellationToken ct)
    {
        var lastPermission = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_opts.PingInterval, ct); } catch { return; }

            var ping = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(ping, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _transport.SendControl(RelayKind.Ping, ping);

            if (DateTimeOffset.UtcNow - lastPermission > _opts.PermissionRefresh)
            {
                lastPermission = DateTimeOffset.UtcNow;
                await RefreshAllAsync(ct);
            }
            // Пока не увидели собеседника — повторяем приветствие по последним адресам.
            if (!_peerSeen && PeerRelays.Count > 0) SendHello();
        }
    }

    async Task RefreshAllAsync(CancellationToken ct)
    {
        if (_creds is null) return;
        foreach (var pair in _transport.Pairs)
        {
            try
            {
                await pair.Client.RefreshAsync(600, ct);
                foreach (var ip in _creds.Servers.Select(s => s.Address).Distinct())
                    await pair.Client.CreatePermissionAsync(ip, ct);
            }
            catch (Exception e) { Log?.Invoke($"продление не удалось: {e.Message}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mux?.Dispose();
        _conv?.Dispose();
        _transport.Dispose();
        _cts.Dispose();
    }
}
