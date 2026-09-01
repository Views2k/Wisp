using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

internal sealed class SetupTelemetryValidator(DateTimeOffset startedAtUtc, long startedTimestamp)
{
    private static readonly TimeSpan MaximumPacketAge = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumAdvanceGap = TimeSpan.FromMilliseconds(500);
    private VehicleState? _previous;
    private long _firstTimestamp;
    private long _gameElapsedMilliseconds;

    public int PacketCount { get; private set; }
    public int MovingPackets { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    public bool IsVerified =>
        PacketCount >= SetupCompletionRecord.MinimumPackets &&
        MovingPackets >= SetupCompletionRecord.MinimumMovingPackets &&
        Elapsed.TotalMilliseconds >= SetupCompletionRecord.MinimumElapsedMilliseconds &&
        _gameElapsedMilliseconds >= SetupCompletionRecord.MinimumElapsedMilliseconds;

    public void Observe(VehicleState? state, DateTimeOffset nowUtc, long nowTimestamp)
    {
        // Only the existing FH6 parser supplies states to this validator. The
        // local monotonic stamp excludes packets cached before an explicit test.
        if (state is null || state.ReceivedTimestamp is not { } receivedTimestamp ||
            receivedTimestamp <= startedTimestamp || receivedTimestamp > nowTimestamp ||
            Stopwatch.GetElapsedTime(receivedTimestamp, nowTimestamp) > MaximumPacketAge ||
            state.ReceivedAtUtc < startedAtUtc || state.ReceivedAtUtc > nowUtc ||
            nowUtc - state.ReceivedAtUtc > MaximumPacketAge)
        {
            Reset();
            return;
        }

        if (!state.IsRaceOn || state.CarOrdinal <= 0 || !Enum.IsDefined(state.Drivetrain) ||
            !float.IsFinite(state.GroundSpeedMetersPerSecond))
        {
            Reset();
            return;
        }

        if (_previous is { } previous)
        {
            var previousTimestamp = previous.ReceivedTimestamp!.Value;
            var receiveGap = Stopwatch.GetElapsedTime(previousTimestamp, receivedTimestamp);
            var advance = unchecked(state.GameTimestampMilliseconds - previous.GameTimestampMilliseconds);
            if (receivedTimestamp <= previousTimestamp ||
                receiveGap > MaximumAdvanceGap ||
                previous.CarOrdinal != state.CarOrdinal || previous.Drivetrain != state.Drivetrain ||
                advance > MaximumAdvanceGap.TotalMilliseconds)
            {
                Reset();
            }
            else if (advance == 0)
            {
                return;
            }
            else
            {
                _gameElapsedMilliseconds += advance;
            }
        }

        if (_previous is null)
        {
            _firstTimestamp = receivedTimestamp;
        }

        _previous = state;
        PacketCount++;
        if (Math.Abs(state.GroundSpeedMetersPerSecond) >= 0.5f)
        {
            MovingPackets++;
        }

        Elapsed = Stopwatch.GetElapsedTime(_firstTimestamp, receivedTimestamp);
    }

    private void Reset()
    {
        _previous = null;
        PacketCount = 0;
        MovingPackets = 0;
        Elapsed = TimeSpan.Zero;
        _gameElapsedMilliseconds = 0;
    }
}
