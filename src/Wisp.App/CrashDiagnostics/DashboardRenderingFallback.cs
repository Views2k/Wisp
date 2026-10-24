using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Wisp.App.CrashDiagnostics;

// WPF reports a dead render thread as COMException UCEERR_RENDERTHREADFAILURE on the dashboard
// thread, and the process cannot continue. The next launch draws the dashboard in software, which
// does not use that thread. The HUD renderer is native Direct3D 11 and is not affected.
internal static class DashboardRenderingFallback
{
    internal const int RenderThreadFailure = unchecked((int)0x88980406);
    internal const string MarkerName = "software-rendering.marker";

    internal static bool IsRenderThreadFailure(Exception? exception)
    {
        for (var error = exception; error is not null; error = error.InnerException)
        {
            if (error is COMException { HResult: RenderThreadFailure }) return true;
        }
        return false;
    }

    // Software drawing works around one failure; it is not a permanent setting. The request lapses
    // after two weeks, and the next render thread failure renews it.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    internal static string MarkerPath(string directory) => Path.Combine(directory, MarkerName);

    internal static bool IsRequested(string directory, DateTimeOffset now)
    {
        try
        {
            var path = MarkerPath(directory);
            if (!File.Exists(path)) return false;
            if (DateTimeOffset.TryParseExact(File.ReadAllText(path).Trim(), "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var requested) && now - requested is { Ticks: >= 0 } age && age < Lifetime)
                return true;
            File.Delete(path);
            return false;
        }
        catch (Exception) { return false; }
    }

    internal static bool TryRequest(string directory, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(MarkerPath(directory), now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception) { return false; }
    }
}
