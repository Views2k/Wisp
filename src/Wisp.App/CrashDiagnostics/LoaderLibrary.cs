using System.Reflection;
using System.Runtime.InteropServices;

namespace Wisp.App.CrashDiagnostics;

internal static class LoaderLibrary
{
    // Compiled imports and explicitly loaded playback libraries. Never retain arbitrary
    // loader messages, entry-point names, filenames or search paths.
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwmapi.dll", "gdi32.dll", "kernel32.dll", "kernelbase.dll", "libmpv-2.dll", "libvlc.dll",
        "libvlccore.dll", "ntdll.dll", "powrprof.dll", "rstrtmgr.dll", "shell32.dll", "user32.dll",
        "winsqlite3.dll", "wintrust.dll", "wisp.nativerenderer.dll", "wtsapi32.dll", "xinput1_4.dll", "xinput9_1_0.dll"
    };

    internal static bool Valid(string type, string? name) =>
        type is nameof(DllNotFoundException) or nameof(EntryPointNotFoundException) &&
        name is { Length: <= 64 } && Names.TryGetValue(name, out var canonical) && canonical == name;

    internal static string? FromException(Exception error)
    {
        var type = error.GetType();
        if (type != typeof(DllNotFoundException) && type != typeof(EntryPointNotFoundException)) return null;
        if (error.TargetSite?.GetCustomAttribute<DllImportAttribute>() is { } import &&
            Names.TryGetValue(import.Value, out var library)) return library;

        // The CLR quotes the requested library. For a missing entry point the procedure
        // is quoted first. Only consider the library position, never a dependency search line.
        var message = error.Message.AsSpan();
        var text = message[..Math.Min(1024, message.Length)];
        var argument = ReadOnlySpan<char>.Empty;
        for (var i = 0; i < (type == typeof(EntryPointNotFoundException) ? 2 : 1); i++)
        {
            var start = text.IndexOf('\'');
            if (start < 0) return null;
            text = text[(start + 1)..];
            var end = text.IndexOf('\'');
            if (end < 0) return null;
            argument = text[..end];
            text = text[(end + 1)..];
        }
        var name = argument[(argument.LastIndexOfAny('\\', '/') + 1)..];
        return name.Length is > 0 and <= 64 && Names.TryGetValue(name.ToString(), out library) ? library : null;
    }
}
