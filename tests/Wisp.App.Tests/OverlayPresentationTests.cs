using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class OverlayPresentationTests
{
    private const int ExtendedStyleIndex = -20;
    private const int LayeredStyle = 0x00080000;
    private const int AcceptFilesStyle = 0x00000010;
    private const int CompositionChangedMessage = 0x031E;

    internal static void AssertOnCurrentDispatcher()
    {
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = false,
            Background = Brushes.Transparent,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = SystemParameters.VirtualScreenLeft - 256,
            Top = SystemParameters.VirtualScreenTop - 256,
            Width = 64,
            Height = 64,
            Content = new Border { Background = Brushes.Black, Width = 16, Height = 16 }
        };
        var savedPlacements = 0;
        using var drag = new NonActivatingWindowDrag(window, () => savedPlacements++);
        try
        {
            drag.SetInteractive(false);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var source = Assert.IsType<HwndSource>(HwndSource.FromHwnd(handle));
            Assert.False(source.UsesPerPixelOpacity);
            Assert.Equal(Colors.Transparent, source.CompositionTarget.BackgroundColor);
            AssertPresentation(handle, interactive: false);

            // An ordinary extended-style change must preserve both the compositor
            // bit and the caller's unrelated style, including WPF's own updates.
            SetWindowLong(handle, ExtendedStyleIndex,
                (GetWindowLong(handle, ExtendedStyleIndex) & ~LayeredStyle) | AcceptFilesStyle);
            AssertPresentation(handle, interactive: false);
            Assert.NotEqual(0, GetWindowLong(handle, ExtendedStyleIndex) & AcceptFilesStyle);

            foreach (var interactive in new[] { false, true, false })
            {
                drag.SetInteractive(interactive);
                window.Show();
                Flush(window);
                Assert.True(window.IsVisible);
                Assert.False(window.IsActive);
                Assert.NotEqual(handle, GetForegroundWindow());
                AssertPresentation(handle, interactive);

                window.Hide();
                window.Width += 8;
                window.Height += 8;
                Flush(window);
                Assert.False(window.IsVisible);
                AssertPresentation(handle, interactive);

                window.Show();
                Flush(window);
                Assert.Same(source, PresentationSource.FromVisual(window));
                AssertPresentation(handle, interactive);

                SendMessage(handle, CompositionChangedMessage, IntPtr.Zero, IntPtr.Zero);
                AssertPresentation(handle, interactive);
            }

            Assert.Equal(0, savedPlacements);
            drag.Dispose();
            SetWindowLong(handle, ExtendedStyleIndex,
                GetWindowLong(handle, ExtendedStyleIndex) | LayeredStyle);
            Assert.Equal(0, GetWindowLong(handle, ExtendedStyleIndex) & LayeredStyle);
            var disposedStyle = GetWindowLong(handle, ExtendedStyleIndex);
            drag.SetInteractive(true);
            Assert.Equal(disposedStyle, GetWindowLong(handle, ExtendedStyleIndex));
            window.Close();
            Assert.False(IsWindow(handle));
        }
        finally
        {
            window.Close();
            application.ShutdownMode = shutdownMode;
        }
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void AssertPresentation(IntPtr handle, bool interactive)
    {
        var style = GetWindowLong(handle, ExtendedStyleIndex);
        Assert.NotEqual(0, style & LayeredStyle);
        Assert.NotEqual(0, style & OverlayActivationPolicy.NoActivateExtendedStyle);
        Assert.NotEqual(0, style & OverlayActivationPolicy.ToolWindowExtendedStyle);
        Assert.Equal(!interactive, (style & OverlayActivationPolicy.TransparentExtendedStyle) != 0);
        Assert.True(GetLayeredWindowAttributes(handle, out _, out var alpha, out var flags));
        Assert.Equal(byte.MaxValue, alpha);
        Assert.Equal(2U, flags);
        Assert.Equal(new IntPtr(OverlayActivationPolicy.MouseActivateNoActivateResult),
            SendMessage(handle, OverlayActivationPolicy.MouseActivateMessage, IntPtr.Zero, IntPtr.Zero));
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(
        IntPtr handle, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wordParameter, IntPtr longParameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
}
