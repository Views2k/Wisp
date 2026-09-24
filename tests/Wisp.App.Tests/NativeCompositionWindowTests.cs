using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App.DebugLogging;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

internal static class NativeCompositionWindowTests
{
    internal static void AssertHost(OverlayWindow window, IntPtr native)
    {
        var layout = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(layout, native);
        OverlayPresentationTests.AssertPassiveRequest(native, native: true);
        OverlayPresentationTests.AssertPassiveRequest(layout, native: false);
        AssertPassiveWindowLifetime(layout);
        Assert.Null(HwndSource.FromHwnd(native));
        Assert.NotEqual(0, GetWindowLong(native, -20) & 0x00200000);
        Assert.NotEqual(0, GetWindowLong(native, -20) & 0x00080000);
        Assert.NotEqual(0, GetWindowLong(native, -20) & 0x08000000);
        Assert.True(IsWindowVisible(native));
        Assert.Equal(0, DwmGetWindowAttribute(layout, 14, out var cloak, sizeof(int)));
        Assert.NotEqual(0, cloak & 1);
        Assert.Equal(0, DwmGetWindowAttribute(native, 14, out cloak, sizeof(int)));
        Assert.Equal(0, cloak);
        Assert.False(GetLayeredWindowAttributes(native, out _, out _, out _));
        AssertBounds();
        var original = Snapshot(window);
        var worker = Worker(window);
        AssertVisibleArtwork(native, layout);
        var foreground = GetForegroundWindow();
        var moveStarted = Stopwatch.GetTimestamp();
        window.Left += 23;
        window.Top += 17;
        Pump();
        AssertBounds();
        var moved = Snapshot(window);
        Assert.Equal(original.Width, moved.Width);
        Assert.Equal(original.Height, moved.Height);
        Assert.Same(worker, Worker(window));
        Assert.Equal(original.Layers.Length, moved.Layers.Length);
        for (var index = 0; index < original.Layers.Length; index++)
            Assert.True(original.Layers[index].CompatibleWith(moved.Layers[index]),
                "Moving the host must preserve HUD-local artwork placement.");
        WaitForPosition(moveStarted);
        AssertVisibleArtwork(native, layout);
        Assert.Equal(foreground, GetForegroundWindow());

        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
            Assert.True(WindowZOrder.AttachAboveGame(window, ownerHandle, raise: false));
            Assert.Equal(ownerHandle, GetWindow(native, 4));
            WindowZOrder.DetachFromGame(window);
            Assert.Equal(GetWindow(layout, 4), GetWindow(native, 4));
            Assert.NotEqual(ownerHandle, GetWindow(native, 4));
        }
        finally { WindowZOrder.DetachFromGame(window); owner.Close(); }
        Assert.True(GetWindowRect(native, out var rectangle));
        // Query from another thread: HTTRANSPARENT alone only works within
        // one input thread, so it must not be our cross-process input policy.
        var hud = LayoutBounds(layout);
        AssertClickThrough(new Point { X = (hud.Left + hud.Right) / 2, Y = (hud.Top + hud.Bottom) / 2 });
        var emptyPoint = new[]
        {
            new Point { X = rectangle.Left + 1, Y = rectangle.Top + 1 },
            new Point { X = rectangle.Right - 2, Y = rectangle.Top + 1 },
            new Point { X = rectangle.Left + 1, Y = rectangle.Bottom - 2 },
            new Point { X = rectangle.Right - 2, Y = rectangle.Bottom - 2 }
        }.FirstOrDefault(point => point.X < hud.Left || point.X >= hud.Right || point.Y < hud.Top || point.Y >= hud.Bottom);
        if (rectangle.Right - rectangle.Left > hud.Width || rectangle.Bottom - rectangle.Top > hud.Height)
            AssertClickThrough(emptyPoint);

        window.SetEditMode(true);
        Pump();
        Assert.True(IsWindowVisible(native));
        Assert.Equal(0, DwmGetWindowAttribute(layout, 14, out cloak, sizeof(int)));
        Assert.Equal(0, cloak & 1);
        window.SetEditMode(false);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        do { Pump(); Thread.Sleep(10); _ = DwmGetWindowAttribute(layout, 14, out cloak, sizeof(int)); }
        while ((cloak & 1) == 0 && DateTime.UtcNow < deadline);
        Assert.NotEqual(0, cloak & 1);
        Assert.True(IsWindowVisible(native));
        Assert.Equal(native, HudNativeHost.PresentationHandle(window));
        Assert.Equal(foreground, GetForegroundWindow());

        void AssertBounds()
        {
            var content = LayoutBounds(layout);
            var monitor = MonitorFromWindow(layout, 2);
            Assert.NotEqual(IntPtr.Zero, monitor);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.True(GetMonitorInfo(monitor, ref info));
            var full = info.Monitor;
            var contained = content.Left >= full.Left && content.Top >= full.Top &&
                content.Right <= full.Right && content.Bottom <= full.Bottom;
            Assert.True(contained, "This live host test must exercise a HUD wholly contained in one monitor.");
            Assert.True(GetWindowRect(native, out var bounds));
            Assert.Equal(full.Left, bounds.Left);
            Assert.Equal(full.Top, bounds.Top);
            Assert.Equal(full.Right, bounds.Right);
            Assert.Equal(full.Bottom, bounds.Bottom);
            var snapshot = Snapshot(window);
            Assert.Equal(content.Width, snapshot.Width);
            Assert.Equal(content.Height, snapshot.Height);
            Assert.Equal((float)content.Left, bounds.Left + snapshot.HostOffsetX);
            Assert.Equal((float)content.Top, bounds.Top + snapshot.HostOffsetY);
        }

        void AssertClickThrough(Point point)
        {
            var target = Task.Run(() => WindowFromPoint(point));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!target.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Assert.True(target.IsCompleted, "Input hit-test did not complete.");
            Assert.NotEqual(native, target.GetAwaiter().GetResult());
        }

        void WaitForPosition(long after)
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (!PositionApplied() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Assert.True(PositionApplied(), "The render worker did not apply the moved host offset.");

            bool PositionApplied()
            {
                var diagnostics = TachDiagnostics.Snapshot();
                return diagnostics is not null && diagnostics.RendererStartup.Concat(diagnostics.RendererRecent)
                    .Any(row => row.HostWindowHandle == native.ToInt64() && row.Stage == "state" &&
                        row.Result == "positioned" && row.HResult == 0 && row.StartedTimestamp >= after);
            }
        }
    }

    private static void AssertPassiveWindowLifetime(IntPtr layout)
    {
        // A new HWND owns its request state, including native-only hosts which
        // never pass through the WPF presentation initializer.
        IntPtr handle;
        using (var host = new NativeCompositionWindow(HwndSource.FromHwnd(layout)!, () => { }, _ => { }))
        {
            handle = host.Handle;
            OverlayPresentationTests.AssertPassiveRequest(handle, native: true);
            SendMessage(handle, 0x031E, IntPtr.Zero, IntPtr.Zero);
            OverlayPresentationTests.AssertPassiveRequest(handle, native: true);
        }
        Assert.DoesNotContain(OverlayPassiveUpdate.Snapshot(), value => value.WindowHandle == handle.ToInt64());
    }

    private static void AssertVisibleArtwork(IntPtr native, IntPtr layout)
    {
        // The native host can cover the monitor; only the original HUD region
        // should allocate pixels or participate in this artwork comparison.
        var bounds = LayoutBounds(layout);
        var background = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(18, 52, 86)),
            Width = 400,
            Height = 400
        };
        var foreground = GetForegroundWindow();
        IntPtr dc = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            background.Show();
            var behind = new WindowInteropHelper(background).Handle;
            Assert.True(SetWindowPos(behind, native, bounds.Left - 10, bounds.Top - 10,
                bounds.Right - bounds.Left + 20, bounds.Bottom - bounds.Top + 20, 0x0010));
            background.UpdateLayout();
            Pump();
            Assert.Equal(0, DwmFlush());
            dc = GetDC(IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, dc);
            var width = bounds.Right - bounds.Left + 20;
            var height = bounds.Bottom - bounds.Top + 20;
            var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            memory = CreateCompatibleDC(dc);
            bitmap = CreateDIBSection(dc, ref header, 0, out var pixels, IntPtr.Zero, 0);
            Assert.NotEqual(IntPtr.Zero, memory);
            Assert.NotEqual(IntPtr.Zero, bitmap);
            previous = SelectObject(memory, bitmap);
            var frame = new int[width * height];
            // The WPF fixture's pixels can reach composition after its layout
            // completes. Wait for observed fixture/HUD pixels, not a fixed delay.
            var wait = Stopwatch.StartNew();
            int attempts = 0, firstClear = 0, clear = 0, transparent = 0, brightArtwork = 0;
            bool ready;
            do
            {
                Pump();
                Assert.Equal(0, DwmFlush());
                Assert.True(BitBlt(memory, 0, 0, width, height, dc, bounds.Left - 10, bounds.Top - 10, 0x40CC0020));
                // GDI must finish writing the DIB before its pointer is read.
                Assert.True(GdiFlush());
                Marshal.Copy(pixels, frame, 0, frame.Length);
                clear = frame[5 * width + 5] & 0xFFFFFF;
                if (++attempts == 1) firstClear = clear;
                transparent = brightArtwork = 0;
                for (var y = bounds.Top + 2; y < bounds.Bottom - 2; y += 5)
                    for (var x = bounds.Left + 2; x < bounds.Right - 2; x += 5)
                    {
                        var pixel = frame[(y - bounds.Top + 10) * width + x - bounds.Left + 10] & 0xFFFFFF;
                        if (pixel == clear) transparent++;
                        if ((pixel & 255) > 160 && ((pixel >> 8) & 255) > 160 && ((pixel >> 16) & 255) > 160) brightArtwork++;
                    }
                ready = (clear & 255) > ((clear >> 16) & 255) && transparent > 100 && brightArtwork > 10;
                if (ready || wait.Elapsed >= TimeSpan.FromSeconds(2)) break;
                Thread.Sleep(10);
            }
            while (wait.Elapsed < TimeSpan.FromSeconds(2));
            Console.WriteLine(FormattableString.Invariant(
                $"nativeHostArtwork attempts={attempts};failedAttempts={attempts - (ready ? 1 : 0)};firstCorner=0x{firstClear:X6};finalCorner=0x{clear:X6};transparentSamples={transparent};brightSamples={brightArtwork};ready={ready};elapsedMs={wait.Elapsed.TotalMilliseconds:F1}"));
            var reviewPath = Environment.GetEnvironmentVariable("WISP_NATIVE_HOST_REVIEW");
            if (!string.IsNullOrEmpty(reviewPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, frame, width * 4)));
                using var stream = System.IO.File.Create(reviewPath);
                encoder.Save(stream);
            }
            Assert.True((clear & 255) > ((clear >> 16) & 255),
                "The empty native host must reveal the blue background beyond the HUD artwork.");
            Assert.True(transparent > 100, $"The native HUD must preserve transparent areas (samples={transparent}).");
            Assert.True(brightArtwork > 10, $"The native HWND must display the real gauge artwork (samples={brightArtwork}).");
            Assert.Equal(foreground, GetForegroundWindow());
        }
        finally
        {
            if (previous != IntPtr.Zero) _ = SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
            if (memory != IntPtr.Zero) _ = DeleteDC(memory);
            if (dc != IntPtr.Zero) _ = ReleaseDC(IntPtr.Zero, dc);
            background.Close();
        }
    }

    private static NativeHostBounds LayoutBounds(IntPtr layout)
    {
        Assert.True(GetClientRect(layout, out var client));
        var origin = new Point();
        Assert.True(ClientToScreen(layout, ref origin));
        return new(origin.X, origin.Y, origin.X + client.Right - client.Left,
            origin.Y + client.Bottom - client.Top);
    }

    private static HudNativeHost Host(Window window)
    {
        var table = (ConditionalWeakTable<Window, HudNativeHost>)typeof(HudNativeHost)
            .GetField("Hosts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(table.TryGetValue(window, out var host));
        return host!;
    }

    private static HudWindowSnapshot Snapshot(Window window) =>
        Assert.IsType<HudWindowSnapshot>(typeof(HudNativeHost)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host(window)));

    private static AnalogHudRenderWorker Worker(Window window) =>
        Assert.IsType<AnalogHudRenderWorker>(typeof(HudNativeHost)
            .GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host(window)));

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rectangle Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPixels, YPixels;
        public uint Used, Important;
    }
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr word, IntPtr data);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rectangle rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rectangle rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
