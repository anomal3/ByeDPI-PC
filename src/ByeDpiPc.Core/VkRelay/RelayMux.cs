using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using KcpSharp;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>Тип сообщения мультиплексора (внутри сегмента KCP).</summary>
enum MuxType : byte
{
    Open = 1, OpenOk = 2, OpenFail = 3, Data = 4, Close = 5, Credit = 6,
}

/// <summary>
/// Один логический поток (TCP-соединение) поверх общего KCP-сеанса. Телефон открывает поток
/// на каждый локальный коннект; ПК на другой стороне подключается к host:port и связывает их.
/// </summary>
public sealed class RelayStream
{
    internal const int InitialCredit = 256 * 1024;

    readonly RelayMux _mux;
    readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    readonly SemaphoreSlim _creditSignal = new(0);
    readonly object _creditLock = new();
    int _sendCredit = InitialCredit;
    int _recvSinceCredit;

    public uint Id { get; }
    internal TaskCompletionSource<string?> OpenResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RelayStream(RelayMux mux, uint id) { _mux = mux; Id = id; }

    /// <summary>Отправить данные собеседнику (с учётом кредита — не переполняем его буфер).</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var chunk = Math.Min(16 * 1024, data.Length - offset);
            await AcquireCreditAsync(chunk, ct);
            await _mux.SendFrameAsync(MuxType.Data, Id, data.Slice(offset, chunk), ct);
            offset += chunk;
        }
    }

    async Task AcquireCreditAsync(int need, CancellationToken ct)
    {
        while (true)
        {
            lock (_creditLock)
            {
                if (_sendCredit >= need) { _sendCredit -= need; return; }
            }
            await _creditSignal.WaitAsync(ct); // ждём пополнения кредита собеседником
        }
    }

    /// <summary>Прочитать следующую порцию данных; null — поток закрыт.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _incoming.Reader.ReadAsync(ct);
            // Подтверждаем приём кредитом, когда накопится заметная доля окна.
            _recvSinceCredit += data.Length;
            if (_recvSinceCredit >= InitialCredit / 4)
            {
                var credit = _recvSinceCredit;
                _recvSinceCredit = 0;
                var body = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(body, (uint)credit);
                await _mux.SendFrameAsync(MuxType.Credit, Id, body, ct);
            }
            return data;
        }
        catch (ChannelClosedException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    internal void Deliver(byte[] data) => _incoming.Writer.TryWrite(data);

    internal void AddCredit(int n)
    {
        lock (_creditLock) _sendCredit += n;
        if (_creditSignal.CurrentCount == 0) _creditSignal.Release();
    }

    internal void CompleteIncoming()
    {
        _incoming.Writer.TryComplete();
        if (_creditSignal.CurrentCount == 0) _creditSignal.Release(); // разбудить ждущего отправителя
    }

    public async Task CloseAsync()
    {
        CompleteIncoming();
        await _mux.CloseStreamAsync(Id);
    }
}

/// <summary>
/// Мультиплексор потоков поверх надёжного KCP-сеанса. Симметричен: обе стороны могут открывать потоки,
/// но в нашем сценарии открывает телефон, а ПК по <see cref="OnOpen"/> подключается к цели.
/// </summary>
public sealed class RelayMux : IDisposable
{
    readonly KcpConversation _conv;
    readonly ConcurrentDictionary<uint, RelayStream> _streams = new();
    readonly CancellationTokenSource _cts = new();
    readonly bool _odd; // чтобы id потоков двух сторон не пересекались
    int _nextId;

    /// <summary>Пришёл запрос открыть поток к host:port (сторона ПК подключается и связывает).</summary>
    public Func<string, int, RelayStream, Task>? OnOpen { get; set; }

    public RelayMux(KcpConversation conv, bool initiatorSide)
    {
        _conv = conv;
        _odd = initiatorSide;
        _ = ReadLoopAsync();
    }

    public async Task<RelayStream> OpenAsync(string host, int port, CancellationToken ct = default)
    {
        var id = (uint)(Interlocked.Add(ref _nextId, 2) - 2) | (_odd ? 1u : 0u);
        var stream = new RelayStream(this, id);
        _streams[id] = stream;
        await SendFrameAsync(MuxType.Open, id, Encoding.UTF8.GetBytes($"{host}:{port}"), ct);
        var err = await stream.OpenResult.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (err is not null)
        {
            _streams.TryRemove(id, out _);
            throw new IOException($"не удалось открыть {host}:{port} через ПК: {err}");
        }
        return stream;
    }

    internal async Task SendFrameAsync(MuxType type, uint streamId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var frame = new byte[5 + data.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), streamId);
        data.CopyTo(frame.AsMemory(5));
        // Ждём место в очереди отправки KCP, затем кладём сообщение.
        await _conv.WaitForSendQueueAvailableSpaceAsync(frame.Length, 1, ct);
        await _conv.SendAsync(frame, ct);
    }

    internal async Task CloseStreamAsync(uint id)
    {
        if (_streams.TryRemove(id, out _))
            try { await SendFrameAsync(MuxType.Close, id, ReadOnlyMemory<byte>.Empty); } catch { }
    }

    async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        while (!_cts.IsCancellationRequested)
        {
            KcpConversationReceiveResult result;
            try { result = await _conv.ReceiveAsync(buffer, _cts.Token); }
            catch (OperationCanceledException) { return; }
            if (result.TransportClosed) return;
            if (result.BytesReceived < 5) continue;
            Dispatch(buffer.AsSpan(0, result.BytesReceived));
        }
    }

    void Dispatch(ReadOnlySpan<byte> frame)
    {
        var type = (MuxType)frame[0];
        var id = BinaryPrimitives.ReadUInt32BigEndian(frame[1..]);
        var payload = frame[5..];
        switch (type)
        {
            case MuxType.Open:
                _ = HandleOpenAsync(id, Encoding.UTF8.GetString(payload));
                break;
            case MuxType.OpenOk:
                if (_streams.TryGetValue(id, out var okStream)) okStream.OpenResult.TrySetResult(null);
                break;
            case MuxType.OpenFail:
                if (_streams.TryGetValue(id, out var failStream))
                    failStream.OpenResult.TrySetResult(Encoding.UTF8.GetString(payload));
                break;
            case MuxType.Data:
                if (_streams.TryGetValue(id, out var dataStream)) dataStream.Deliver(payload.ToArray());
                break;
            case MuxType.Close:
                if (_streams.TryRemove(id, out var closeStream)) closeStream.CompleteIncoming();
                break;
            case MuxType.Credit:
                if (payload.Length >= 4 && _streams.TryGetValue(id, out var creditStream))
                    creditStream.AddCredit((int)BinaryPrimitives.ReadUInt32BigEndian(payload));
                break;
        }
    }

    async Task HandleOpenAsync(uint id, string hostPort)
    {
        var stream = new RelayStream(this, id);
        _streams[id] = stream;
        var sep = hostPort.LastIndexOf(':');
        if (OnOpen is null || sep < 0 || !int.TryParse(hostPort[(sep + 1)..], out var port))
        {
            await SendFrameAsync(MuxType.OpenFail, id, Encoding.UTF8.GetBytes("нет обработчика"));
            _streams.TryRemove(id, out _);
            return;
        }
        try
        {
            await OnOpen(hostPort[..sep], port, stream);
            await SendFrameAsync(MuxType.OpenOk, id, ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception e)
        {
            await SendFrameAsync(MuxType.OpenFail, id, Encoding.UTF8.GetBytes(e.Message));
            _streams.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var s in _streams.Values) s.CompleteIncoming();
        _cts.Dispose();
    }
}
