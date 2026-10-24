using System.Diagnostics;
using Wisp.App.Supplementary;

namespace Wisp.App;

// RivaTuner Statistics Server injects its overlay hooks into every Direct3D process it detects.
// Inside Wisp they run on the dashboard's drawing thread and have corrupted its heap. While the
// hook library is loaded, the dashboard asks the player to exclude Wisp in RivaTuner.
internal static class OverlayHookNotice
{
    internal const string HookModule = "RTSSHooks64.dll";
    internal static readonly TimeSpan[] Checks = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];
    internal static readonly SupplementaryContentItem Notice = LocalDashboardNotices.Create("rtss-hooks",
        "RivaTuner Statistics Server is hooking Wisp",
        "RivaTuner's overlay hooks are loaded inside Wisp and have crashed it on some PCs. In RivaTuner " +
        "Statistics Server, add Wisp.exe and set its Application detection level to None, then restart Wisp.");

    internal static bool IsHookLoaded(IEnumerable<string> moduleNames) =>
        moduleNames.Any(name => string.Equals(name, HookModule, StringComparison.OrdinalIgnoreCase));

    internal static bool IsHookLoaded()
    {
        try { return IsHookLoaded(LoadedModuleNames()); }
        catch (Exception) { return false; }
    }

    private static List<string> LoadedModuleNames()
    {
        using var process = Process.GetCurrentProcess();
        var names = new List<string>();
        foreach (ProcessModule module in process.Modules)
        {
            using (module) names.Add(module.ModuleName);
        }
        return names;
    }

    internal static async Task WatchAsync(LocalDashboardNotices notices, CancellationToken cancellationToken)
    {
        foreach (var delay in Checks)
        {
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (await Task.Run(IsHookLoaded, cancellationToken).ConfigureAwait(false))
            {
                notices.Raise(Notice);
                return;
            }
        }
    }
}
