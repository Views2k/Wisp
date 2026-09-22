using System.Buffers.Binary;
using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

internal enum NativeShiftLiveReadOutcome
{
    Success,
    UnsupportedBuild,
    InitialIdentityFailed,
    FinalIdentityFailed,
    InitialControlsUnavailable,
    RepeatedControlsUnavailable,
    EngineSpeedUnavailable,
    SecondaryBoundaryUnavailable,
    OutputControlUnavailable,
    BranchFlagUnavailable,
    InvalidControls,
    InvalidEngineSpeed,
    InvalidSecondaryBoundary,
    InvalidOutputControl,
    InconsistentControls
}

internal readonly record struct NativeShiftLiveReadDiagnostic(
    NativeShiftLiveReadOutcome Outcome, int Attempts, long ObservationTimestamp);

internal static class NativeShiftLiveStateReader
{
    private const double RadiansToRpm = 30 / Math.PI;
    private const ulong AlternateLimiterBranchRva = 0xA8EA2AC;

    internal static bool TryRead(IReadOnlyProcessMemory memory, ulong moduleBase,
        ulong source, ulong provider, int carOrdinal, NativeHudCompatibilityPack pack,
        out ShiftCueLiveState? state) =>
        TryRead(memory, moduleBase, source, provider, carOrdinal, pack, out state, out _);

    internal static bool TryRead(IReadOnlyProcessMemory memory, ulong moduleBase,
        ulong source, ulong provider, int carOrdinal, NativeHudCompatibilityPack pack,
        out ShiftCueLiveState? state, out NativeShiftLiveReadDiagnostic diagnostic)
    {
        state = null;
        diagnostic = default;
        if (!NativeShiftPerformanceReader.Supported(pack))
            return Fail(NativeShiftLiveReadOutcome.UnsupportedBuild, -1, Stopwatch.GetTimestamp(), out diagnostic);

        Span<byte> gears = stackalloc byte[3];
        Span<byte> flags = stackalloc byte[8];
        Span<byte> repeatedGears = stackalloc byte[3];
        Span<byte> repeatedFlags = stackalloc byte[8];
        // A physics update can change the small control-state bracket during a
        // read. Retry that race once using wholly new observations. Never reuse
        // a prior frame or retry an unreadable, invalid or changed-car state.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            if (!NativeShiftPerformanceReader.IdentityMatches(memory, moduleBase, source, provider, carOrdinal, pack))
                return Fail(NativeShiftLiveReadOutcome.InitialIdentityFailed, attempt, started, out diagnostic);
            if (!memory.TryReadBytes(provider + 8, gears) || !memory.TryReadBytes(provider + 0x224, flags))
                return Fail(NativeShiftLiveReadOutcome.InitialControlsUnavailable, attempt, started, out diagnostic);
            if (!memory.TryReadSingle(provider + 0x1B0, out var omega))
                return Fail(NativeShiftLiveReadOutcome.EngineSpeedUnavailable, attempt, started, out diagnostic);
            if (!memory.TryReadSingle(provider + 0x678, out var secondaryBoundary))
                return Fail(NativeShiftLiveReadOutcome.SecondaryBoundaryUnavailable, attempt, started, out diagnostic);
            if (!memory.TryReadSingle(provider + 0x2520, out var outputControl))
                return Fail(NativeShiftLiveReadOutcome.OutputControlUnavailable, attempt, started, out diagnostic);
            if (!memory.TryReadByte(moduleBase + AlternateLimiterBranchRva, out var alternateBranch))
                return Fail(NativeShiftLiveReadOutcome.BranchFlagUnavailable, attempt, started, out diagnostic);
            if (!memory.TryReadBytes(provider + 8, repeatedGears) || !memory.TryReadBytes(provider + 0x224, repeatedFlags))
                return Fail(NativeShiftLiveReadOutcome.RepeatedControlsUnavailable, attempt, started, out diagnostic);
            if (!NativeShiftPerformanceReader.IdentityMatches(memory, moduleBase, source, provider, carOrdinal, pack))
                return Fail(NativeShiftLiveReadOutcome.FinalIdentityFailed, attempt, started, out diagnostic);

            var limiter = BinaryPrimitives.ReadUInt32LittleEndian(flags[..4]);
            var secondary = BinaryPrimitives.ReadUInt32LittleEndian(flags[4..]);
            var rpm = omega * RadiansToRpm;
            // The secondary threshold can be stale or uninitialized while its path
            // is inactive. Never promote that stored value into an operative limit.
            var secondaryRpm = secondary == 1 ? secondaryBoundary * RadiansToRpm : 0;
            if (!ValidControls(gears, flags) || !ValidControls(repeatedGears, repeatedFlags))
                return Fail(NativeShiftLiveReadOutcome.InvalidControls, attempt, started, out diagnostic);
            if (!double.IsFinite(rpm) || rpm < 0 || rpm > 40_000)
                return Fail(NativeShiftLiveReadOutcome.InvalidEngineSpeed, attempt, started, out diagnostic);
            if (secondary == 1 && (!double.IsFinite(secondaryRpm) || secondaryRpm <= 0 || secondaryRpm > 40_000))
                return Fail(NativeShiftLiveReadOutcome.InvalidSecondaryBoundary, attempt, started, out diagnostic);
            if (!float.IsFinite(outputControl))
                return Fail(NativeShiftLiveReadOutcome.InvalidOutputControl, attempt, started, out diagnostic);
            if (!gears.SequenceEqual(repeatedGears) || !flags.SequenceEqual(repeatedFlags))
            {
                diagnostic = new(NativeShiftLiveReadOutcome.InconsistentControls, attempt + 1, started);
                continue;
            }

            // Timestamp the beginning, so read latency cannot make old observations
            // appear fresh. Changing RPM is deliberately not equality-tested.
            state = new(carOrdinal, started, rpm, gears[0], gears[1], gears[2],
                limiter == 1, secondary == 1, secondaryRpm, outputControl, alternateBranch != 0);
            diagnostic = new(NativeShiftLiveReadOutcome.Success, attempt + 1, started);
            return true;
        }
        return false;
    }

    private static bool Fail(NativeShiftLiveReadOutcome outcome, int attempt, long started,
        out NativeShiftLiveReadDiagnostic diagnostic)
    {
        diagnostic = new(outcome, attempt + 1, started);
        return false;
    }

    private static bool ValidControls(ReadOnlySpan<byte> gears, ReadOnlySpan<byte> flags) =>
        gears[0] <= 15 && gears[1] <= 15 && gears[2] <= 15 &&
        BinaryPrimitives.ReadUInt32LittleEndian(flags[..4]) <= 1 &&
        BinaryPrimitives.ReadUInt32LittleEndian(flags[4..]) <= 1;
}
