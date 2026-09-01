using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wisp.Core;

namespace Wisp.Telemetry;

public readonly record struct ReceiverStatistics(
    long AcceptedPackets,
    long RejectedPackets,
    double PacketsPerSecond,
    PacketParseError LastParseError,
    string? ListenerError);

public sealed class TelemetryUdpReceiver : IAsyncDisposable
{
    private const int MaximumDrainDatagrams = 64;

    private readonly Fh6PacketParser _parser;
    private readonly object _statisticsGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private ReceiverSession? _session;
    private VehicleState? _latest;
    private long _acceptedPackets;
    private long _rejectedPackets;
    private long _previousAcceptedPackets;
    private DateTimeOffset _previousRateAtUtc = DateTimeOffset.UtcNow;
    private double _cachedPacketRate;
    private int _lastParseError;
    private string? _listenerError;
    private int _disposed;

    public TelemetryUdpReceiver(Fh6PacketParser? parser = null)
    {
        _parser = parser ?? new Fh6PacketParser();
    }

    public event EventHandler? PacketAvailable;

    public VehicleState? Latest => Volatile.Read(ref _latest);

    public bool IsRunning => Volatile.Read(ref _session)?.ReceiveTask is { IsCompleted: false };

    public int? ListeningPort => Volatile.Read(ref _session)?.Port;

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = Volatile.Read(ref _session);
            if (current?.ReceiveTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The telemetry listener is already running.");
            }

            if (current is not null)
            {
                Volatile.Write(ref _session, null);
                await StopSessionAsync(current).ConfigureAwait(false);
            }

            var replacement = CreateBoundSession(port, cancellationToken);
            try
            {
                ResetSessionState();
                replacement.ReceiveTask = ReceiveLoopAsync(
                    replacement.Socket,
                    replacement.Cancellation.Token);
                Volatile.Write(ref _session, replacement);
            }
            catch
            {
                await StopSessionAsync(replacement).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(int port, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = Volatile.Read(ref _session);
            if (current is { Port: var currentPort, ReceiveTask.IsCompleted: false } && currentPort == port)
            {
                return;
            }

            // Binding first keeps the current listener alive when the requested
            // port is unavailable. No state is changed until this succeeds.
            var replacement = CreateBoundSession(port, cancellationToken);
            try
            {
                if (current is not null)
                {
                    Volatile.Write(ref _session, null);
                    await StopSessionAsync(current).ConfigureAwait(false);
                }

                ResetSessionState();
                replacement.ReceiveTask = ReceiveLoopAsync(
                    replacement.Socket,
                    replacement.Cancellation.Token);
                Volatile.Write(ref _session, replacement);
            }
            catch
            {
                await StopSessionAsync(replacement).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _session);
            Volatile.Write(ref _session, null);
            if (current is not null)
            {
                await StopSessionAsync(current).ConfigureAwait(false);
            }

            ResetSessionState();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ReceiverStatistics GetStatistics(DateTimeOffset nowUtc)
    {
        lock (_statisticsGate)
        {
            var elapsed = nowUtc - _previousRateAtUtc;
            if (elapsed >= TimeSpan.FromMilliseconds(500))
            {
                var accepted = Interlocked.Read(ref _acceptedPackets);
                _cachedPacketRate = (accepted - _previousAcceptedPackets) / elapsed.TotalSeconds;
                _previousAcceptedPackets = accepted;
                _previousRateAtUtc = nowUtc;
            }

            return new ReceiverStatistics(
                Interlocked.Read(ref _acceptedPackets),
                Interlocked.Read(ref _rejectedPackets),
                _cachedPacketRate,
                (PacketParseError)Volatile.Read(ref _lastParseError),
                Volatile.Read(ref _listenerError));
        }
    }

    public static void ValidatePort(int port)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Choose a port from 1024 through 65535.");
        }

        if (port is >= 5200 and <= 5300)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Forza reserves ports 5200 through 5300; choose another port.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var current = Volatile.Read(ref _session);
            Volatile.Write(ref _session, null);
            if (current is not null)
            {
                await StopSessionAsync(current).ConfigureAwait(false);
            }

            ResetSessionState();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static ReceiverSession CreateBoundSession(int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
            ReceiveBufferSize = Fh6PacketLayout.PacketLength * 32
        };

        CancellationTokenSource? linkedCancellation = null;
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return new ReceiverSession(port, socket, linkedCancellation);
        }
        catch
        {
            linkedCancellation?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    private static async Task StopSessionAsync(ReceiverSession session)
    {
        session.Cancellation.Cancel();
        session.Socket.Dispose();
        if (session.ReceiveTask is not null)
        {
            try
            {
                await session.ReceiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        session.Cancellation.Dispose();
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(2048, pinned: true);
        EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Loopback, 0);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    remoteEndpoint,
                    cancellationToken).ConfigureAwait(false);

                var receivedBytes = DrainToNewestDatagram(
                    socket,
                    buffer,
                    result.ReceivedBytes,
                    ref remoteEndpoint);
                var receivedAt = DateTimeOffset.UtcNow;
                var receivedTimestamp = Stopwatch.GetTimestamp();
                if (_parser.TryParse(buffer.AsSpan(0, receivedBytes), receivedAt, out var state, out var error,
                        receivedTimestamp))
                {
                    Volatile.Write(ref _latest, state);
                    Interlocked.Increment(ref _acceptedPackets);
                    NotifyPacketAvailable();
                }
                else
                {
                    Volatile.Write(ref _lastParseError, (int)error);
                    Interlocked.Increment(ref _rejectedPackets);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _listenerError, exception.Message);
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static int DrainToNewestDatagram(
        Socket socket,
        byte[] buffer,
        int receivedBytes,
        ref EndPoint remoteEndpoint)
    {
        for (var drained = 0; drained < MaximumDrainDatagrams && socket.Available > 0; drained++)
        {
            receivedBytes = socket.ReceiveFrom(
                buffer,
                0,
                buffer.Length,
                SocketFlags.None,
                ref remoteEndpoint);
        }

        return receivedBytes;
    }

    private void NotifyPacketAvailable()
    {
        var handler = PacketAvailable;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch
        {
            // A UI notification must never terminate the UDP receive loop.
        }
    }

    private void ResetSessionState()
    {
        Volatile.Write(ref _latest, null);
        lock (_statisticsGate)
        {
            Interlocked.Exchange(ref _acceptedPackets, 0);
            Interlocked.Exchange(ref _rejectedPackets, 0);
            Volatile.Write(ref _lastParseError, (int)PacketParseError.None);
            Volatile.Write(ref _listenerError, null);
            _previousAcceptedPackets = 0;
            _cachedPacketRate = 0;
            _previousRateAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class ReceiverSession(
        int port,
        Socket socket,
        CancellationTokenSource cancellation)
    {
        public int Port { get; } = port;
        public Socket Socket { get; } = socket;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? ReceiveTask { get; set; }
    }
}
