using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Wisp.App.CrashDiagnostics;

// A sharing or lock violation on one of Wisp's own files means another process holds it. The
// Restart Manager names that process; the local report keeps the name so a scanner or a backup
// tool can be told apart from a bug in Wisp.
internal static class FileLockHolders
{
    internal const int SharingViolation = unchecked((int)0x80070020);
    internal const int LockViolation = unchecked((int)0x80070021);
    internal const int MaximumHolders = 4;
    private const int ErrorMoreData = 234;
    private const int SessionKeyLength = 32;

    internal static bool IsLockFailure(Exception? error) =>
        error is IOException { HResult: SharingViolation or LockViolation };

    // Names of the other processes holding Wisp's program files open; empty when unknown.
    internal static string[] DescribeProgramFiles()
    {
        var names = new List<string>();
        if (Environment.ProcessPath is not { } executable) return [];
        foreach (var path in new[] { executable, Path.ChangeExtension(executable, ".dll") })
        {
            if (!File.Exists(path)) continue;
            foreach (var name in Describe(path))
            {
                if (!names.Contains(name)) names.Add(name);
                if (names.Count == MaximumHolders) return names.ToArray();
            }
        }
        return names.ToArray();
    }

    internal static string[] Describe(string path)
    {
        try { return Query(path); }
        catch (Exception) { return []; }
    }

    internal static string Note(string[] holders) =>
        holders.Length == 0 ? "File held by: unknown" : "File held by: " + string.Join(", ", holders);

    internal static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var trimmed = name.Trim();
        if (trimmed.Length > 32) trimmed = trimmed[..32];
        return trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ' ')
            ? trimmed : "unknown";
    }

    private static string[] Query(string path)
    {
        var key = new StringBuilder(SessionKeyLength + 1);
        if (RmStartSession(out var session, 0, key) != 0) return [];
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0) return [];
            uint count = 0, reasons = 0;
            var result = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (result != ErrorMoreData && result != 0 || needed == 0) return [];
            var entries = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, entries, ref reasons) != 0) return [];
            var current = Environment.ProcessId;
            var names = new List<string>();
            foreach (var entry in entries.Take((int)Math.Min(count, entries.Length)))
            {
                if (entry.Process.ProcessId == current) continue;
                string? name;
                try
                {
                    using var process = Process.GetProcessById(entry.Process.ProcessId);
                    name = process.ProcessName;
                }
                catch (Exception) { name = entry.AppName; }
                var safe = SafeName(name);
                if (!names.Contains(safe)) names.Add(safe);
                if (names.Count == MaximumHolders) break;
            }
            return names.ToArray();
        }
        finally { RmEndSession(session); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] files,
        uint applicationCount, RmUniqueProcess[]? applications, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint neededCount, ref uint count,
        [In, Out] RmProcessInfo[]? processInfo, ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}
