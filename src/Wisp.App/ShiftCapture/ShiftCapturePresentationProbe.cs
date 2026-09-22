using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisp.App.ShiftCapture;

internal readonly record struct ShiftCaptureRect(int X, int Y, int Width, int Height)
{
    internal bool IsValid => Width is > 0 and <= 256 && Height is > 0 and <= 256 &&
        (long)X + Width <= int.MaxValue && (long)Y + Height <= int.MaxValue;
}

internal enum ShiftCapturePresentationStatus
{
    Observed,
    InvalidRegion,
    OverlayUnavailable,
    CaptureExcluded,
    CaptureFailed,
    CostLimitExceeded,
    Disposed
}

internal readonly record struct ShiftCapturePixelSummary(int PixelCount, int CompatiblePixelCount, ulong PixelHash)
{
    public double CompatibleFraction => PixelCount > 0 ? (double)CompatiblePixelCount / PixelCount : 0;
}

internal sealed record ShiftCapturePresentationSample(
    long ContextVersion,
    ShiftCapturePresentationStatus Status,
    bool Qualified,
    long ReadStartedQpc,
    long ReadFinishedQpc,
    long? PreviousReadStartedQpc,
    long? PreviousReadFinishedQpc,
    long QpcFrequency,
    double ReadbackMilliseconds,
    ShiftCapturePixelSummary Pixels,
    int ErrorCode)
{
    // These are CPU readback intervals. GDI provides no compositor frame timestamp,
    // display/scanout timestamp, or guarantee that this contains the latest frame.
    public string EvidenceKind => "conditional-desktop-roi-readback";
    public double RecommendedIntervalMilliseconds { get; init; }
}

internal interface IShiftCapturePixelSource : IDisposable
{
    ShiftCapturePresentationStatus OverlayStatus();
    bool TryCopy(ShiftCaptureRect region, byte[] bgra, out int errorCode);
}

/// <summary>
/// Private-session, caller-paced desktop readback. Qualification must come from an
/// actual cue off/on/off check using the same composition path, region, color and
/// foreground context. A synthetic WPF window alone cannot qualify a DComp HUD.
/// Color compatibility is evidence to inspect, not proof of a cue or visible photons.
/// </summary>
internal sealed class ShiftCapturePresentationProbe : IDisposable
{
    private const double MinimumIntervalMilliseconds = 1000d / 60;
    private const double MaximumIntervalMilliseconds = 1000;
    private const double ReadbackDutyBudget = .05;
    private const double HardReadbackLimitMilliseconds = 50;
    private readonly object _gate = new();
    private IShiftCapturePixelSource _source;
    private readonly Func<long> _clock;
    private readonly long _frequency;
    private byte[] _pixels = [];
    private (ShiftCaptureRect Rect, uint Color, long Version)? _context;
    private ShiftCapturePresentationSample? _previous;
    private bool _qualified;
    private bool _disposed;
    private bool _costStopped;
    private double _intervalMilliseconds = MinimumIntervalMilliseconds;
    private ShiftCapturePresentationStatus? _reportedFailure;
    private long? _lastAttempt;

    internal ShiftCapturePresentationProbe(nint overlayWindowHandle)
        : this(new DesktopPixelSource(overlayWindowHandle), Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

    internal ShiftCapturePresentationProbe(IShiftCapturePixelSource source, Func<long> clock, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _source = source;
        _clock = clock;
        _frequency = qpcFrequency;
    }

    internal bool SetQualified(bool qualified)
    {
        lock (_gate)
        {
            _qualified = qualified && !_disposed && !_costStopped && _context.HasValue &&
                _previous?.Status == ShiftCapturePresentationStatus.Observed &&
                _source.OverlayStatus() == ShiftCapturePresentationStatus.Observed;
            return _qualified;
        }
    }

    internal TimeSpan RecommendedInterval
    {
        get { lock (_gate) return TimeSpan.FromMilliseconds(_intervalMilliseconds); }
    }

    internal void ChangeWindow(nint overlayWindowHandle) => ReplaceSource(new DesktopPixelSource(overlayWindowHandle));

    internal void ReplaceSource(IShiftCapturePixelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (ReferenceEquals(_source, source)) return;
            if (_disposed)
            {
                source.Dispose();
                return;
            }
            var previousSource = _source;
            _source = source;
            _qualified = false;
            _context = null;
            _previous = null;
            Array.Clear(_pixels);
            previousSource.Dispose();
        }
    }

    // No timer, UI callback, storage or capture runs unless the private coordinator
    // calls this method. Null means pacing skipped a read or an unchanged failure
    // was already reported. The observed high-water cost never resets with context.
    internal ShiftCapturePresentationSample? TrySample(ShiftCaptureRect physicalRect, uint expectedArgb, long contextVersion)
    {
        lock (_gate)
        {
            var now = _clock();
            var context = (physicalRect, expectedArgb, contextVersion);
            if (_context != context)
            {
                _qualified = false;
                _previous = null;
                _context = context;
            }
            if (_disposed) return Failure(ShiftCapturePresentationStatus.Disposed, now);
            if (_costStopped) return Failure(ShiftCapturePresentationStatus.CostLimitExceeded, now);
            if (!physicalRect.IsValid || (expectedArgb >> 24) == 0)
                return Failure(ShiftCapturePresentationStatus.InvalidRegion, now);
            var overlayStatus = _source.OverlayStatus();
            if (overlayStatus != ShiftCapturePresentationStatus.Observed) return Failure(overlayStatus, now);
            if (_lastAttempt is { } last && now >= last &&
                (double)(now - last) * 1000 / _frequency < _intervalMilliseconds)
                return null;
            var required = checked(physicalRect.Width * physicalRect.Height * 4);
            if (_pixels.Length != required) _pixels = new byte[required];
            var started = _clock();
            _lastAttempt = started;
            var copied = _source.TryCopy(physicalRect, _pixels, out var errorCode);
            var finished = _clock();
            if (finished < started)
            {
                Array.Clear(_pixels);
                _intervalMilliseconds = MaximumIntervalMilliseconds;
                return Failure(ShiftCapturePresentationStatus.CaptureFailed, started, finished, errorCode);
            }
            var cost = (double)(finished - started) * 1000 / _frequency;
            _intervalMilliseconds = Math.Clamp(Math.Max(_intervalMilliseconds, cost / ReadbackDutyBudget),
                MinimumIntervalMilliseconds, MaximumIntervalMilliseconds);
            _costStopped = cost > HardReadbackLimitMilliseconds;
            if (_costStopped)
            {
                Array.Clear(_pixels);
                return Failure(ShiftCapturePresentationStatus.CostLimitExceeded, started, finished);
            }
            if (!copied)
            {
                Array.Clear(_pixels);
                return Failure(ShiftCapturePresentationStatus.CaptureFailed, started, finished, errorCode);
            }
            var summary = AnalyzeBgra(_pixels, expectedArgb);
            Array.Clear(_pixels);
            var sample = new ShiftCapturePresentationSample(contextVersion,
                ShiftCapturePresentationStatus.Observed, _qualified, started, finished,
                _previous?.ReadStartedQpc, _previous?.ReadFinishedQpc, _frequency, cost, summary, 0)
            { RecommendedIntervalMilliseconds = _intervalMilliseconds };
            _reportedFailure = null;
            _previous = sample;
            return sample;
        }
    }

    private ShiftCapturePresentationSample? Failure(ShiftCapturePresentationStatus status, long started,
        long? finished = null, int errorCode = 0)
    {
        _qualified = false;
        _previous = null;
        if (_reportedFailure == status) return null;
        _reportedFailure = status;
        var end = finished ?? started;
        return new(_context?.Version ?? 0, status, false, started, end, null, null, _frequency,
            Math.Max(0, (double)(end - started) * 1000 / _frequency), default, errorCode)
        { RecommendedIntervalMilliseconds = _intervalMilliseconds };
    }

    internal static ShiftCapturePixelSummary AnalyzeBgra(ReadOnlySpan<byte> bgra, uint expectedArgb)
    {
        if (bgra.Length == 0 || bgra.Length % 4 != 0) throw new ArgumentException("A complete BGRA region is required.", nameof(bgra));
        var alpha = (byte)(expectedArgb >> 24);
        var red = (byte)(expectedArgb >> 16);
        var green = (byte)(expectedArgb >> 8);
        var blue = (byte)expectedArgb;
        var matched = 0;
        var hash = 14695981039346656037UL;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            // GDI's fourth byte is reserved, not reliable alpha. Hash only RGB.
            for (var channel = 0; channel < 3; channel++) hash = unchecked((hash ^ bgra[i + channel]) * 1099511628211UL);
            if (alpha != 0 && Compatible(bgra[i], blue, alpha) && Compatible(bgra[i + 1], green, alpha) &&
                Compatible(bgra[i + 2], red, alpha)) matched++;
        }
        return new(bgra.Length / 4, matched, hash);
    }

    private static bool Compatible(byte observed, byte foreground, byte alpha)
    {
        // A translucent cue can be blended with any underlying pixel. Count the
        // mathematically compatible range, without assuming the road is black.
        // This becomes less selective with lower opacity; it is never a detector
        // of cue identity on its own. Tolerance covers capture/color quantization.
        const double tolerance = 12;
        var minimum = foreground * (alpha / 255d);
        var maximum = minimum + 255 - alpha;
        return observed >= minimum - tolerance && observed <= maximum + tolerance;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _qualified = false;
            Array.Clear(_pixels);
            _pixels = [];
            _source.Dispose();
        }
    }

    private sealed class DesktopPixelSource(nint overlayWindowHandle) : IShiftCapturePixelSource
    {
        private nint _memoryDc;
        private nint _bitmap;
        private nint _oldBitmap;
        private nint _bits;
        private int _width;
        private int _height;

        public ShiftCapturePresentationStatus OverlayStatus()
        {
            if (overlayWindowHandle == 0 || !IsWindow(overlayWindowHandle) || !IsWindowVisible(overlayWindowHandle))
                return ShiftCapturePresentationStatus.OverlayUnavailable;
            if (!GetWindowDisplayAffinity(overlayWindowHandle, out var affinity))
                return ShiftCapturePresentationStatus.OverlayUnavailable;
            return affinity != 0 ? ShiftCapturePresentationStatus.CaptureExcluded : ShiftCapturePresentationStatus.Observed;
        }

        public bool TryCopy(ShiftCaptureRect region, byte[] bgra, out int errorCode)
        {
            errorCode = 0;
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            if (region.X < left || region.Y < top || (long)region.X + region.Width > (long)left + GetSystemMetrics(78) ||
                (long)region.Y + region.Height > (long)top + GetSystemMetrics(79)) return false;
            var desktopDc = GetDC(0);
            if (desktopDc == 0) { errorCode = Marshal.GetLastWin32Error(); return false; }
            try
            {
                if (!Prepare(desktopDc, region.Width, region.Height) ||
                    !BitBlt(_memoryDc, 0, 0, region.Width, region.Height, desktopDc, region.X, region.Y, 0x40CC0020) || !GdiFlush())
                { errorCode = Marshal.GetLastWin32Error(); return false; }
                Marshal.Copy(_bits, bgra, 0, bgra.Length);
                return true;
            }
            finally { ReleaseDC(0, desktopDc); }
        }

        private bool Prepare(nint desktopDc, int width, int height)
        {
            if (_memoryDc != 0 && _width == width && _height == height) return true;
            Dispose();
            _memoryDc = CreateCompatibleDC(desktopDc);
            if (_memoryDc == 0) return false;
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 }
            };
            _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out _bits, 0, 0);
            if (_bitmap == 0 || _bits == 0) { Dispose(); return false; }
            _oldBitmap = SelectObject(_memoryDc, _bitmap);
            if (_oldBitmap == 0 || _oldBitmap == new nint(-1)) { Dispose(); return false; }
            _width = width;
            _height = height;
            return true;
        }

        public void Dispose()
        {
            if (_oldBitmap != 0 && _oldBitmap != new nint(-1) && _memoryDc != 0) SelectObject(_memoryDc, _oldBitmap);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (_memoryDc != 0) DeleteDC(_memoryDc);
            _memoryDc = _bitmap = _oldBitmap = _bits = 0;
            _width = _height = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }

        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    }
}
