using System.Globalization;
using System.IO;
using System.Text;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public static class RunCsvExporter
{
    public const string Header = "time_s,segment,is_driving,is_race_on,game_timestamp_ms,car_ordinal,drivetrain,num_cylinders,ground_speed_mps,wheel_speed_mps,front_radius_m,rear_radius_m,engine_rpm,engine_maximum_rpm,gear,steering_raw,accelerator_raw,brake_raw,lateral_acceleration_mps2,longitudinal_acceleration_mps2,power_w,torque_nm,boost_psi,wheel_rotation_fl_rad_s,wheel_rotation_fr_rad_s,wheel_rotation_rl_rad_s,wheel_rotation_rr_rad_s,tire_slip_ratio_fl,tire_slip_ratio_fr,tire_slip_ratio_rl,tire_slip_ratio_rr,tire_slip_angle_fl,tire_slip_angle_fr,tire_slip_angle_rl,tire_slip_angle_rr,suspension_travel_fl,suspension_travel_fr,suspension_travel_rl,suspension_travel_rr,tire_temperature_fl_f,tire_temperature_fr_f,tire_temperature_rl_f,tire_temperature_rr_f";

    public static Task WriteAsync(RecordedRun run, string newFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFilePath);
        return Task.Run(() => WriteCoreAsync(run, newFilePath, cancellationToken), cancellationToken);
    }

    private static async Task WriteCoreAsync(RecordedRun run, string newFilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(newFilePath);
        if (File.Exists(destination)) throw new IOException("The export file already exists. Choose a new filename.");
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, ".wisp-csv-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var writer = new StreamWriter(file, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
                {
                    await writer.WriteLineAsync(Header.AsMemory(), cancellationToken).ConfigureAwait(false);
                    foreach (var sample in run.Samples)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(Row(sample).AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            // The final export is never removed, including when a same-name file won a race.
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Row(RunSample sample)
    {
        var state = sample.State;
        var values = new List<string>(43)
        {
            Number(sample.ElapsedSeconds), Integer(sample.Segment), Bit(sample.IsDriving), Bit(state.IsRaceOn),
            state.GameTimestampMilliseconds.ToString(CultureInfo.InvariantCulture), Integer(state.CarOrdinal), Integer((int)state.Drivetrain), Integer(state.NumCylinders),
            Number(state.GroundSpeedMetersPerSecond), Number(sample.WheelSpeedMetersPerSecond), Number(sample.FrontRadiusMeters), Number(sample.RearRadiusMeters),
            Number(state.EngineRpm), Number(state.EngineMaximumRpm), Integer((int)state.Gear), Integer(state.Steering), Integer(state.Accelerator), Integer(state.Brake),
            Number(state.LateralAccelerationMetersPerSecondSquared), Number(state.LongitudinalAccelerationMetersPerSecondSquared),
            Number(state.PowerWatts), Number(state.TorqueNm), Number(state.BoostPressurePsi)
        };
        Wheels(state.WheelRotationRadiansPerSecond); Wheels(state.TireSlipRatio); Wheels(state.TireSlipAngle);
        Wheels(state.NormalizedSuspensionTravel); Wheels(state.TireTemperatureFahrenheit);
        return string.Join(',', values);

        void Wheels(WheelValues wheels)
        {
            values.Add(Number(wheels.FrontLeft)); values.Add(Number(wheels.FrontRight));
            values.Add(Number(wheels.RearLeft)); values.Add(Number(wheels.RearRight));
        }
    }
    private static string Number(double? value) => value is { } number && double.IsFinite(number) ? number.ToString("R", CultureInfo.InvariantCulture) : "";
    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Bit(bool value) => value ? "1" : "0";
}
