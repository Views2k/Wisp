using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.App.NativeRendering;

// UI-thread adapter: capture layout/appearance, then hand immutable state to
// the independent render thread. It never draws or waits for the GPU here.
internal sealed class DirectCompositionAnalogHost : IDisposable
{
    private readonly NativeAnalogSpeedometer _owner;
    private readonly OverlayWindow _window;
    private readonly Action<bool, int> _status;
    private readonly int _controlId;
    private AnalogHudRenderWorker? _worker;
    private AnalogHudLayout? _layout;
    private AnalogHudPresentation? _presentation;
    private bool _disposed;
    private bool _failed;
    private Size _layoutSize;
    private DpiScale _layoutDpi;

    internal DirectCompositionAnalogHost(NativeAnalogSpeedometer owner, OverlayWindow window,
        int controlId, Action<bool, int> status)
    {
        _owner = owner;
        _window = window;
        _controlId = controlId;
        _status = status;
        RefreshPresentation();
        _owner.LayoutUpdated += OnLayoutUpdated;
    }

    internal void UpdateFrame(NativeGaugeFrame frame)
    {
        if (_disposed) return;
        RefreshPresentation();
        _worker?.UpdateFrame(frame, Stopwatch.GetTimestamp());
    }

    internal void RefreshPresentation()
    {
        if (_disposed || _failed) return;
        try
        {
            RefreshPresentationCore();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _failed = true;
            ReportStatus(false, error.HResult);
        }
    }

    private void RefreshPresentationCore()
    {
        bool active = _owner.IsLoaded && _owner.IsVisible && _window.IsVisible &&
                      _window.WindowState != WindowState.Minimized;
        if (!active)
        {
            if (_presentation is not null)
            {
                _presentation = _presentation with { Active = false, Opacity = 0 };
                _worker?.UpdatePresentation(_presentation);
            }
            return;
        }
        if (!_owner.IsArrangeValid) return;
        var source = PresentationSource.FromVisual(_owner) as HwndSource;
        if (source?.CompositionTarget is null || !GetClientRect(source.Handle, out var client)) return;
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0 || _owner.RenderSize.Width <= 0 || _owner.RenderSize.Height <= 0) return;
        var transform = _owner.TransformToAncestor(_window);
        var dpi = source.CompositionTarget.TransformToDevice;
        var origin = dpi.Transform(transform.Transform(new Point(0, 0)));
        var x = dpi.Transform(transform.Transform(new Point(1, 0))) - origin;
        var y = dpi.Transform(transform.Transform(new Point(0, 1))) - origin;
        double opacity = 1;
        for (DependencyObject? current = _owner; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element) opacity *= element.Opacity;
            if (ReferenceEquals(current, _window)) break;
        }
        var currentDpi = VisualTreeHelper.GetDpi(_owner);
        if (_layout is null || _layoutSize != _owner.RenderSize ||
            _layoutDpi.DpiScaleX != currentDpi.DpiScaleX || _layoutDpi.DpiScaleY != currentDpi.DpiScaleY)
        {
            _layout = AnalogHudLayout.Capture(_owner);
            _layoutSize = _owner.RenderSize;
            _layoutDpi = currentDpi;
        }
        var color = (_owner.TryFindResource(TractionCueThemeResources.BrushKey) as SolidColorBrush)?.Color ?? Colors.White;
        var traction = (_owner.DataContext as DiagnosticsViewModel)?.IsTractionCueActive ?? false;
        _presentation = new(width, height, (float)origin.X, (float)origin.Y,
            (float)x.X, (float)x.Y, (float)y.X, (float)y.Y,
            (float)Math.Clamp(opacity, 0, 1), true, traction, new(color.R, color.G, color.B, color.A), _layout);
        if (_worker is null)
        {
            var textures = AnalogHudAssets.LoadOnUiThread();
            _worker = new AnalogHudRenderWorker(source.Handle, _controlId, textures, _presentation, ReportStatus,
                (_owner.DataContext as DiagnosticsViewModel)?.ActiveCpuRendering == true);
        }
        else _worker.UpdatePresentation(_presentation);
    }

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        if (_disposed) return;
        RefreshPresentation();
    }

    private void ReportStatus(bool ready, int hresult)
    {
        if (_owner.Dispatcher.HasShutdownStarted) return;
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed) _status(ready, hresult);
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.LayoutUpdated -= OnLayoutUpdated;
        _worker?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rectangle);
}
