using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wisp.Core;

namespace Wisp.App;

internal sealed class ShiftCalibrationStore(string? directory)
{
    private const int Schema = 1;
    private const long MaximumBytes = 512 * 1024;

    internal ShiftCalibrationResult? Load(string build, ShiftCalibrationContext context)
    {
        if (directory is null) return null;
        var path = FilePath(build, context.Fingerprint);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            var saved = JsonSerializer.Deserialize<SavedCalibration>(File.ReadAllBytes(path));
            if (saved is null || saved.Schema != Schema || saved.Build != build ||
                saved.Fingerprint != context.Fingerprint || saved.CarOrdinal != context.CarOrdinal ||
                saved.Samples is null || saved.Samples.Length is < 2 or > 4096)
                return null;
            return ShiftCalibrationSession.TryRestore(context, saved.Samples, saved.EmpiricalUpperRpm,
                saved.ConfirmingUpshifts, saved.AcceptedSamples, out var result) ? result : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
                                      JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Data is written away from ingress/rendering. Only the final atomic rename
    // is serialized with cancellation/configuration changes by the caller.
    internal bool Save(string build, ShiftCalibrationResult result, Func<Action, bool> commit)
    {
        if (!result.Ready || result.Profile is null) return false;
        if (directory is null) return commit(static () => { });
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = FilePath(build, result.Context.Fingerprint);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var saved = new SavedCalibration(Schema, build, result.Context.Fingerprint,
                result.Context.CarOrdinal, result.Profile.Samples.ToArray(), result.EmpiricalUpperRpm,
                result.ConfirmingUpshifts, result.AcceptedSamples);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(saved);
            if (bytes.Length > MaximumBytes) return false;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            return commit(() => File.Move(temporary, path, overwrite: true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
                                      ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        }
    }

    private string FilePath(string build, string fingerprint) => Path.Combine(directory!,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Schema}:{build}:{fingerprint}"))) + ".json");

    private sealed record SavedCalibration(int Schema, string Build, string Fingerprint, int CarOrdinal,
        AccelerationShiftSample[] Samples, double? EmpiricalUpperRpm, int ConfirmingUpshifts, int AcceptedSamples);
}
