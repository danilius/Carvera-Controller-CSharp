using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Carvera.Core.Pendant;

/// <summary>
/// Persistent, reconnecting TCP client for the CYD pendant using bounded newline-delimited JSON frames.
/// Ported from tcp_client.py: the controller probes with <c>heartbeat</c> every 250 ms and the pendant must
/// answer <c>heartbeat_ack</c> within 1 s or the link is dropped and re-established. Events fire on the
/// client's worker thread.
/// </summary>
public sealed class CydClient : IDisposable
{
    private readonly Func<string> _host;
    private readonly Func<int> _port;
    private readonly TimeSpan _heartbeatInterval, _heartbeatTimeout, _reconnectInterval;
    private readonly int _maxQueued;
    private readonly NdjsonDecoder _decoder;
    private readonly ConcurrentQueue<byte[]> _tx = new();
    private readonly object _socketGate = new();
    private readonly Dictionary<long, long> _probes = [];
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _running;
    private CancellationTokenSource _stop = new();
    private long _lastAck = -1, _lastProbe = -1, _connectedAt;
    private long _probeSequence;
    private int _generation;

    public CydClient(Func<string> host, Func<int> port, TimeSpan? heartbeatInterval = null, TimeSpan? heartbeatTimeout = null,
        TimeSpan? reconnectInterval = null, int maxLineBytes = 64 * 1024, int maxQueuedMessages = 256)
    {
        _host = host;
        _port = port;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromMilliseconds(250);
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(1);
        _reconnectInterval = reconnectInterval ?? TimeSpan.FromSeconds(1);
        if (_heartbeatInterval <= TimeSpan.Zero || _heartbeatInterval >= _heartbeatTimeout)
            throw new ArgumentException("Heartbeat interval must be positive and shorter than its timeout.");
        _maxQueued = maxQueuedMessages;
        _decoder = new NdjsonDecoder(maxLineBytes);
    }

    public event Action<CydClient>? Connected;
    public event Action<CydClient>? Disconnected;
    public event Action<CydClient, JsonElement>? MessageReceived;

    public bool IsRunning => _running;

    /// <summary>True while the pendant has acknowledged a heartbeat within the timeout. Motion needs this.</summary>
    public bool MotionReady
    {
        get
        {
            var ack = Interlocked.Read(ref _lastAck);
            return _running && _socket is not null && ack >= 0 && Elapsed(ack) < _heartbeatTimeout;
        }
    }

    private static long Now => Stopwatch.GetTimestamp();
    private static TimeSpan Elapsed(long since) => Stopwatch.GetElapsedTime(since);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _stop = new CancellationTokenSource();
        _thread = new Thread(Run) { IsBackground = true, Name = "cyd-tcp-client" };
        _thread.Start();
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        _running = false;
        _stop.Cancel();
        CloseSocket();
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Queues a message. When the queue is full the oldest message is dropped.</summary>
    public bool Send(object payload)
    {
        byte[] line;
        try { line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n"); }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { return false; }
        _tx.Enqueue(line);
        while (_tx.Count > _maxQueued) _tx.TryDequeue(out _);
        return true;
    }

    private Socket? TryConnect()
    {
        var host = (_host() ?? "").Trim();
        var port = _port();
        if (host.Length == 0 || port is < 1 or > 65535) return null;
        Socket? candidate = null;
        try
        {
            candidate = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                candidate.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            candidate.NoDelay = true;
            candidate.SendTimeout = 50;
            return candidate;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException)
        {
            candidate?.Dispose();
            return null;
        }
    }

    private void CloseSocket()
    {
        Socket? socket;
        lock (_socketGate)
        {
            socket = _socket;
            _socket = null;
        }
        if (socket is null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        socket.Dispose();
    }

    private static void Raise(Action action)
    {
        try { action(); } catch (Exception) { /* a faulty handler must not kill the link */ }
    }

    private void PrepareConnection()
    {
        _connectedAt = Now;
        Interlocked.Exchange(ref _lastAck, -1);
        _lastProbe = -1;
        _probes.Clear();
        _decoder.Reset();
        while (_tx.TryDequeue(out _)) { } // replies from a previous socket are no longer valid
    }

    private void SendHeartbeatIfNeeded(Socket socket)
    {
        var now = Now;
        var ack = Interlocked.Read(ref _lastAck);
        var reference = ack >= 0 ? ack : _connectedAt;
        if (Elapsed(reference) >= _heartbeatTimeout) throw new IOException("CYD heartbeat acknowledgement timed out");
        if (_lastProbe >= 0 && Elapsed(_lastProbe) < _heartbeatInterval) return;
        _probeSequence++;
        foreach (var stale in _probes.Where(p => Elapsed(p.Value) >= _heartbeatTimeout).Select(p => p.Key).ToList()) _probes.Remove(stale);
        _probes[_probeSequence] = now;
        socket.Send(Encoding.UTF8.GetBytes($"{{\"type\":\"heartbeat\",\"seq\":{_probeSequence}}}\n"));
        _lastProbe = now;
    }

    private void DrainTx(Socket socket)
    {
        // UI updates must not keep the worker from reading heartbeat ACKs: at most four sends, 20 ms.
        var started = Now;
        for (var i = 0; i < 4 && Elapsed(started) < TimeSpan.FromMilliseconds(20); i++)
        {
            if (!_tx.TryDequeue(out var line)) return;
            socket.Send(line);
        }
    }

    private bool AcceptHeartbeat(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "heartbeat_ack") return false;
        if (message.TryGetProperty("seq", out var seqElement) && seqElement.ValueKind == JsonValueKind.Number && seqElement.TryGetInt64(out var seq)
            && _probes.Remove(seq, out var sentAt) && Elapsed(sentAt) < _heartbeatTimeout)
            Interlocked.Exchange(ref _lastAck, Now);
        return true;
    }

    private void PumpRx(Socket socket, byte[] buffer, int generation)
    {
        if (!socket.Poll(50_000, SelectMode.SelectRead)) return;
        var count = socket.Receive(buffer);
        if (count == 0) throw new IOException("CYD socket closed");
        foreach (var message in _decoder.Feed(buffer.AsSpan(0, count)))
        {
            if (AcceptHeartbeat(message)) continue;
            if (generation == Volatile.Read(ref _generation) && _running) Raise(() => MessageReceived?.Invoke(this, message));
        }
    }

    private void Run()
    {
        var buffer = new byte[4096];
        while (_running)
        {
            var connected = false;
            var generation = Volatile.Read(ref _generation);
            try
            {
                var socket = TryConnect();
                if (socket is null)
                {
                    _stop.Token.WaitHandle.WaitOne(_reconnectInterval);
                    continue;
                }
                lock (_socketGate)
                {
                    if (!_running)
                    {
                        socket.Dispose();
                        break;
                    }
                    _socket = socket;
                }
                connected = true;
                PrepareConnection();
                Raise(() => Connected?.Invoke(this));
                while (_running && _socket is not null)
                {
                    SendHeartbeatIfNeeded(socket);
                    DrainTx(socket);
                    PumpRx(socket, buffer, generation);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            finally
            {
                Interlocked.Increment(ref _generation);
                CloseSocket();
                if (connected) Raise(() => Disconnected?.Invoke(this));
            }
            if (_running) _stop.Token.WaitHandle.WaitOne(_reconnectInterval);
        }
    }
}
