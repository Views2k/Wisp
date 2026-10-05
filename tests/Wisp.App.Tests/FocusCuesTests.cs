using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Xunit;

namespace Wisp.App.Tests;

public sealed class FocusCuesTests
{
    [Fact]
    public void NavigationKeysShowFocusOutlinesAndPointerInputHidesThem() => OnStaThread(() =>
    {
        using var source = new HwndSource(new HwndSourceParameters("Wisp focus cue test") { Width = 1, Height = 1, WindowStyle = 0 });
        KeyEventArgs Press(Key key, RoutedEvent routed) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routed };

        Assert.True(FocusCues.ModeFor(Press(Key.Tab, Keyboard.PreviewKeyDownEvent)));
        Assert.True(FocusCues.ModeFor(Press(Key.Down, Keyboard.PreviewKeyDownEvent)));
        // Modifier keys alone and key releases leave the current mode alone.
        Assert.Null(FocusCues.ModeFor(Press(Key.LeftShift, Keyboard.PreviewKeyDownEvent)));
        Assert.Null(FocusCues.ModeFor(Press(Key.LeftCtrl, Keyboard.PreviewKeyDownEvent)));
        Assert.Null(FocusCues.ModeFor(Press(Key.Tab, Keyboard.PreviewKeyUpEvent)));

        Assert.False(FocusCues.ModeFor(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent }));
        Assert.Null(FocusCues.ModeFor(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }));
        Assert.Null(FocusCues.ModeFor(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent }));
    });

    private static void OnStaThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Focus cue test exceeded its deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
