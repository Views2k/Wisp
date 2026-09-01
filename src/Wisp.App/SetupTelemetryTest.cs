using System.IO;
using System.Net.Sockets;
using System.Security;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.App;

public sealed record SetupTelemetryEvidence(
    int Port,
    int Packets,
    int MovingPackets,
    TimeSpan Elapsed,
    DateTimeOffset VerifiedAtUtc);

public sealed record SetupTestResult(SetupTelemetryEvidence? Evidence, string Message)
{
    public bool Passed => Evidence is not null;
}

internal interface ISetupTelemetrySource
{
    event EventHandler? PacketAvailable;
    VehicleState? Latest { get; }
    bool IsRunning { get; }
    int? ListeningPort { get; }
    Task BindAsync(int port);
    Task StopAsync();
    ReceiverStatistics GetStatistics(DateTimeOffset nowUtc);
}

internal sealed class SetupTelemetrySource(TelemetryUdpReceiver receiver) : ISetupTelemetrySource
{
    public event EventHandler? PacketAvailable
    {
        add => receiver.PacketAvailable += value;
        remove => receiver.PacketAvailable -= value;
    }

    public VehicleState? Latest => receiver.Latest;
    public bool IsRunning => receiver.IsRunning;
    public int? ListeningPort => receiver.ListeningPort;
    public Task BindAsync(int port) => receiver.RestartAsync(port);
    public Task StopAsync() => receiver.StopAsync();
    public ReceiverStatistics GetStatistics(DateTimeOffset nowUtc) => receiver.GetStatistics(nowUtc);
}

public sealed class SetupTelemetryTest
{
    private readonly ISetupTelemetrySource _source;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private int _running;

    internal SetupTelemetryTest(
        ISetupTelemetrySource source,
        TimeProvider? clock = null,
        TimeSpan? timeout = null)
    {
        _source = source;
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(SetupCompletionRecord.TestTimeoutSeconds);
    }

    public SetupTelemetryEvidence? SuccessfulEvidence { get; private set; }
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public void Invalidate() => SuccessfulEvidence = null;

    public async Task<SetupTestResult> RunAsync(
        string? portText,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A connection test is already running.");
        }

        SuccessfulEvidence = null;
        try
        {
            var result = await RunCoreAsync(portText, progress, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return new SetupTestResult(null, "Test cancelled. Your settings have not changed. You can retry when ready.");
            }

            SuccessfulEvidence = result.Evidence;
            return result;
        }
        catch (Exception exception) when (IsExpectedListenerFailure(exception))
        {
            return new SetupTestResult(null,
                "The listener could not finish or close cleanly. Setup remains unverified. " +
                "Close other telemetry tools and retry; if it repeats, exit Wisp and reopen setup.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SetupTestResult(null, "Test cancelled. Your settings have not changed. You can retry when ready.");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    internal static bool IsExpectedListenerFailure(Exception exception) =>
        exception is IOException or SocketException or InvalidOperationException or
            UnauthorizedAccessException or SecurityException;

    private async Task<SetupTestResult> RunCoreAsync(
        string? portText,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        EventHandler? packetHandler = null;
        var port = 0;
        var ownsListener = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            port = UdpPortInput.Parse(portText);
            await _source.BindAsync(port).ConfigureAwait(false);
            ownsListener = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!_source.IsRunning || _source.ListeningPort != port)
            {
                return new SetupTestResult(null, "The listener could not stay open. Close other telemetry tools and retry.");
            }

            var validator = new SetupTelemetryValidator(_clock.GetUtcNow(), _clock.GetTimestamp());
            var completion = new TaskCompletionSource<SetupTelemetryEvidence>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            var reportedPackets = -1;
            packetHandler = (_, _) =>
            {
                lock (gate)
                {
                    if (completion.Task.IsCompleted || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var now = _clock.GetUtcNow();
                    validator.Observe(_source.Latest, now, _clock.GetTimestamp());
                    if (!_source.IsRunning || _source.ListeningPort != port)
                    {
                        return;
                    }

                    var packetCount = Math.Min(validator.PacketCount, SetupCompletionRecord.MinimumPackets);
                    if (packetCount != reportedPackets)
                    {
                        reportedPackets = packetCount;
                        progress?.Report(
                            $"Checking Data Out: {packetCount}/{SetupCompletionRecord.MinimumPackets} packets. " +
                            "Keep driving briefly; Wisp also checks advancing time and movement.");
                    }

                    if (validator.IsVerified)
                    {
                        completion.TrySetResult(new SetupTelemetryEvidence(
                            port, validator.PacketCount, validator.MovingPackets, validator.Elapsed, now));
                    }
                }
            };
            _source.PacketAvailable += packetHandler;
            progress?.Report(
                $"Port {port} is open. Switch to FH6, leave the menus, and drive gently. " +
                $"This test waits up to {SetupCompletionRecord.TestTimeoutSeconds} seconds.");

            var evidence = await completion.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new SetupTestResult(evidence, "Data Out verified: fresh parsed packets with advancing timestamps and speed data. Game display settings still need your confirmation.");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return new SetupTestResult(null, exception.Message.Split(Environment.NewLine)[0]);
        }
        catch (SocketException exception)
        {
            var detail = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? "Another app is already using this port. Close its listener, or choose an unused port and update FH6 to match."
                : "Windows could not bind this port. Try an unused port; check local firewall/security rules if it still fails.";
            return new SetupTestResult(null, $"Cannot listen on UDP port {port}. {detail}");
        }
        catch (TimeoutException)
        {
            return new SetupTestResult(null, TimeoutMessage(_source.GetStatistics(_clock.GetUtcNow()), _source.IsRunning));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SetupTestResult(null, "Test cancelled. Your settings have not changed. You can retry when ready.");
        }
        finally
        {
            if (packetHandler is not null)
            {
                _source.PacketAvailable -= packetHandler;
            }

            if (ownsListener)
            {
                await _source.StopAsync().ConfigureAwait(false);
            }
        }
    }

    internal static string TimeoutMessage(ReceiverStatistics statistics, bool listenerRunning)
    {
        if (!listenerRunning || statistics.ListenerError is not null)
        {
            return "The telemetry listener stopped. Retry the test; if it repeats, check local firewall/security rules and close competing telemetry tools.";
        }

        if (statistics.AcceptedPackets == 0 && statistics.RejectedPackets > 0)
        {
            return "Packets arrived, but they were not valid FH6 Data Out data. Confirm FH6 is sending to Wisp's port, and stop other senders using that port.";
        }

        if (statistics.AcceptedPackets > 0)
        {
            return "Data arrived, but the required live sequence was not complete. Leave pause/photo/menu screens, drive gently for a few seconds, and retry. Frozen timestamps and stale packets do not count.";
        }

        return "No FH6 data arrived. In Settings > HUD and Gameplay, enable Data Out, set its IP to 127.0.0.1, and match the port shown here. Leave the menus and drive, then retry. Check local firewall/security rules if needed.";
    }
}
