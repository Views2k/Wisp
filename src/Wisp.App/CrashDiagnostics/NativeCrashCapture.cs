using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Wisp.App.CrashDiagnostics;

// What the native renderer's vectored exception handler wrote when a hardware fault hit the
// process: the faulting address and a call stack, each as module+offset. Windows' own event
// only keeps the faulting module and offset; this is what locates the fault.
internal sealed record NativeFaultRecord(uint ExceptionCode, string Module, ulong Offset, string? Access,
    ulong? AccessAddress, uint ThreadId, string[] Frames)
{
    internal const int MaximumFrames = 48;
    internal const int MaximumRecordBytes = 64 * 1024;

    internal NativeFaultRecord? Sanitize()
    {
        var module = WindowsFaultEventReader.SafeModule(Module);
        if (module is null || Frames is null) return null;
        if (Access is not (null or "read" or "write" or "execute")) return null;
        var frames = Frames.Where(ValidFrame).Take(MaximumFrames).ToArray();
        return this with { Module = module, Frames = frames };
    }

    internal static bool ValidFrame(string? frame)
    {
        if (frame is not { Length: <= 96 }) return false;
        var plus = frame.IndexOf("+0x", StringComparison.Ordinal);
        if (plus <= 0) return false;
        var module = frame[..plus];
        var offset = frame[(plus + 3)..];
        return WindowsFaultEventReader.SafeModule(module) == module && offset.Length is >= 1 and <= 16 &&
            offset.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    // Parses the last complete record in the file. A record is a block of key=value lines
    // between "wisp-native-fault 1" and "end".
    internal static NativeFaultRecord? Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var blocks = text.Split("wisp-native-fault 1\n", StringSplitOptions.RemoveEmptyEntries);
        for (var index = blocks.Length - 1; index >= 0; index--)
        {
            var block = blocks[index];
            var end = block.IndexOf("\nend\n", StringComparison.Ordinal);
            if (end < 0) continue;
            uint code = 0, thread = 0;
            string? module = null, access = null;
            ulong offset = 0, accessAddress = 0;
            var hasAccessAddress = false;
            var frames = new List<string>();
            foreach (var line in block[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator];
                var value = line[(separator + 1)..].Trim();
                switch (key)
                {
                    case "code": if (!Hex(value, out var parsedCode) || parsedCode > uint.MaxValue) return null; code = (uint)parsedCode; break;
                    case "thread": if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out thread)) return null; break;
                    case "address":
                        if (!SplitAddress(value, out module, out offset)) return null;
                        break;
                    case "access":
                        var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 2 || !Hex(parts[1], out accessAddress)) return null;
                        access = parts[0];
                        hasAccessAddress = true;
                        break;
                    case "frame":
                        if (frames.Count < MaximumFrames && ValidFrame(value)) frames.Add(value);
                        break;
                }
            }
            if (module is null) return null;
            return new NativeFaultRecord(code, module, offset, access, hasAccessAddress ? accessAddress : null, thread, frames.ToArray()).Sanitize();
        }
        return null;
    }

    private static bool SplitAddress(string value, out string? module, out ulong offset)
    {
        module = null;
        offset = 0;
        var plus = value.IndexOf("+0x", StringComparison.Ordinal);
        if (plus <= 0 || !Hex(value[(plus + 3)..], out offset)) return false;
        module = WindowsFaultEventReader.SafeModule(value[..plus]);
        return module is not null;
    }

    private static bool Hex(string value, out ulong number)
    {
        number = 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return value is { Length: > 0 and <= 16 } && value.All(char.IsAsciiHexDigit) &&
            ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number);
    }
}

internal static class NativeCrashCapture
{
    private static class Native
    {
        [DllImport("Wisp.NativeRenderer.dll", EntryPoint = "WispCrashCaptureInstall", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int Install(string path);
    }

    internal static string PathFor(string directory, Guid runId) =>
        Path.Combine(directory, "native-" + runId.ToString("N", CultureInfo.InvariantCulture) + ".txt");

    // Installs the handler for this run. Failure only means a future fault stays unlocated.
    internal static bool TryInstall(string directory, Guid runId)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return Native.Install(PathFor(directory, runId)) >= 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // Reads and removes the previous run's record. Absent or unreadable records are null.
    internal static NativeFaultRecord? TryRead(string directory, Guid runId)
    {
        var path = PathFor(directory, runId);
        try
        {
            if (!File.Exists(path)) return null;
            NativeFaultRecord? record = null;
            var info = new FileInfo(path);
            if (info.Length is > 0 and <= NativeFaultRecord.MaximumRecordBytes) record = NativeFaultRecord.Parse(File.ReadAllText(path));
            try { File.Delete(path); } catch (Exception) { }
            return record;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
