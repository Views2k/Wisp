using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryPower(string Scheme, string Supply, string BatterySaver)
{
    internal bool IsValid => Scheme is "balanced" or "high-performance" or "power-saver" or "custom" or "unknown" &&
        Supply is "ac" or "battery" or "unknown" && BatterySaver is "enabled" or "disabled" or "unknown";
}

internal sealed record SupplementarySoftware(string Family, string State)
{
    internal bool IsValid => Family is "discord" or "rtss" or "game-bar" or "obs" or "simhub" or "overwolf" &&
        State is "present" or "not-observed" or "unknown";
}

internal static class SupplementarySystemContext
{
    private static readonly (string Family, string[] Names)[] SoftwareFamilies =
    [
        ("discord", ["Discord", "DiscordPTB", "DiscordCanary"]),
        ("rtss", ["RTSS"]), ("game-bar", ["GameBar"]), ("obs", ["obs64", "obs32"]),
        ("simhub", ["SimHubWPF"]), ("overwolf", ["Overwolf"])
    ];

    // Fixed local names only. No full process list, paths, IDs or titles enter the reporting projection.
    internal static ImmutableArray<SupplementarySoftware> ReadSoftware(Func<string, bool>? running = null,
        Func<TimeSpan>? elapsed = null)
    {
        var started = Stopwatch.GetTimestamp();
        running ??= IsRunning;
        elapsed ??= () => Stopwatch.GetElapsedTime(started);
        var results = ImmutableArray.CreateBuilder<SupplementarySoftware>(SoftwareFamilies.Length);
        foreach (var (family, names) in SoftwareFamilies)
        {
            var state = "not-observed";
            foreach (var name in names)
            {
                // Stops additional lookups after the budget. It does not claim to cancel an in-flight OS call.
                if (elapsed() >= TimeSpan.FromMilliseconds(500)) { state = "unknown"; break; }
                try { if (running(name)) { state = "present"; break; } }
                catch (Exception) { state = "unknown"; }
            }
            results.Add(new(family, state));
        }
        return results.MoveToImmutable();
    }

    private static bool IsRunning(string name)
    {
        var matches = Process.GetProcessesByName(name);
        try { return matches.Length > 0; }
        finally { foreach (var process in matches) process.Dispose(); }
    }

    internal static SupplementaryPower ReadPower()
    {
        var result = new SupplementaryPower("unknown", "unknown", "unknown");
        IntPtr scheme = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) == 0 && scheme != IntPtr.Zero)
                result = result with { Scheme = SchemeFamily(Marshal.PtrToStructure<Guid>(scheme)) };
        }
        catch (Exception) { }
        finally { if (scheme != IntPtr.Zero) _ = LocalFree(scheme); }
        try
        {
            if (GetSystemPowerStatus(out var status))
                result = result with
                {
                    Supply = status.AcLineStatus switch { 0 => "battery", 1 => "ac", _ => "unknown" },
                    BatterySaver = Environment.OSVersion.Version.Major >= 10
                        ? status.SystemStatusFlag switch { 0 => "disabled", 1 => "enabled", _ => "unknown" } : "unknown"
                };
        }
        catch (Exception) { }
        return result;
    }

    // These identify built-in plans, not their individual settings or effective processor performance.
    internal static string SchemeFamily(Guid scheme) => scheme.ToString("D") switch
    {
        "381b4222-f694-41f0-9685-ff5bb260df2e" => "balanced",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "high-performance",
        "a1841308-3541-4fab-bc81-f71556f20b4a" => "power-saver",
        _ => "custom"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
