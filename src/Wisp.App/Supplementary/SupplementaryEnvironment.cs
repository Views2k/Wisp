using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wisp.Core;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryAdapter(uint VendorId, uint DeviceId, string Model, uint DedicatedMemoryMiB,
    bool Software, string? DriverVersion = null);
internal sealed record SupplementaryDisplay(int AdapterIndex, uint Width, uint Height, string Hdr,
    uint? RefreshMilliHz = null, uint? ScalePercent = null, uint? BitsPerColor = null);
internal sealed record SupplementarySettings(string RenderMode, string Units, string GaugeStyle, bool ClipsEnabled)
{
    public double? Smoothing { get; init; }
    public double? OverlayOpacity { get; init; }
    public double? OverlayWidthScale { get; init; }
    public double? OverlayHeightScale { get; init; }
    public bool? GameAwareVisibility { get; init; }
    public bool? HudGpuPriority { get; init; }
    public bool? GForceEnabled { get; init; }
    public bool? BoostGaugeEnabled { get; init; }
    public bool? TireTemperatureGaugeEnabled { get; init; }
    public bool? PowerGaugeEnabled { get; init; }
    public bool? TorqueGaugeEnabled { get; init; }
    public bool? LapMapEnabled { get; init; }
    public bool? LapDeltaEnabled { get; init; }
    public bool? LapReviewRecordingEnabled { get; init; }
    public bool? DebugLoggingEnabled { get; init; }
    public bool? ClipsLossless { get; init; }
    public bool? ClipsPreserveHdr { get; init; }
    public bool? ClipsSystemAudio { get; init; }
    public int? ClipsResolutionHeight { get; init; }
    public int? ClipsFrameRate { get; init; }
    public int? ClipsQuality { get; init; }
    public int? ClipsLengthSeconds { get; init; }

    internal bool IsValid => RenderMode is "cpu" or "gpu" && Units is "mph" or "kmh" &&
        GaugeStyle is "minimal" or "combined" or "separate-boxes" or "native-digital" or "native-analogue" or "unknown" &&
        OptionalNumber(Smoothing, 0, 1) && OptionalNumber(OverlayOpacity, 0, 1) &&
        OptionalNumber(OverlayWidthScale, 0.1, 10) && OptionalNumber(OverlayHeightScale, 0.1, 10) &&
        ClipsResolutionHeight is null or 360 or 480 or 720 or 1080 or 1440 or 2160 &&
        ClipsFrameRate is null or 30 or 60 && ClipsQuality is null or >= 10 and <= 100 &&
        (ClipsLengthSeconds is null || ClipsLengthSeconds is >= 30 and <= 300 && ClipsLengthSeconds % 30 == 0);
    private static bool OptionalNumber(double? value, double min, double max) => value is null || double.IsFinite(value.Value) && value >= min && value <= max;
    private static double? Number(double value, double min, double max) => OptionalNumber(value, min, max) ? value : null;
    internal static SupplementarySettings Capture(AppSettings settings) => new(settings.CpuRenderingEnabled ? "cpu" : "gpu",
        settings.SpeedUnit == SpeedUnit.KilometersPerHour ? "kmh" : "mph", settings.LayoutMode switch
        {
            HudLayoutMode.Minimal => "minimal",
            HudLayoutMode.Combined => "combined",
            HudLayoutMode.SeparateBoxes => "separate-boxes",
            HudLayoutMode.Native when settings.NativeGaugeMode == NativeGaugeMode.Analogue => "native-analogue",
            HudLayoutMode.Native when settings.NativeGaugeMode == NativeGaugeMode.Digital => "native-digital",
            _ => "unknown"
        }, settings.Clips?.Enabled == true)
    {
        Smoothing = Number(settings.Smoothing, 0, 1),
        OverlayOpacity = Number(settings.OverlayOpacity, 0, 1),
        OverlayWidthScale = Number(settings.OverlayWidthScale, 0.1, 10),
        OverlayHeightScale = Number(settings.OverlayHeightScale, 0.1, 10),
        GameAwareVisibility = settings.GameAwareVisibility,
        HudGpuPriority = settings.HudGpuPriority,
        GForceEnabled = settings.GForceEnabled,
        BoostGaugeEnabled = settings.BoostGaugeEnabled,
        TireTemperatureGaugeEnabled = settings.TireTemperatureGaugeEnabled,
        PowerGaugeEnabled = settings.PowerGaugeEnabled,
        TorqueGaugeEnabled = settings.TorqueGaugeEnabled,
        LapMapEnabled = settings.LapMapEnabled,
        LapDeltaEnabled = settings.LapDeltaEnabled,
        LapReviewRecordingEnabled = settings.LapReviewRecordingEnabled,
        DebugLoggingEnabled = settings.DebugLoggingEnabled,
        ClipsLossless = settings.Clips?.LosslessVideo,
        ClipsPreserveHdr = settings.Clips?.PreserveHdrRecording,
        ClipsSystemAudio = settings.Clips?.CaptureSystemAudio,
        ClipsResolutionHeight = settings.Clips?.ResolutionHeight is 360 or 480 or 720 or 1080 or 1440 or 2160 ? settings.Clips.ResolutionHeight : null,
        ClipsFrameRate = settings.Clips?.FrameRate is 30 or 60 ? settings.Clips.FrameRate : null,
        ClipsQuality = settings.Clips?.Quality is >= 10 and <= 100 ? settings.Clips.Quality : null,
        ClipsLengthSeconds = settings.Clips?.LengthSeconds is >= 30 and <= 300 && settings.Clips.LengthSeconds % 30 == 0 ? settings.Clips.LengthSeconds : null
    };
}
internal sealed record SupplementaryCoverage(string DisplayedFrames, string GameRenderAdapter, string Vrr, string Foreground);
internal sealed record SupplementaryEnvironment(int SchemaVersion, DateTimeOffset CollectedAt, int OsBuild, int LogicalProcessors,
    string GraphicsStatus, string GraphicsReason, ImmutableArray<SupplementaryAdapter> Adapters,
    ImmutableArray<SupplementaryDisplay> Displays, SupplementarySettings Settings, SupplementaryCoverage Coverage, int? MemoryGiB = null)
{
    public SupplementaryPower? Power { get; init; }
    public ImmutableArray<SupplementarySoftware>? Software { get; init; }
    internal bool IsValid(DateTimeOffset observedAt) => SchemaVersion == 1 && CollectedAt.Offset == TimeSpan.Zero &&
        CollectedAt <= observedAt + TimeSpan.FromMinutes(5) && CollectedAt >= observedAt - TimeSpan.FromMinutes(10) &&
        OsBuild is >= 1 and <= 100000 && LogicalProcessors is >= 1 and <= 1024 && MemoryGiB is null or >= 1 and <= 4096 &&
        GraphicsStatus is "available" or "partial" or "unavailable" &&
        GraphicsReason is "none" or "api-unavailable" or "collector-failed" or "partial-adapters" or "partial-displays" &&
        !Adapters.IsDefault && Adapters.Length <= 4 && Adapters.All(a => a is not null && a.VendorId <= 65535 && a.DeviceId <= 65535 &&
            ValidModel(a.Model) && a.DedicatedMemoryMiB <= 1048576 && (a.DriverVersion is null || ValidDriver(a.DriverVersion))) &&
        !Displays.IsDefault && Displays.Length <= 8 && Displays.All(d => d is not null && d.AdapterIndex >= 0 && d.AdapterIndex < Adapters.Length &&
            d.Width is >= 1 and <= 32768 && d.Height is >= 1 and <= 32768 && d.Hdr is "enabled" or "disabled" or "unknown" &&
            d.RefreshMilliHz is null or >= 1000 and <= 1000000 && d.ScalePercent is null or >= 100 and <= 800 && d.BitsPerColor is null or >= 1 and <= 32) &&
        Settings is not null && Settings.IsValid &&
        Coverage is not null && Coverage.DisplayedFrames == "not-measured" && Coverage.GameRenderAdapter == "not-measured" &&
        Coverage.Vrr == "not-measured" && Coverage.Foreground is "available" or "unavailable" &&
        (Power is null || Power.IsValid) &&
        (Software is null || !Software.Value.IsDefault && Software.Value.Length <= 6 &&
            Software.Value.All(s => s is not null && s.IsValid) && Software.Value.Select(s => s.Family).Distinct(StringComparer.Ordinal).Count() == Software.Value.Length) &&
        JsonSerializer.SerializeToUtf8Bytes(this, SupplementarySchema.Json).Length <= 8192;

    internal static bool ValidModel(string? value) => value is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(value) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '(' or ')' or '.' or '_' or '+' or '-');
    internal static bool ValidDriver(string value) => value.Split('.') is { Length: 4 } parts &&
        parts.All(p => p.Length is > 0 and <= 5 && p.All(char.IsAsciiDigit) && ushort.TryParse(p, out _));
}

// One cached background query per five minutes. The render, telemetry and UI paths never call DXGI inventory.
internal sealed class SupplementaryEnvironmentCollector
{
    private readonly Func<DateTimeOffset, SupplementarySettings, SupplementaryEnvironment> _read;
    private readonly TimeProvider _clock;
    private SupplementaryEnvironment? _cached;
    private long _capturedAt;
    private readonly ForzaFocusService _focus = new();
    internal SupplementaryEnvironmentCollector(Func<DateTimeOffset, SupplementarySettings, SupplementaryEnvironment>? read = null, TimeProvider? clock = null)
    { _read = read ?? Read; _clock = clock ?? TimeProvider.System; }

    internal SupplementaryEnvironment Snapshot(SupplementarySettings settings)
    {
        var timestamp = _clock.GetTimestamp();
        var now = _clock.GetUtcNow();
        if (_cached is null || _clock.GetElapsedTime(_capturedAt, timestamp) >= TimeSpan.FromMinutes(5) ||
            _cached.CollectedAt > now + TimeSpan.FromMinutes(5) || _cached.CollectedAt < now - TimeSpan.FromMinutes(10))
        {
            _cached = _read(now, settings);
            _capturedAt = timestamp;
        }
        return _cached with { Settings = settings };
    }

    internal string Foreground()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var id) == 0) return "unknown";
            if (id == Environment.ProcessId) return "wisp";
            return _focus.GetState(_clock.GetUtcNow()).IsForzaForeground ? "game" : "other";
        }
        catch (Exception) { return "unknown"; }
    }

    private static SupplementaryEnvironment Read(DateTimeOffset now, SupplementarySettings settings)
    {
        var result = new SupplementaryEnvironment(1, now, Environment.OSVersion.Version.Build,
            Environment.ProcessorCount, "unavailable", "api-unavailable", [], [], settings,
            new("not-measured", "not-measured", "not-measured", "available"))
        { Power = SupplementarySystemContext.ReadPower(), Software = SupplementarySystemContext.ReadSoftware() };
        try
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                var gib = (memory.TotalPhysical + (1UL << 29)) >> 30;
                if (gib is >= 1 and <= 4096) result = result with { MemoryGiB = (int)gib };
            }
            var native = new NativeSnapshot { Size = (uint)Marshal.SizeOf<NativeSnapshot>(), Adapters = new NativeAdapter[4], Displays = new NativeDisplay[8] };
            if (WispReadEnvironment(ref native) < 0) return result;
            return Project(native, result);
        }
        catch (DllNotFoundException) { return result; }
        catch (EntryPointNotFoundException) { return result; }
        catch (Exception) { return result with { GraphicsReason = "collector-failed" }; }
    }

    internal static SupplementaryEnvironment Project(NativeSnapshot native, SupplementaryEnvironment basis)
    {
        if (native.Size != Marshal.SizeOf<NativeSnapshot>() || native.AdapterCount > 4 || native.DisplayCount > 8 ||
            native.Adapters is not { Length: 4 } || native.Displays is not { Length: 8 }) return basis with { GraphicsReason = "collector-failed" };
        var adapters = native.Adapters.Take((int)native.AdapterCount).Select(a => new SupplementaryAdapter(a.VendorId, a.DeviceId,
            SupplementaryEnvironment.ValidModel(a.Model?.Trim()) ? a.Model!.Trim() : "Unrecognized graphics adapter",
            a.DedicatedMemoryMiB, a.Software != 0, a.DriverValid == 1 ? $"{a.DriverMajor}.{a.DriverMinor}.{a.DriverBuild}.{a.DriverRevision}" : null)).ToImmutableArray();
        var displays = native.Displays.Take((int)native.DisplayCount).Select(d => new SupplementaryDisplay((int)d.AdapterIndex,
            d.Width, d.Height, d.Hdr switch { 1 => "disabled", 2 => "enabled", _ => "unknown" },
            d.RefreshMilliHz is >= 1000 and <= 1000000 ? d.RefreshMilliHz : null, d.ScalePercent is >= 100 and <= 800 ? d.ScalePercent : null,
            d.BitsPerColor is >= 1 and <= 32 ? d.BitsPerColor : null)).ToImmutableArray();
        var projected = basis with
        {
            GraphicsStatus = native.Status switch { 1 => "available", 2 => "partial", _ => "unavailable" },
            GraphicsReason = native.Reason switch { 0 => "none", 3 => "partial-adapters", 4 => "partial-displays", _ => "api-unavailable" },
            Adapters = adapters,
            Displays = displays
        };
        return projected.IsValid(basis.CollectedAt) ? projected : basis with { GraphicsReason = "collector-failed" };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeAdapter
    {
        public uint VendorId, DeviceId, DriverMajor, DriverMinor, DriverBuild, DriverRevision, DriverValid, DedicatedMemoryMiB, Software;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Model;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDisplay { public uint AdapterIndex, Width, Height, RefreshMilliHz, ScalePercent, Hdr, BitsPerColor; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSnapshot
    {
        public uint Size, Status, Reason, AdapterCount, DisplayCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public NativeAdapter[] Adapters;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public NativeDisplay[] Displays;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, ExtendedVirtual;
    }
    [DllImport("Wisp.NativeRenderer.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WispReadEnvironment(ref NativeSnapshot snapshot);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
