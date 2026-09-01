using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wisp.Core;

namespace Wisp.App;

internal sealed class NonActivatingWindowDrag : IDisposable
{
    private const int ExtendedStyleIndex = -20;
    private const int LeftButtonVirtualKey = 0x01;
    private const int LeftButtonDownMessage = 0x0201;
    private const int LeftButtonUpMessage = 0x0202;
    private const int MouseMoveMessage = 0x0200;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint NoOwnerZOrder = 0x0200;

    private readonly Window _window;
    private readonly Action _savePlacement;
    private readonly DispatcherTimer _dragTimer;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private int _cursorOffsetX;
    private int _cursorOffsetY;
    private bool _interactive;
    private bool _dragging;
    private bool _disposed;

    public NonActivatingWindowDrag(Window window, Action savePlacement)
    {
        _window = window;
        _savePlacement = savePlacement;
        _dragTimer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _dragTimer.Tick += OnDragTimer;
        _window.SourceInitialized += OnSourceInitialized;
        _window.IsVisibleChanged += OnWindowIsVisibleChanged;
        _window.Closed += OnWindowClosed;
    }

    public void SetInteractive(bool interactive)
    {
        if (_disposed)
        {
            return;
        }

        _interactive = interactive;
        if (!interactive)
        {
            EndDrag(savePlacement: true);
        }

        ApplyInputStyle();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndDrag(savePlacement: false);
        _dragTimer.Tick -= OnDragTimer;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
        _window.Closed -= OnWindowClosed;
        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        _windowHandle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowProcedure);
        ApplyInputStyle();
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs) => Dispose();

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is false)
        {
            EndDrag(savePlacement: true);
        }
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (OverlayActivationPolicy.TryHandleWindowMessage(message, out var activationResult))
        {
            handled = true;
            return activationResult;
        }

        if (!_interactive)
        {
            return IntPtr.Zero;
        }

        switch (message)
        {
            case LeftButtonDownMessage:
                handled = BeginDrag();
                break;
            case MouseMoveMessage when _dragging:
                MoveToCursor();
                handled = true;
                break;
            case LeftButtonUpMessage when _dragging:
                MoveToCursor();
                EndDrag(savePlacement: true);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private bool BeginDrag()
    {
        if (_windowHandle == IntPtr.Zero ||
            !GetCursorPos(out var cursorPosition) ||
            !GetWindowRect(_windowHandle, out var windowBounds))
        {
            return false;
        }

        _cursorOffsetX = cursorPosition.X - windowBounds.Left;
        _cursorOffsetY = cursorPosition.Y - windowBounds.Top;
        _dragging = true;
        _dragTimer.Start();
        return true;
    }

    private void OnDragTimer(object? sender, EventArgs eventArgs)
    {
        if (!_interactive || (GetAsyncKeyState(LeftButtonVirtualKey) & 0x8000) == 0)
        {
            MoveToCursor();
            EndDrag(savePlacement: true);
            return;
        }

        MoveToCursor();
    }

    private void MoveToCursor()
    {
        if (!_dragging ||
            _windowHandle == IntPtr.Zero ||
            !GetCursorPos(out var cursorPosition))
        {
            return;
        }

        SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            cursorPosition.X - _cursorOffsetX,
            cursorPosition.Y - _cursorOffsetY,
            0,
            0,
            NoSize | NoZOrder | NoActivate | NoOwnerZOrder);
    }

    private void EndDrag(bool savePlacement)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _dragTimer.Stop();
        if (savePlacement)
        {
            _savePlacement();
        }
    }

    private void ApplyInputStyle()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var currentStyle = GetWindowLong(_windowHandle, ExtendedStyleIndex);
        var updatedStyle = OverlayActivationPolicy.BuildExtendedStyle(currentStyle, _interactive);
        if (updatedStyle != currentStyle)
        {
            SetWindowLong(_windowHandle, ExtendedStyleIndex, updatedStyle);
            SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NoMove | NoSize | NoZOrder | NoActivate | NoOwnerZOrder | FrameChanged);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

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
    }
}
