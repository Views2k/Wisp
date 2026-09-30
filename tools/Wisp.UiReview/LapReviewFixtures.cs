using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.UiReview;

internal static class LapReviewFixtures
{
    internal static RecordedRun Create(bool reference = false)
    {
        const int samplesPerLap = 600;
        var stepMilliseconds = reference ? 102 : 100;
        var duration = samplesPerLap * stepMilliseconds / 1000f;
        var started = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, samplesPerLap + 2).Select(index =>
        {
            var phase = index % samplesPerLap / (double)samplesPerLap;
            var angle = phase * Math.Tau;
            var elapsed = index * stepMilliseconds / 1000d;
            var speed = (float)(34 + 9 * Math.Sin(angle * 2) + (reference ? -1.2 : 0));
            var braking = phase is > .22 and < .31 or > .69 and < .78;
            var state = new VehicleState
            {
                IsRaceOn = true,
                GameTimestampMilliseconds = (uint)(index * stepMilliseconds),
                ReceivedAtUtc = started.AddSeconds(elapsed),
                CarOrdinal = 101,
                Drivetrain = DrivetrainType.RearWheelDrive,
                NumCylinders = 8,
                Lap = new(new((float)(210 * Math.Cos(angle)), 0, (float)(140 * Math.Sin(angle))),
                    index % samplesPerLap * stepMilliseconds / 1000f, duration, (float)elapsed,
                    (ushort)(index / samplesPerLap), 1),
                GroundSpeedMetersPerSecond = speed,
                WheelRotationRadiansPerSecond = new(speed / .34f, speed / .34f, speed / .35f, speed / .35f),
                TireSlipRatio = new(.01f, .02f, .08f, .07f),
                TireSlipAngle = new(.02f, .03f, .06f, .05f),
                NormalizedSuspensionTravel = new(.45f, .5f, .55f, .48f),
                LateralAccelerationMetersPerSecondSquared = (float)Math.Sin(angle) * 9,
                LongitudinalAccelerationMetersPerSecondSquared = braking ? -7 : 3,
                EngineRpm = 3200 + speed * 65,
                EngineMaximumRpm = 8500,
                Gear = phase is > .2 and < .4 ? TransmissionGear.Third : TransmissionGear.Fourth,
                Steering = (sbyte)(Math.Sin(angle) * 48),
                Accelerator = braking ? (byte)0 : (byte)255,
                Brake = braking ? (byte)(reference ? 155 : 190) : (byte)0,
                TireTemperatureFahrenheit = new(178, 182, 190, 188),
                PowerWatts = speed * 7000,
                TorqueNm = 480,
                BoostPressurePsi = braking ? -4 : 14
            };
            return new RunSample { ElapsedSeconds = elapsed, IsDriving = true, State = state };
        }).ToArray();
        return new()
        {
            Id = Guid.Parse(reference ? "b1c9eaad-3af8-4493-b93e-877cdb7c2931" : "aad0dcca-4827-47aa-9bba-fbd9a3c6b43e"),
            Name = reference ? "Circuit · reference tune" : "Circuit · current tune",
            Tune = reference ? "Baseline" : "Revised braking",
            StartedAtUtc = started,
            FinishReason = "Synthetic completed lap",
            LapTimingMode = LapTimingMode.GameLaps,
            Samples = samples
        };
    }
}
