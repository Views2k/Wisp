using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

internal enum NativeRenderActivity
{
    Inactive,
    Static,
    Live
}

internal sealed class NativeRenderLifetime
{
    private readonly FrameworkElement _owner;
    private readonly Action _activityChanged;
    private Window? _host;
    private bool _wasUnloaded;
    private NativeRenderActivity _activity = NativeRenderActivity.Static;

    internal NativeRenderLifetime(FrameworkElement owner, Action activityChanged)
    {
        _owner = owner;
        _activityChanged = activityChanged;
    }

    internal bool IsLoaded { get; private set; }
    internal bool CanUpdateVisuals => _activity != NativeRenderActivity.Inactive;
    internal bool IsLive => _activity == NativeRenderActivity.Live;

    internal void Loaded()
    {
        if (IsLoaded)
            return;

        IsLoaded = true;
        _wasUnloaded = false;
        SetHost(Window.GetWindow(_owner));
        Refresh();
    }

    internal void Unloaded()
    {
        IsLoaded = false;
        _wasUnloaded = true;
        SetHost(null);
        Refresh();
    }

    internal void Refresh()
    {
        var host = IsLoaded ? _host : Window.GetWindow(_owner);
        var activity = ActivityFor(
            IsLoaded, _wasUnloaded, _owner.IsVisible,
            !IsLoaded && host is null && HasHiddenAncestor(_owner),
            host is not null, host?.IsVisible ?? false,
            host?.WindowState == WindowState.Minimized);
        if (activity == _activity)
            return;

        _activity = activity;
        _activityChanged();
    }

    internal static NativeRenderActivity ActivityFor(
        bool isLoaded, bool wasUnloaded, bool isVisible, bool hasHiddenAncestor,
        bool hasHost, bool hostIsVisible, bool hostIsMinimized)
    {
        if (wasUnloaded || hasHiddenAncestor ||
            (hasHost && (!hostIsVisible || hostIsMinimized)))
            return NativeRenderActivity.Inactive;

        if (isLoaded)
            return isVisible ? NativeRenderActivity.Live : NativeRenderActivity.Inactive;

        // Detached, never-loaded controls still support static rendering. A
        // known hidden window is not an offscreen rendering request.
        return hasHost && !isVisible ? NativeRenderActivity.Inactive : NativeRenderActivity.Static;
    }

    private void SetHost(Window? host)
    {
        if (ReferenceEquals(_host, host))
            return;

        if (_host is not null)
        {
            _host.StateChanged -= OnHostChanged;
            _host.IsVisibleChanged -= OnHostVisibilityChanged;
            _host.Closed -= OnHostClosed;
        }

        _host = host;
        if (_host is not null)
        {
            _host.StateChanged += OnHostChanged;
            _host.IsVisibleChanged += OnHostVisibilityChanged;
            _host.Closed += OnHostClosed;
        }
    }

    private void OnHostChanged(object? sender, EventArgs eventArgs) => Refresh();

    private void OnHostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs eventArgs) => Refresh();

    private void OnHostClosed(object? sender, EventArgs eventArgs) => Unloaded();

    private static bool HasHiddenAncestor(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null;)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
                return true;

            current = current is Visual
                ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
