using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Wisp.Core;

namespace Wisp.App;

public sealed class ForzaFocusService
{
    private static readonly TimeSpan FullscreenCheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MissingForegroundRetention = TimeSpan.FromMilliseconds(250);

    private HashSet<int> _forzaProcessIds = new();
    private HashSet<string> _forzaExecutableDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ForzaProcessDiscovery _discovery;
    private volatile bool _captureRequested;
    private readonly ForzaCaptureObservationTracker _captureObservation = new();
    private ForzaCaptureCandidate? _captureCandidate;
    private DateTimeOffset _nextCaptureCheckAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFullscreenCheckAtUtc = DateTimeOffset.MinValue;
    private IntPtr _lastFullscreenWindow;
    private IntPtr _lastForzaForegroundWindow;
    private IntPtr _lastClassifiedForegroundWindow;
    private int _lastClassifiedForegroundProcessId;
    private int _lastClassifiedRootProcessId;
    private int _lastClassifiedRootOwnerProcessId;
    private bool _lastClassifiedForegroundIsForza;
    private bool _lastFullscreenState;
    private DateTimeOffset _lastForzaConfirmedAtUtc = DateTimeOffset.MinValue;

    public ForzaFocusService() => _discovery = new(() => FindForzaProcesses(_captureRequested));

    internal bool CaptureRequested
    {
        get => _captureRequested;
        set
        {
            _captureRequested = value;
            if (!value) { _captureCandidate = null; _captureObservation.Observe(null); _nextCaptureCheckAtUtc = DateTimeOffset.MinValue; }
        }
    }

    internal Clips.RecorderTargetObservation? CaptureObservation => _captureObservation.Current;

    public ForzaFocusState GetState(DateTimeOffset nowUtc)
    {
        if (_discovery.Refresh(nowUtc))
        {
            var snapshot = _discovery.Current;
            _forzaProcessIds = snapshot.ProcessIds;
            _forzaExecutableDirectories = snapshot.ExecutableDirectories;
            _captureCandidate = _captureRequested ? snapshot.CaptureCandidate : null;
            _lastClassifiedForegroundWindow = IntPtr.Zero;
            _lastClassifiedForegroundProcessId = 0;
            _lastClassifiedRootProcessId = 0;
            _lastClassifiedRootOwnerProcessId = 0;
        }

        RefreshCaptureObservation(nowUtc);

        var foregroundWindow = GetReliableForegroundWindow();
        if (foregroundWindow == IntPtr.Zero && _forzaProcessIds.Count > 0)
        {
            foregroundWindow = FindTopmostInteractiveWindow();
        }

        if (foregroundWindow == IntPtr.Zero)
        {
            var canRetainConfirmedWindow = _lastForzaForegroundWindow != IntPtr.Zero &&
                                           nowUtc - _lastForzaConfirmedAtUtc <= MissingForegroundRetention;
            var transitionIdentity = ForzaWindowIdentityPolicy.Evaluate(
                IntPtr.Zero,
                0,
                0,
                0,
                _forzaProcessIds,
                canRetainConfirmedWindow ? _lastForzaForegroundWindow : IntPtr.Zero);
            if (!transitionIdentity.IsForzaForeground)
            {
                ClearConfirmedForeground();
            }

            return new ForzaFocusState(
                _forzaProcessIds.Count > 0,
                transitionIdentity.IsForzaForeground,
                transitionIdentity.IsForzaForeground && _lastFullscreenState,
                transitionIdentity.ConfirmedForzaWindow);
        }

        GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        var rootWindow = GetAncestor(foregroundWindow, GetRoot);
        if (rootWindow == IntPtr.Zero)
        {
            rootWindow = foregroundWindow;
        }

        var rootOwnerWindow = GetAncestor(foregroundWindow, GetRootOwner);
        if (rootOwnerWindow == IntPtr.Zero)
        {
            rootOwnerWindow = rootWindow;
        }

        GetWindowThreadProcessId(rootWindow, out var rootProcessId);
        GetWindowThreadProcessId(rootOwnerWindow, out var rootOwnerProcessId);
        var hasKnownForzaProcessInChain =
            _forzaProcessIds.Contains(foregroundProcessId) ||
            _forzaProcessIds.Contains(rootProcessId) ||
            _forzaProcessIds.Contains(rootOwnerProcessId);
        var additionalSurfaceEvidence = !hasKnownForzaProcessInChain &&
                                        HasAdditionalForzaSurfaceEvidence(
                                            foregroundWindow,
                                            foregroundProcessId,
                                            rootWindow,
                                            rootProcessId,
                                            rootOwnerWindow,
                                            rootOwnerProcessId);
        var identity = ForzaWindowIdentityPolicy.Evaluate(
            foregroundWindow,
            foregroundProcessId,
            rootProcessId,
            rootOwnerProcessId,
            _forzaProcessIds,
            _lastForzaForegroundWindow,
            additionalSurfaceEvidence);
        var isForzaForeground = identity.IsForzaForeground;
        if (isForzaForeground)
        {
            _lastForzaForegroundWindow = identity.ConfirmedForzaWindow;
            _lastForzaConfirmedAtUtc = nowUtc;
        }
        else
        {
            ClearConfirmedForeground();
        }

        var fullscreenWindow = rootWindow != IntPtr.Zero ? rootWindow : foregroundWindow;
        if (isForzaForeground &&
            (fullscreenWindow != _lastFullscreenWindow || nowUtc >= _nextFullscreenCheckAtUtc))
        {
            _lastFullscreenWindow = fullscreenWindow;
            _lastFullscreenState = IsFullscreenWindow(fullscreenWindow);
            _nextFullscreenCheckAtUtc = nowUtc + FullscreenCheckInterval;
        }

        return new ForzaFocusState(
            _forzaProcessIds.Count > 0,
            isForzaForeground,
            isForzaForeground && _lastFullscreenState,
            foregroundWindow);
    }

    private bool HasAdditionalForzaSurfaceEvidence(
        IntPtr foregroundWindow,
        int foregroundProcessId,
        IntPtr rootWindow,
        int rootProcessId,
        IntPtr rootOwnerWindow,
        int rootOwnerProcessId)
    {
        if (_lastClassifiedForegroundWindow == foregroundWindow &&
            _lastClassifiedForegroundProcessId == foregroundProcessId &&
            _lastClassifiedRootProcessId == rootProcessId &&
            _lastClassifiedRootOwnerProcessId == rootOwnerProcessId)
        {
            return _lastClassifiedForegroundIsForza;
        }

        var isForza = false;
        var inspectDescendants = false;
        foreach (var windowHandle in DistinctWindowChain(
                     foregroundWindow,
                     rootWindow,
                     rootOwnerWindow))
        {
            if (WindowMatchesForza(windowHandle, out var recognizedWindowHost))
            {
                isForza = true;
                break;
            }

            inspectDescendants |= recognizedWindowHost;
        }

        if (!isForza && inspectDescendants)
        {
            var descendantRoot = rootOwnerWindow != IntPtr.Zero
                ? rootOwnerWindow
                : rootWindow;
            EnumChildWindows(
                descendantRoot,
                (windowHandle, parameter) =>
                {
                    GetWindowThreadProcessId(windowHandle, out var processId);
                    if (_forzaProcessIds.Contains(processId) ||
                        WindowMatchesForza(windowHandle, out _))
                    {
                        isForza = true;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);
        }

        _lastClassifiedForegroundWindow = foregroundWindow;
        _lastClassifiedForegroundProcessId = foregroundProcessId;
        _lastClassifiedRootProcessId = rootProcessId;
        _lastClassifiedRootOwnerProcessId = rootOwnerProcessId;
        _lastClassifiedForegroundIsForza = isForza;
        return isForza;
    }

    private bool WindowMatchesForza(IntPtr windowHandle, out bool recognizedWindowHost)
    {
        recognizedWindowHost = false;
        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            recognizedWindowHost = ForzaProcessIdentityPolicy.IsRecognizedWindowHost(processName);
            var caption = GetWindowCaption(windowHandle);
            if (ForzaProcessIdentityPolicy.Matches(processName, caption, null)) return true;
            if (!recognizedWindowHost || _forzaExecutableDirectories.Count == 0) return false;
            var executablePath = TryGetExecutablePath(process.Id);
            return ForzaProcessIdentityPolicy.Matches(
                processName,
                caption,
                executablePath,
                _forzaExecutableDirectories);
        }
        catch (ArgumentException)
        {
            // The foreground process exited between the Win32 queries.
        }
        catch (InvalidOperationException)
        {
            // The process exited while its metadata was being read.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Protected processes remain fail-closed.
        }

        return false;
    }

    private static IEnumerable<IntPtr> DistinctWindowChain(
        IntPtr foregroundWindow,
        IntPtr rootWindow,
        IntPtr rootOwnerWindow)
    {
        if (foregroundWindow != IntPtr.Zero)
        {
            yield return foregroundWindow;
        }

        if (rootWindow != IntPtr.Zero && rootWindow != foregroundWindow)
        {
            yield return rootWindow;
        }

        if (rootOwnerWindow != IntPtr.Zero &&
            rootOwnerWindow != foregroundWindow &&
            rootOwnerWindow != rootWindow)
        {
            yield return rootOwnerWindow;
        }
    }

    private void ClearConfirmedForeground()
    {
        _lastForzaForegroundWindow = IntPtr.Zero;
        _lastForzaConfirmedAtUtc = DateTimeOffset.MinValue;
        _lastFullscreenWindow = IntPtr.Zero;
        _lastFullscreenState = false;
        _nextFullscreenCheckAtUtc = DateTimeOffset.MinValue;
    }

    private static ForzaProcessSnapshot FindForzaProcesses(bool includeCapture)
    {
        var processIds = new HashSet<int>();
        var executableDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactMatches = Process.GetProcessesByName("forzahorizon6");
        try
        {
            foreach (var process in exactMatches)
            {
                processIds.Add(process.Id);
                AddExecutableDirectory(TryGetExecutablePath(process.Id), executableDirectories);
            }
        }
        finally
        {
            foreach (var process in exactMatches)
            {
                process.Dispose();
            }
        }

        if (processIds.Count > 0 && !includeCapture)
        {
            return new ForzaProcessSnapshot(processIds, executableDirectories);
        }

        var exactProcessFound = processIds.Count > 0;
        ForzaCaptureCandidate? captureCandidate = null;
        var owners = new ForzaWindowOwnerCache(TryGetProcessName, TryGetExecutablePath);
        EnumWindows(
            (windowHandle, _) =>
            {
                GetWindowThreadProcessId(windowHandle, out var processId);
                if (processId <= 0)
                {
                    return true;
                }

                if (!exactProcessFound && !processIds.Contains(processId) && owners.Matches(processId, GetWindowCaption(windowHandle)))
                {
                    processIds.Add(processId);
                    AddExecutableDirectory(owners.GetMatchedExecutablePath(processId), executableDirectories);
                }

                if (includeCapture && processIds.Contains(processId) &&
                    TryCaptureCandidate(windowHandle, processId) is { } candidate &&
                    (captureCandidate is null || (long)candidate.Width * candidate.Height > (long)captureCandidate.Width * captureCandidate.Height))
                    captureCandidate = candidate;

                return true;
            },
            IntPtr.Zero);

        return new ForzaProcessSnapshot(processIds, executableDirectories, captureCandidate);
    }

    private void RefreshCaptureObservation(DateTimeOffset nowUtc)
    {
        if (!_captureRequested || nowUtc < _nextCaptureCheckAtUtc) return;
        _nextCaptureCheckAtUtc = nowUtc + TimeSpan.FromMilliseconds(250);
        var candidate = _captureCandidate;
        if (candidate is not null)
        {
            var window = unchecked((IntPtr)(long)candidate.Window);
            GetWindowThreadProcessId(window, out var owner);
            if (owner != candidate.ProcessId || GetAncestor(window, GetRoot) != window ||
                !IsWindowVisible(window) || IsIconic(window) || IsWindowCloaked(window) ||
                !TryGetCaptureBounds(window, out var bounds))
                candidate = null;
            else candidate = candidate with { Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top };
        }
        _captureObservation.Observe(candidate);
    }

    private static bool TryGetCaptureBounds(IntPtr window, out NativeRectangle bounds)
    {
        bounds = default;
        // Keep physical capture geometry separate from the HUD's tolerant
        // fullscreen/foreground policy and restore the calling UI context.
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousDpi == IntPtr.Zero) return false;
        var eligible = false;
        try
        {
            var monitor = MonitorFromWindow(window, DefaultToNullMonitor);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) ||
                !GetClientRect(window, out var client)) return false;
            var topLeft = new NativePoint { X = client.Left, Y = client.Top };
            var bottomRight = new NativePoint { X = client.Right, Y = client.Bottom };
            if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight)) return false;
            bounds = new() { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
            eligible = ForzaCaptureEligibility.IsEligible(window, GetForegroundWindow(), bounds.ToPixelBounds(), info.Monitor.ToPixelBounds());
        }
        finally
        {
            if (SetThreadDpiAwarenessContext(previousDpi) == IntPtr.Zero) eligible = false;
        }
        return eligible;
    }

    private static ForzaCaptureCandidate? TryCaptureCandidate(IntPtr window, int processId)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || IsWindowCloaked(window) || GetWindow(window, 4) != IntPtr.Zero ||
            !GetWindowRect(window, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return null;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || !GetProcessTimes(process, out var creation, out _, out _, out _)) return null;
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length) ||
            !string.Equals(Path.GetFileName(path.ToString()), "ForzaHorizon6.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var time = ((ulong)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        if (time == 0) return null;
        return new((uint)processId, unchecked((ulong)window.ToInt64()), time, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    private static IntPtr FindTopmostInteractiveWindow()
    {
        var windowHandle = GetTopWindow(IntPtr.Zero);
        for (var examined = 0;
             windowHandle != IntPtr.Zero && examined < MaximumFallbackWindows;
             examined++)
        {
            GetWindowThreadProcessId(windowHandle, out var processId);
            var extendedStyle = GetWindowLong(windowHandle, ExtendedWindowStyleIndex);
            var isOwnOverlayWindow = processId == Environment.ProcessId &&
                                     (extendedStyle & ToolWindowStyle) != 0;
            if (processId > 0 && !isOwnOverlayWindow &&
                IsWindowVisible(windowHandle) && !IsWindowCloaked(windowHandle) &&
                GetWindowRect(windowHandle, out var bounds) &&
                bounds.Right - bounds.Left >= MinimumFallbackWindowWidth &&
                bounds.Bottom - bounds.Top >= MinimumFallbackWindowHeight &&
                MonitorFromWindow(windowHandle, DefaultToNullMonitor) != IntPtr.Zero)
            {
                var windowClass = GetWindowClass(windowHandle);
                if (!string.Equals(windowClass, "Shell_TrayWnd", StringComparison.Ordinal) &&
                    !string.Equals(windowClass, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                {
                    return windowHandle;
                }
            }

            var nextWindow = GetWindow(windowHandle, GetNextWindow);
            if (nextWindow == windowHandle)
            {
                break;
            }

            windowHandle = nextWindow;
        }

        return IntPtr.Zero;
    }

    private static bool IsWindowCloaked(IntPtr windowHandle)
    {
        return DwmGetWindowAttribute(
                   windowHandle,
                   DwmWindowAttributeCloaked,
                   out var cloaked,
                   Marshal.SizeOf<int>()) == 0 &&
               cloaked != 0;
    }

    private static string GetWindowClass(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(windowHandle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string GetWindowCaption(IntPtr windowHandle)
    {
        var length = Math.Min(GetWindowTextLength(windowHandle), MaximumWindowCaptionLength);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        return GetWindowText(windowHandle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    internal static string? TryGetExecutablePath(int processId)
    {
        // Getting the image name needs neither VM_READ nor enumeration of every
        // module. Protected/exited processes return false instead of throwing.
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
    }

    private static void AddExecutableDirectory(string? executablePath, HashSet<string> directories)
    {
        if (!string.IsNullOrWhiteSpace(executablePath) &&
            Path.GetDirectoryName(executablePath) is { Length: > 0 } directory)
        {
            directories.Add(Path.TrimEndingDirectorySeparator(directory));
        }
    }

    private static bool IsFullscreenWindow(IntPtr windowHandle)
    {
        if (!GetWindowRect(windowHandle, out var windowBounds))
        {
            return false;
        }

        var monitorHandle = MonitorFromWindow(windowHandle, DefaultToNearestMonitor);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitorHandle == IntPtr.Zero || !GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        return FullscreenBoundsPolicy.CoversMonitor(
            windowBounds.ToPixelBounds(),
            monitorInfo.Monitor.ToPixelBounds());
    }

    private static IntPtr GetReliableForegroundWindow()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero)
        {
            return foregroundWindow;
        }

        var guiThreadInfo = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref guiThreadInfo))
        {
            return IntPtr.Zero;
        }

        return guiThreadInfo.ActiveWindow != IntPtr.Zero
            ? guiThreadInfo.ActiveWindow
            : guiThreadInfo.FocusWindow;
    }

    private const int MinimumFallbackWindowWidth = 160;
    private const int MinimumFallbackWindowHeight = 120;
    private const int MaximumFallbackWindows = 1024;
    private const int MaximumWindowCaptionLength = 512;
    private const int DwmWindowAttributeCloaked = 14;
    private const int ExtendedWindowStyleIndex = -20;
    private const int ToolWindowStyle = 0x80;
    private const uint GetNextWindow = 2;
    private const uint GetRoot = 2;
    private const uint GetRootOwner = 3;
    private const uint DefaultToNullMonitor = 0;
    private const uint DefaultToNearestMonitor = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process,
        out System.Runtime.InteropServices.ComTypes.FILETIME creation, out System.Runtime.InteropServices.ComTypes.FILETIME exit,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo guiThreadInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parentWindow,
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr parentWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder windowText,
        int maximumCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out int attributeValue,
        int attributeSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelBounds ToPixelBounds() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public NativeRectangle CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }
}

public readonly record struct ForzaFocusState(
    bool IsForzaRunning,
    bool IsForzaForeground,
    bool IsFullscreen,
    IntPtr ForegroundWindow);
