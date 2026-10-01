using System.Runtime.InteropServices;
using System.Text;
using static Wisp.MpvOfflineDecodeProbe.ProbeFiles;

namespace Wisp.MpvOfflineDecodeProbe;

// x64 declarations from the pinned archive client.h (API 2.5); no managed package.
internal sealed unsafe class MpvNative
{
    private readonly IntPtr _module;
    internal IntPtr Handle { get; private set; }
    private readonly Create _create;
    private readonly Call _initialize;
    private readonly Destroy _destroy;
    private readonly SetText _option, _property;
    private readonly Get _get;
    private readonly Command _command;
    private readonly Wait _wait;
    private readonly LogRequest _logs;
    private readonly FreeNode _freeNode;
    internal uint ApiVersion { get; }

    internal MpvNative(string dll)
    {
        Need(IntPtr.Size == 8 && Marshal.SizeOf<Node>() == 16 && Marshal.SizeOf<Event>() == 24 && Marshal.SizeOf<NodeList>() == 24, "x64-abi-required");
        const uint searchDllLoadDirectory = 0x00000100, searchSystem32 = 0x00000800;
        _module = LoadLibraryEx(dll, IntPtr.Zero, searchDllLoadDirectory | searchSystem32);
        Need(_module != IntPtr.Zero, "runtime-load-failed");
        _create = Bind<Create>("mpv_create"); _initialize = Bind<Call>("mpv_initialize");
        _destroy = Bind<Destroy>("mpv_terminate_destroy"); _option = Bind<SetText>("mpv_set_option_string");
        _property = Bind<SetText>("mpv_set_property_string"); _get = Bind<Get>("mpv_get_property");
        _command = Bind<Command>("mpv_command_ret"); _wait = Bind<Wait>("mpv_wait_event");
        _logs = Bind<LogRequest>("mpv_request_log_messages"); _freeNode = Bind<FreeNode>("mpv_free_node_contents");
        ApiVersion = Bind<Version>("mpv_client_api_version")();
        Need(ApiVersion == 0x00020005, "pinned-api-version-mismatch");
        Handle = _create(); Need(Handle != IntPtr.Zero, "runtime-create-failed");
    }
    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));
    internal void Option(string name, string value) => Check(_option(Handle, name, value), "option-" + name);
    internal void Set(string name, string value) => Check(_property(Handle, name, value), "property-" + name);
    internal void Initialize() { Check(_logs(Handle, "warn"), "request-logs"); Check(_initialize(Handle), "initialize"); }
    internal bool? Flag(string name) { int value = 0; return _get(Handle, name, 3, (IntPtr)(&value)) >= 0 ? value != 0 : null; }
    internal long? Integer(string name) { long value = 0; return _get(Handle, name, 4, (IntPtr)(&value)) >= 0 ? value : null; }
    internal double? Number(string name) { double value = 0; return _get(Handle, name, 5, (IntPtr)(&value)) >= 0 && double.IsFinite(value) ? value : null; }
    internal string? Text(string name)
    {
        var node = default(Node);
        try { return _get(Handle, name, 6, (IntPtr)(&node)) >= 0 && node.Format == 1 ? ReadText(node.Pointer, 128) : null; }
        finally { _freeNode(ref node); }
    }
    internal Dictionary<string, double> CacheNumbers()
    {
        var node = default(Node); var result = new Dictionary<string, double>();
        try
        {
            if (_get(Handle, "demuxer-cache-state", 6, (IntPtr)(&node)) < 0) return result;
            foreach (var (name, item) in Map(node))
                if (name is "fw-bytes" or "total-bytes" or "raw-input-rate" or "reader-pts" or "cache-end" or "cache-duration")
                {
                    var value = item.Format == 4 ? item.Int64 : item.Format == 5 ? item.Number : double.NaN;
                    if (double.IsFinite(value)) result[name] = value;
                }
            return result;
        }
        finally { _freeNode(ref node); }
    }
    internal void Run(params string[] args)
    {
        var node = Invoke(args);
        _freeNode(ref node);
    }
    internal byte[] RawFrame()
    {
        var node = Invoke(["screenshot-raw", "video", "bgr0"]);
        try
        {
            var fields = Map(node);
            Need(fields.TryGetValue("w", out var width) && width.Format == 4 && width.Int64 == 3840 &&
                fields.TryGetValue("h", out var height) && height.Format == 4 && height.Int64 == 2160 &&
                fields.TryGetValue("stride", out var stride) && stride.Format == 4 && stride.Int64 == 15360 &&
                fields.TryGetValue("format", out var format) && format.Format == 1 && ReadText(format.Pointer, 16) == "bgr0" &&
                fields.TryGetValue("data", out var data) && data.Format == 9 && data.Pointer != IntPtr.Zero, "screenshot-layout-mismatch");
            var bytes = Marshal.PtrToStructure<ByteArray>(fields["data"].Pointer);
            Need(bytes.Size == 33177600 && bytes.Data != IntPtr.Zero, "screenshot-size-mismatch");
            var copy = GC.AllocateUninitializedArray<byte>(33177600);
            new ReadOnlySpan<byte>((void*)bytes.Data, copy.Length).CopyTo(copy);
            return copy;
        }
        finally { _freeNode(ref node); }
    }
    private Node Invoke(string[] args)
    {
        var strings = new IntPtr[args.Length + 1]; var result = default(Node);
        try
        {
            for (var i = 0; i < args.Length; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (IntPtr* pointer = strings)
            {
                var code = _command(Handle, (IntPtr)pointer, ref result);
                if (code < 0) { _freeNode(ref result); throw new ProbeFailure("command-" + args[0] + "-" + code); }
            }
            return result;
        }
        finally { foreach (var value in strings) if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
    }
    internal Event Next(double timeout) => Marshal.PtrToStructure<Event>(_wait(Handle, timeout));
    internal void Close()
    {
        if (Handle != IntPtr.Zero) { _destroy(Handle); Handle = IntPtr.Zero; }
        // Keep the DLL loaded until process exit; a failed teardown must never unload active code.
    }
    internal static Dictionary<string, Node> Map(Node value)
    {
        Need(value.Format == 8 && value.Pointer != IntPtr.Zero, "node-map-required");
        var map = Marshal.PtrToStructure<NodeList>(value.Pointer);
        Need(map.Count is >= 0 and <= 64 && (map.Count == 0 || (map.Values != IntPtr.Zero && map.Keys != IntPtr.Zero)), "node-map-bound");
        var fields = new Dictionary<string, Node>(StringComparer.Ordinal);
        for (var i = 0; i < map.Count; i++)
        {
            var key = ReadText(Marshal.ReadIntPtr(map.Keys, i * 8), 128);
            Need(fields.TryAdd(key, Marshal.PtrToStructure<Node>(map.Values + i * 16)), "node-duplicate-key");
        }
        return fields;
    }
    internal static string ReadText(IntPtr pointer, int maximum)
    {
        Need(pointer != IntPtr.Zero, "null-native-text");
        var bytes = (byte*)pointer; var count = 0;
        while (count < maximum && bytes[count] != 0) count++;
        Need(count < maximum, "native-text-bound"); return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes, count));
    }
    private static void Check(int code, string action) { if (code < 0) throw new ProbeFailure(action + "-" + code); }
    [StructLayout(LayoutKind.Explicit, Size = 16)] internal struct Node
    {
        [FieldOffset(0)] internal IntPtr Pointer;
        [FieldOffset(0)] internal long Int64;
        [FieldOffset(0)] internal double Number;
        [FieldOffset(8)] internal int Format;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NodeList { internal int Count; internal IntPtr Values, Keys; }
    [StructLayout(LayoutKind.Sequential)] private struct ByteArray { internal IntPtr Data; internal nuint Size; }
    [StructLayout(LayoutKind.Sequential)] internal struct Event { internal int Id, Error; internal ulong Reply; internal IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct Log { internal IntPtr Prefix, Level, Text; internal int NumericLevel; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Call(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetText(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Get(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Command(IntPtr handle, IntPtr args, ref Node result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Wait(IntPtr handle, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LogRequest(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeNode(ref Node value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
}
