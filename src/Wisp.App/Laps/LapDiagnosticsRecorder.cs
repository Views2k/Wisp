using System.IO;
using System.Globalization;
using System.IO.Compression;
using Wisp.Core;

namespace Wisp.App.Laps;

// Private test builds record every packet the lap tracker receives, with what it showed, so a
// session can be replayed exactly. Files: %LOCALAPPDATA%\Wisp\LapDiagnostics\laps-*.csv.gz.
internal sealed class LapDiagnosticsRecorder : IDisposable
{
    private const int KeptFiles = 8;
    private readonly StreamWriter _writer;
    private DateTimeOffset _flushed;

    private LapDiagnosticsRecorder(StreamWriter writer) => _writer = writer;

    internal static LapDiagnosticsRecorder? Start(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var old in Directory.GetFiles(directory, "laps-*.csv.gz").OrderDescending().Skip(KeptFiles - 1))
                File.Delete(old);
            var path = Path.Combine(directory, $"laps-{DateTime.Now:yyyyMMdd-HHmmss}.csv.gz");
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
            var writer = new StreamWriter(new GZipStream(file, CompressionLevel.Fastest), bufferSize: 1 << 16);
            writer.WriteLine($"# Wisp {ApplicationVersionInfo.DisplayVersion} {ApplicationVersionInfo.DiagnosticBuildLabel}");
            writer.WriteLine("received_utc_ticks,received_qpc,timestamp_ms,race_on,car,x,y,z,speed,lap_current,lap_last,race_time,lap_number,timing,reference,status,delta,reference_seconds");
            return new(writer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void Write(VehicleState state, LapTimingMode timing, LapDeltaReference reference, LapDeltaReading reading)
    {
        try
        {
            var lap = state.Lap;
            _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{state.ReceivedAtUtc.UtcTicks},{state.ReceivedTimestamp},{state.GameTimestampMilliseconds},{(state.IsRaceOn ? 1 : 0)},{state.CarOrdinal}," +
                $"{lap?.Position.X:R},{lap?.Position.Y:R},{lap?.Position.Z:R},{state.GroundSpeedMetersPerSecond:R}," +
                $"{lap?.CurrentLapSeconds:R},{lap?.LastLapSeconds:R},{lap?.RaceSeconds:R},{lap?.LapNumber}," +
                $"{(int)timing},{(int)reference},{(int)reading.Status},{reading.Seconds:R},{reading.ReferenceSeconds:R}"));
            if (state.ReceivedAtUtc - _flushed < TimeSpan.FromSeconds(2)) return;
            _flushed = state.ReceivedAtUtc;
            _writer.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        try { _writer.Dispose(); }
        catch (IOException) { }
    }
}
