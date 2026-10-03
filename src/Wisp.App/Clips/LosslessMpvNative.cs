using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Wisp.App.Clips;

// client.h API 2.5, x64. Calls and node ownership stay on the player's worker.
internal sealed class LosslessMpvNative
{
    private static readonly Lazy<IntPtr> Module = new(LoadModule);
    private readonly IntPtr _module;
    private IntPtr _handle;
    private readonly Call _initialize;
    private readonly Destroy _destroy;
    private readonly SetText _option, _property;
    private readonly GetInteger _getInteger;
    private readonly GetFlag _getFlag;
    private readonly GetNumber _getNumber;
    private readonly GetNode _getNode;
    private readonly Command _command;
    private readonly Wait _wait;
    private readonly LogRequest _logs;
    private readonly FreeNode _freeNode;
    private long _restarts;
    internal long Restarts => _restarts;
    internal LosslessPacketQueueDiagnostic? PacketQueueDiagnostic { get; private set; }

    internal LosslessMpvNative()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<Node>() != 16 || Marshal.SizeOf<Event>() != 24)
            throw new PlatformNotSupportedException("Lossless playback requires the x64 decoder.");
        _module = Module.Value;
        var version = Bind<Version>("mpv_client_api_version")();
        if ((version >> 16) != 2 || (version & 0xffff) < 5)
            throw new NotSupportedException("The lossless decoder API is incompatible.");
        _initialize = Bind<Call>("mpv_initialize"); _destroy = Bind<Destroy>("mpv_terminate_destroy");
        _option = Bind<SetText>("mpv_set_option_string"); _property = Bind<SetText>("mpv_set_property_string");
        _getInteger = Bind<GetInteger>("mpv_get_property"); _getFlag = Bind<GetFlag>("mpv_get_property");
        _getNumber = Bind<GetNumber>("mpv_get_property"); _getNode = Bind<GetNode>("mpv_get_property");
        _command = Bind<Command>("mpv_command_ret"); _wait = Bind<Wait>("mpv_wait_event");
        _logs = Bind<LogRequest>("mpv_request_log_messages"); _freeNode = Bind<FreeNode>("mpv_free_node_contents");
        _handle = Bind<Create>("mpv_create")();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("The lossless decoder could not be created.");
    }

    private static IntPtr LoadModule()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "libmpv", "win-x64", "libmpv-2.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("The lossless playback components are missing. Reinstall Wisp to restore them.");
        ClipLibrary.CheckPath(path);
        // App-local and replaceable; dependency search is limited to its directory and System32.
        const uint searchDllLoadDirectory = 0x00000100, searchSystem32 = 0x00000800;
        var module = LoadLibraryEx(path, IntPtr.Zero, searchDllLoadDirectory | searchSystem32);
        if (module == IntPtr.Zero) throw new InvalidOperationException("The lossless decoder could not be loaded.");
        return module; // Never unload native code while an outstanding owner may exist.
    }

    internal void Initialize(IntPtr window, bool hdrVideo = false)
    {
        if (window == IntPtr.Zero) throw new ArgumentException("A video surface is required.", nameof(window));
        foreach (var (name, value) in Options) Check(_option(_handle, name, value), "option-" + name);
        // With a Windows 10 manifest, auto selects PQ on an HDR desktop even
        // for SDR video. Keep SDR at Windows' SDR white; HDR follows the display.
        Check(_option(_handle, "d3d11-output-csp", hdrVideo ? "auto" : "srgb"), "option-d3d11-output-csp");
        Check(_option(_handle, "wid", window.ToInt64().ToString(CultureInfo.InvariantCulture)), "video-window");
        Check(_logs(_handle, "warn"), "request-logs");
        Check(_initialize(_handle), "initialize");
    }

    internal void InitializeHeadless(IEnumerable<(string Name, string Value)>? options = null)
    {
        foreach (var (name, value) in HeadlessOptions) Check(_option(_handle, name, value), "option-" + name);
        if (options is not null)
            foreach (var (name, value) in options) Check(_option(_handle, name, value), "option-" + name);
        Check(_logs(_handle, "warn"), "request-logs");
        Check(_initialize(_handle), "initialize");
    }

    private static readonly (string Name, string Value)[] HeadlessOptions =
    [
        ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"),
        ("input-terminal", "no"), ("input-default-bindings", "no"), ("input-vo-keyboard", "no"),
        ("osc", "no"), ("osd-level", "0"), ("access-references", "no"),
        ("sub-auto", "no"), ("audio-file-auto", "no"), ("vo", "null"), ("ao", "null"),
        ("vid", "no"), ("aid", "no"), ("sid", "no"), ("force-window", "no"),
        ("idle", "yes"), ("keep-open", "no"), ("hwdec", "no"), ("framedrop", "no"),
        ("cache", "no"), ("demuxer-max-bytes", "33554432"), ("demuxer-max-back-bytes", "0"),
        ("save-position-on-quit", "no"), ("resume-playback", "no"), ("stop-screensaver", "no")
    ];

    internal string[] ReadEncoderNames()
    {
        var node = default(Node);
        try
        {
            Check(_getNode(_handle, "encoder-list", 6, ref node), "encoder-list");
            if (node.Format != 7 || node.Pointer == IntPtr.Zero) throw new InvalidDataException("Missing encoder list.");
            var array = Marshal.PtrToStructure<NodeList>(node.Pointer);
            if (array.Count is < 0 or > 4096 || (array.Count != 0 && array.Values == IntPtr.Zero))
                throw new InvalidDataException("Invalid encoder list.");
            var names = new List<string>();
            for (var i = 0; i < array.Count; i++)
            {
                var entry = Marshal.PtrToStructure<Node>(array.Values + i * 16);
                if (entry.Format != 8 || entry.Pointer == IntPtr.Zero) throw new InvalidDataException("Invalid encoder entry.");
                var map = Marshal.PtrToStructure<NodeList>(entry.Pointer);
                if (map.Count is < 1 or > 16 || map.Values == IntPtr.Zero || map.Keys == IntPtr.Zero)
                    throw new InvalidDataException("Invalid encoder fields.");
                for (var field = 0; field < map.Count; field++)
                {
                    if (ReadText(Marshal.ReadIntPtr(map.Keys, field * 8), 64) != "driver") continue;
                    var value = Marshal.PtrToStructure<Node>(map.Values + field * 16);
                    if (value.Format != 1) throw new InvalidDataException("Invalid encoder name.");
                    var name = ReadText(value.Pointer, 128);
                    if (name is "h264_nvenc" or "hevc_nvenc" or "h264_mf" or "libopenh264" or "libx264" or "aac") names.Add(name);
                }
            }
            return names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        finally { _freeNode(ref node); }
    }

    internal static readonly (string Name, string Value)[] Options =
    [
        ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"),
        ("input-terminal", "no"), ("input-default-bindings", "no"), ("input-vo-keyboard", "no"),
        ("input-cursor", "no"), ("osc", "no"), ("osd-level", "0"), ("access-references", "no"),
        ("sub-auto", "no"), ("audio-file-auto", "no"), ("vo", "gpu"), ("gpu-api", "d3d11"),
        ("gpu-context", "d3d11"), ("ao", "wasapi"), ("hwdec", "no"), ("audio-display", "no"),
        ("pause", "yes"), ("mute", "yes"), ("volume", "0"), ("keep-open", "yes"), ("idle", "yes"),
        // Keep decoded references and presentation order intact. No decoder hurry-up path.
        ("framedrop", "no"), ("screenshot-sw", "yes"), ("cache", "yes"), ("cache-on-disk", "no"),
        ("cache-secs", "1"), ("cache-pause", "yes"), ("cache-pause-initial", "yes"), ("cache-pause-wait", "1"),
        ("demuxer-max-bytes", "268435456"), ("demuxer-max-back-bytes", "0"),
        ("demuxer-readahead-secs", "1"), ("vd-queue-enable", "no"),
        ("save-position-on-quit", "no"), ("resume-playback", "no"), ("stop-screensaver", "no")
    ];

    internal void Set(string name, string value) => Check(_property(_handle, name, value), "property-" + name);
    internal bool? Flag(string name) { var value = 0; return _getFlag(_handle, name, 3, ref value) >= 0 ? value != 0 : null; }
    internal long? Integer(string name) { long value = 0; return _getInteger(_handle, name, 4, ref value) >= 0 ? value : null; }
    internal double? Number(string name) { double value = 0; return _getNumber(_handle, name, 5, ref value) >= 0 && double.IsFinite(value) ? value : null; }
    internal string? Text(string name)
    {
        var node = default(Node);
        try { return _getNode(_handle, name, 6, ref node) >= 0 && node.Format == 1 ? ReadText(node.Pointer, 128) : null; }
        finally { _freeNode(ref node); }
    }
    internal void Run(params string[] args)
    {
        var result = Invoke(args);
        _freeNode(ref result);
    }
    private Node Invoke(string[] args)
    {
        var strings = new IntPtr[args.Length + 1]; var result = default(Node);
        var pointer = Marshal.AllocHGlobal(strings.Length * IntPtr.Size);
        try
        {
            for (var i = 0; i < args.Length; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            Marshal.Copy(strings, 0, pointer, strings.Length);
            Check(_command(_handle, pointer, ref result), "command-" + args[0]);
            return result;
        }
        catch { _freeNode(ref result); throw; }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            foreach (var item in strings) if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
        }
    }
    internal LosslessDecodedFrame ReadDiagnosticFrame()
    {
        if (Flag("pause") != true) throw new InvalidOperationException("Diagnostic samples require paused playback.");
        var node = Invoke(["screenshot-raw", "video", "bgr0"]);
        try
        {
            if (node.Format != 8 || node.Pointer == IntPtr.Zero) throw new InvalidDataException("Missing frame map.");
            var map = Marshal.PtrToStructure<NodeList>(node.Pointer);
            if (map.Count is < 1 or > 16 || map.Values == IntPtr.Zero || map.Keys == IntPtr.Zero) throw new InvalidDataException("Invalid frame map.");
            var fields = new Dictionary<string, Node>(StringComparer.Ordinal);
            for (var index = 0; index < map.Count; index++)
                if (!fields.TryAdd(ReadText(Marshal.ReadIntPtr(map.Keys, index * 8), 32), Marshal.PtrToStructure<Node>(map.Values + index * 16)))
                    throw new InvalidDataException("Duplicate frame field.");
            if (!fields.TryGetValue("w", out var width) || width.Format != 4 || width.Integer is < 1 or > 3840 ||
                !fields.TryGetValue("h", out var height) || height.Format != 4 || height.Integer is < 1 or > 2160 ||
                !fields.TryGetValue("stride", out var stride) || stride.Format != 4 || stride.Integer != width.Integer * 4 ||
                !fields.TryGetValue("format", out var format) || format.Format != 1 || ReadText(format.Pointer, 16) != "bgr0" ||
                !fields.TryGetValue("data", out var data) || data.Format != 9 || data.Pointer == IntPtr.Zero)
                throw new InvalidDataException("Unexpected frame layout.");
            var length = checked((int)(stride.Integer * height.Integer));
            var bytes = Marshal.PtrToStructure<ByteArray>(data.Pointer);
            if (bytes.Size != (nuint)length || bytes.Data == IntPtr.Zero) throw new InvalidDataException("Unexpected frame bytes.");
            var copy = GC.AllocateUninitializedArray<byte>(length); Marshal.Copy(bytes.Data, copy, 0, length);
            return new((int)width.Integer, (int)height.Integer, copy);
        }
        finally { _freeNode(ref node); }
    }
    internal void DrainEvents()
    {
        for (var count = 0; count < 128; count++)
        {
            var item = Marshal.PtrToStructure<Event>(_wait(_handle, 0));
            if (item.Id == 0) return;
            if (item.Id == 21) _restarts++;
            if (item.Id == 24) throw new LosslessMpvException("event-queue-overflow");
            if (item.Id == 7 && item.Data != IntPtr.Zero && Marshal.ReadInt32(item.Data) == 4)
                throw new LosslessMpvException("decode-failed");
            if (item.Id == 2 && item.Data != IntPtr.Zero)
            {
                var log = Marshal.PtrToStructure<Log>(item.Data);
                // Never put native messages (which can contain paths) in diagnostics.
                if (log.NumericLevel <= 20) throw new LosslessMpvException("decoder-error");
                var message = ReadText(log.Text, 4096);
                if (message.StartsWith("Too many packets in the demuxer packet queues:", StringComparison.Ordinal))
                {
                    try { PacketQueueDiagnostic = ReadPacketQueueDiagnostic(); }
                    catch (Exception error) when (error is InvalidDataException or RegexMatchTimeoutException) { }
                    throw new LosslessMpvException("packet-queue-full");
                }
            }
        }
        // A flood is a reported failure, not silently discarded decoder events.
        throw new LosslessMpvException("event-batch-limit");
    }

    private LosslessPacketQueueDiagnostic ReadPacketQueueDiagnostic()
    {
        var cache = new Dictionary<string, double>(StringComparer.Ordinal);
        var queues = new List<LosslessPacketQueueStreamDiagnostic>(3);
        // The player already failed. Read only the immediately queued bounded
        // numeric stream summaries, never native path-bearing message text.
        for (var count = 0; count < 4; count++)
        {
            var pending = Marshal.PtrToStructure<Event>(_wait(_handle, 0));
            if (pending.Id == 0) break;
            if (pending.Id != 2 || pending.Data == IntPtr.Zero) continue;
            var summary = Marshal.PtrToStructure<Log>(pending.Data);
            var match = Regex.Match(ReadText(summary.Text, 4096),
                @"^\s*(video|audio|sub)/(\d{1,2}): (\d{1,10}) packets, (\d{1,19}) bytes(?: \((?:lazy|refreshing)\))*\s*$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            if (match.Success && int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var stream) &&
                long.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var packets) &&
                long.TryParse(match.Groups[4].Value, CultureInfo.InvariantCulture, out var bytes))
                queues.Add(new(match.Groups[1].Value, stream, packets, bytes));
        }
        var node = default(Node);
        try
        {
            if (_getNode(_handle, "demuxer-cache-state", 6, ref node) >= 0) ReadMap(node, "", true);
        }
        catch (InvalidDataException) { cache.Clear(); }
        finally { _freeNode(ref node); }
        return new(Number("time-pos"), Number("audio-pts"), Number("avsync"),
            Integer("frame-drop-count"), Integer("decoder-frame-drop-count"), cache, queues,
            Flag("paused-for-cache"), Number("cache-buffering-state"));

        void ReadMap(Node value, string prefix, bool allowStreams)
        {
            if (value.Format != 8 || value.Pointer == IntPtr.Zero) return;
            var map = Marshal.PtrToStructure<NodeList>(value.Pointer);
            if (map.Count is < 0 or > 32 || map.Count > 0 && (map.Values == IntPtr.Zero || map.Keys == IntPtr.Zero)) return;
            for (var field = 0; field < map.Count; field++)
            {
                var key = ReadText(Marshal.ReadIntPtr(map.Keys, field * 8), 64);
                var item = Marshal.PtrToStructure<Node>(map.Values + field * 16);
                if (key == "ts-per-stream" && allowStreams && item.Format == 7 && item.Pointer != IntPtr.Zero)
                {
                    var streams = Marshal.PtrToStructure<NodeList>(item.Pointer);
                    if (streams.Count is < 0 or > 3 || streams.Count > 0 && streams.Values == IntPtr.Zero) continue;
                    for (var stream = 0; stream < streams.Count; stream++)
                        ReadMap(Marshal.PtrToStructure<Node>(streams.Values + stream * 16),
                            (stream == 0 ? "video/" : stream == 1 ? "audio/" : "subtitle/"), false);
                }
                else if (key is "fw-bytes" or "cache-end" or "reader-pts" or "cache-duration" or
                    "raw-input-rate" or "total-bytes" or "eof" or "underrun" or "idle")
                {
                    var number = item.Format == 5 ? item.Number : item.Format == 3 ? item.Flag : item.Format == 4 ? item.Integer : double.NaN;
                    if (double.IsFinite(number)) cache[prefix + key] = number;
                }
            }
        }
    }

    internal (bool Ended, bool Shutdown) ReadExportEvents()
    {
        var ended = false; var shutdown = false;
        for (var count = 0; count < 128; count++)
        {
            var item = Marshal.PtrToStructure<Event>(_wait(_handle, 0));
            if (item.Id == 0) return (ended, shutdown);
            if (item.Id == 1) shutdown = true;
            if (item.Id == 24) throw new LosslessMpvException("export-event-overflow");
            if (item.Id == 7)
            {
                if (item.Data == IntPtr.Zero || Marshal.ReadInt32(item.Data) != 0 || Marshal.ReadInt32(item.Data, 4) < 0)
                    throw new LosslessMpvException("export-file-ended-with-error");
                ended = true;
            }
            if (item.Id == 2 && item.Data != IntPtr.Zero)
            {
                var log = Marshal.PtrToStructure<Log>(item.Data);
                if (log.NumericLevel <= 20) throw new LosslessMpvException("export-native-error");
                if (ReadText(log.Text, 4096).StartsWith("Too many packets in the demuxer packet queues:", StringComparison.Ordinal))
                    throw new LosslessMpvException("packet-queue-full");
            }
        }
        throw new LosslessMpvException("export-event-batch-limit");
    }
    internal void Close()
    {
        if (_handle == IntPtr.Zero) return;
        _destroy(_handle); _handle = IntPtr.Zero;
    }
    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));
    private static string ReadText(IntPtr pointer, int maximum)
    {
        if (pointer == IntPtr.Zero) throw new InvalidDataException("The decoder returned missing text.");
        var length = 0;
        while (length < maximum && Marshal.ReadByte(pointer, length) != 0) length++;
        if (length == maximum) throw new InvalidDataException("The decoder returned oversized text.");
        var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return Encoding.UTF8.GetString(bytes);
    }
    private static void Check(int code, string action) { if (code < 0) throw new LosslessMpvException(action + "/" + code.ToString(CultureInfo.InvariantCulture)); }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Node
    { [FieldOffset(0)] internal IntPtr Pointer; [FieldOffset(0)] internal long Integer; [FieldOffset(0)] internal int Flag; [FieldOffset(0)] internal double Number; [FieldOffset(8)] internal int Format; }
    [StructLayout(LayoutKind.Sequential)] private struct NodeList { internal int Count; internal IntPtr Values, Keys; }
    [StructLayout(LayoutKind.Sequential)] private struct ByteArray { internal IntPtr Data; internal nuint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct Event { internal int Id, Error; internal ulong Reply; internal IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Log { internal IntPtr Prefix, Level, Text; internal int NumericLevel; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Call(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetText(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFlag(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetInteger(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref long value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetNumber(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetNode(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref Node value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Command(IntPtr handle, IntPtr args, ref Node result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Wait(IntPtr handle, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LogRequest(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeNode(ref Node value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
}

internal sealed class LosslessMpvException(string code) : Exception("The lossless decoder operation failed.")
{
    internal string Code { get; } = code;
}

internal sealed record LosslessDecodedFrame(int Width, int Height, byte[] Bgra);
internal sealed record LosslessPacketQueueDiagnostic(double? Position, double? AudioPosition, double? AudioVideoDifference,
    long? FrameDrops, long? DecoderFrameDrops, IReadOnlyDictionary<string, double> Cache,
    IReadOnlyList<LosslessPacketQueueStreamDiagnostic> Queues, bool? PausedForCache, double? CacheBufferingState);
internal sealed record LosslessPacketQueueStreamDiagnostic(string Stream, int StreamIndex, long Packets, long Bytes);
