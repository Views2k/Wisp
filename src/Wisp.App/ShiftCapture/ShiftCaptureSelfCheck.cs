using System.Text.Json;
using Wisp.Core;

namespace Wisp.App;

// In-memory recorder regression checks. No live session, input device or game is involved.
internal static class ShiftCaptureSelfCheck
{
    internal static ShiftCaptureSelfCheckResult Run() => new(
    [
        Check("same-timestamp-observations", "shared-timestamp telemetry handling", SharedTimestampObservations),
        Check("parked-neutral-bindings", "B/X binding with transient neutral", ParkedNeutralBindings),
        Check("context-and-clock-guards", "stale-clock and context isolation", ContextAndClockGuards),
        Check("finite-summary-schema", "recorder summary serialization", FiniteSummarySchema)
    ]);

    private static ShiftCaptureSelfCheckCase Check(string name, string description, Func<bool> check)
    {
        try
        {
            return check() ? new(name, true, null) : new(name, false, description + " did not match its expected result.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(name, false, description + " could not complete (" + ex.GetType().Name + ").");
        }
    }

    private static bool SharedTimestampObservations()
    {
        var result = SharedTimestampFixture();
        var shift = result.Upshifts.SingleOrDefault();
        var second = result.Profiles.SingleOrDefault()?.Gears.SingleOrDefault(gear => gear.Gear == 2);
        return result.TelemetryPackets == 5 && result.RepeatedGameTimestamps == 4 &&
            result.InvalidOrUnassociatedSamples == 0 && second is { Samples: 2, MaximumRpm: 7_000, HighRpmPowerCutCandidates: 1 } &&
            shift is { FromGear: 2, ToGear: 3, CleanFullLoad: true, PositiveTorqueSustained: true } &&
            shift.GearChange == new ShiftCaptureTimeBracket(1_010, 1_020) && shift.ButtonBracket is not null;
    }

    private static ShiftCaptureCoverageSnapshot SharedTimestampFixture()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 100, 6_900), 1_000, "self-check");
        capture.ObserveTelemetry(State(2, 100, 7_000, torque: 0), 1_010, "self-check");
        capture.ObserveButton(Button("B", 1_011, 1_019), 1_019);
        capture.ObserveTelemetry(State(3, 100, 5_000), 1_020, "self-check");
        capture.ObserveTelemetry(State(3, 100, 5_010), 1_035, "self-check");
        capture.ObserveTelemetry(State(3, 100, 5_020), 1_050, "self-check");
        return capture.Snapshot();
    }

    private static bool ParkedNeutralBindings()
    {
        var bindings = new ShiftCaptureBindingCheck(1_000);
        bindings.ObserveState(Parked(1, 100), 990);
        // Telemetry can arrive before the input worker reports its measured edge.
        bindings.ObserveState(Parked(0, 101), 1_001);
        bindings.ObserveState(Parked(2, 102), 1_002);
        bindings.ObserveButton(Button("B", 1_000, 1_004), 1_004);
        if (!bindings.UpVerified || bindings.DownVerified) return false;
        bindings.ObserveState(Parked(2, 103), 1_090);
        bindings.ObserveButton(Button("X", 1_100, 1_104), 1_104);
        // The input worker can also overtake an earlier queued telemetry packet.
        bindings.ObserveState(Parked(2, 104), 1_099);
        bindings.ObserveState(Parked(0, 105), 1_110);
        bindings.ObserveState(Parked(1, 106), 1_125);
        if (!bindings.UpVerified || !bindings.DownVerified) return false;
        bindings.Reset();
        bindings.ObserveState(Parked(1, 200), 1_990);
        bindings.ObserveButton(Button("B", 2_000, 2_004), 2_004);
        bindings.ObserveState(State(0, 201, 900), 2_010); // Moving context must reject.
        bindings.ObserveState(Parked(2, 202), 2_025);
        return !bindings.UpVerified && !bindings.DownVerified;
    }

    private static bool ContextAndClockGuards()
    {
        var capture = new ShiftCaptureCoverage(1_000);
        capture.ObserveTelemetry(State(2, 100, 7_000), 1_000, "self-check");
        for (var qpc = 1_050; qpc <= 1_400; qpc += 50)
            capture.ObserveTelemetry(State(0, 100, 6_000, torque: 0), qpc, "self-check");
        capture.ObserveTelemetry(State(3, 100, 5_000), 1_450, "self-check");
        var stalled = capture.Snapshot();
        if (stalled.Upshifts.Length != 0 || stalled.ContinuityGaps != 1) return false;
        capture.ObserveTelemetry(State(3, 101, 5_000), 1_500, "self-check");
        capture.ObserveTelemetry(State(4, 100, 4_000), 1_550, "self-check");
        if (capture.Snapshot().InvalidOrUnassociatedSamples != 1) return false;
        capture.ObserveTelemetry(State(3, 102, 5_000), 1_600, "self-check");
        capture.ObserveButton(Button("B", 1_601, 1_604), 1_604);
        capture.InvalidateContext();
        capture.ObserveTelemetry(State(4, 102, 4_000), 1_650, "self-check");
        return capture.Snapshot().Upshifts.Length == 0;
    }

    private static bool FiniteSummarySchema()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Strict numeric serialization catches non-finite values in derived summaries.
        var element = JsonSerializer.SerializeToElement(new
        {
            coverage = SharedTimestampFixture(),
            emptyPolling = new ShiftCapturePollStatistics(1_000).Snapshot()
        }, options);
        return element.GetProperty("coverage").GetProperty("repeatedGameTimestamps").GetInt64() == 4 &&
            element.GetProperty("coverage").GetProperty("upshifts").GetArrayLength() == 1 &&
            element.GetProperty("emptyPolling").GetProperty("total").GetProperty("apiRead")
                .GetProperty("meanMilliseconds").ValueKind == JsonValueKind.Null;
    }

    private static ShiftCaptureButtonEvent Button(string name, long earliest, long latest) =>
        new(name, name == "B" ? "upshift" : "downshift", "pressed", earliest, earliest + 1,
            latest - 1, latest, earliest, latest, "Synthetic recorder self-check; not user input");

    private static VehicleState Parked(int gear, uint clock) => State(gear, clock, 900) with
    {
        GroundSpeedMetersPerSecond = 0,
        Accelerator = 0
    };

    private static VehicleState State(int gear, uint clock, float rpm, float torque = 500) => new()
    {
        IsRaceOn = true,
        CarOrdinal = 1,
        NumCylinders = 8,
        Gear = (TransmissionGear)gear,
        GameTimestampMilliseconds = clock,
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        Drivetrain = DrivetrainType.RearWheelDrive,
        EngineRpm = rpm,
        EngineMaximumRpm = 7_000,
        GroundSpeedMetersPerSecond = 30,
        Accelerator = 255,
        Brake = 0,
        Steering = 0,
        TorqueNm = torque,
        PowerWatts = torque * rpm / 9.5493f,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 2
    };
}

internal sealed record ShiftCaptureSelfCheckCase(string Name, bool Passed, string? Failure);
internal sealed record ShiftCaptureSelfCheckResult(ShiftCaptureSelfCheckCase[] Checks)
{
    public bool Passed => Checks.Length == 4 && Checks.All(check => check.Passed);
    public string Status => Passed ? "Recorder self-check passed. Live channels still require verification." :
        "Recorder self-check failed. Capture was not started. " +
        (Checks.FirstOrDefault(check => !check.Passed)?.Failure ?? "Recorder checks are incomplete.");
    public string Scope => "Isolated recorder logic checks only; not live game, controller, display, shift accuracy or performance validation.";
}
