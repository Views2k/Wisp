using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Wisp.App;

[Flags]
public enum OverlayHotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public readonly record struct OverlayHotkeyChord(OverlayHotkeyModifiers Modifiers, Key Key)
{
    public static OverlayHotkeyChord Default { get; } =
        new(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift, Key.H);

    public static bool TryCreate(
        OverlayHotkeyModifiers modifiers,
        Key key,
        out OverlayHotkeyChord chord,
        out string error)
    {
        chord = default;
        var knownModifiers = OverlayHotkeyModifiers.Alt | OverlayHotkeyModifiers.Control |
                             OverlayHotkeyModifiers.Shift | OverlayHotkeyModifiers.Windows;
        if (modifiers == OverlayHotkeyModifiers.None || (modifiers & ~knownModifiers) != 0)
        {
            error = "Use at least one modifier (Ctrl, Alt, Shift, or Windows).";
            return false;
        }

        if (!Enum.IsDefined(key) ||
            key is Key.None or Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            error = "Add one non-modifier key to the shortcut.";
            return false;
        }

        if (IsReserved(modifiers, key))
        {
            error = "That shortcut is reserved by Windows. Choose another one.";
            return false;
        }

        if (KeyInterop.VirtualKeyFromKey(key) == 0)
        {
            error = "That key cannot be registered as a Windows shortcut.";
            return false;
        }

        chord = new OverlayHotkeyChord(modifiers, key);
        error = string.Empty;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(OverlayHotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (Modifiers.HasFlag(OverlayHotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (Modifiers.HasFlag(OverlayHotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (Modifiers.HasFlag(OverlayHotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    private static bool IsReserved(OverlayHotkeyModifiers modifiers, Key key) =>
        modifiers.HasFlag(OverlayHotkeyModifiers.Alt) && key is Key.Tab or Key.Escape or Key.F4 or Key.Space ||
        modifiers.HasFlag(OverlayHotkeyModifiers.Control) && key == Key.Escape ||
        modifiers.HasFlag(OverlayHotkeyModifiers.Control) &&
        modifiers.HasFlag(OverlayHotkeyModifiers.Shift) && key == Key.Escape ||
        modifiers.HasFlag(OverlayHotkeyModifiers.Control) &&
        modifiers.HasFlag(OverlayHotkeyModifiers.Alt) && key == Key.Delete ||
        modifiers.HasFlag(OverlayHotkeyModifiers.Windows) && key is Key.L or Key.D or Key.Tab ||
        key is Key.F12 or Key.PrintScreen or Key.CapsLock or Key.NumLock or Key.Scroll or Key.Pause;

    private static string KeyName(Key key)
    {
        if ((int)key >= (int)Key.D0 && (int)key <= (int)Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }
        if ((int)key >= (int)Key.NumPad0 && (int)key <= (int)Key.NumPad9)
        {
            return $"Num {(int)key - (int)Key.NumPad0}";
        }
        return key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            _ => key.ToString()
        };
    }
}

internal readonly record struct OverlayHotkeyRegistrationResult(bool Succeeded, string Error)
{
    internal static OverlayHotkeyRegistrationResult Success { get; } = new(true, string.Empty);
}

internal sealed class OverlayHotkeyService : IDisposable
{
    private const int HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;
    private const int FirstHotkeyId = 0x5750;
    private const int SecondHotkeyId = 0x5751;
    private readonly HwndSource _window;
    private int _registeredId;
    private OverlayHotkeyChord? _registeredChord;
    private bool _disposed;

    internal OverlayHotkeyService()
    {
        _window = new HwndSource(new HwndSourceParameters("Wisp overlay hotkey")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3),
            HwndSourceHook = OnWindowMessage
        });
    }

    internal event EventHandler? Pressed;

    internal OverlayHotkeyRegistrationResult Apply(bool enabled, OverlayHotkeyChord chord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!enabled)
        {
            UnregisterCurrent();
            return OverlayHotkeyRegistrationResult.Success;
        }

        if (!OverlayHotkeyChord.TryCreate(chord.Modifiers, chord.Key, out chord, out var validationError))
        {
            return new OverlayHotkeyRegistrationResult(false, validationError);
        }

        if (_registeredChord == chord)
        {
            return OverlayHotkeyRegistrationResult.Success;
        }

        var nextId = _registeredId == FirstHotkeyId ? SecondHotkeyId : FirstHotkeyId;
        var nativeModifiers = (uint)chord.Modifiers | NoRepeat;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(chord.Key);
        if (!RegisterHotKey(_window.Handle, nextId, nativeModifiers, virtualKey))
        {
            var win32 = new Win32Exception(Marshal.GetLastWin32Error());
            return new OverlayHotkeyRegistrationResult(
                false,
                win32.NativeErrorCode == 1409
                    ? "another app is already using it"
                    : "Windows refused the shortcut");
        }

        // Keep the known-working shortcut registered until its replacement is
        // secured. A conflict therefore cannot strand the user without a key.
        if (_registeredId != 0)
        {
            _ = UnregisterHotKey(_window.Handle, _registeredId);
        }
        _registeredId = nextId;
        _registeredChord = chord;
        return OverlayHotkeyRegistrationResult.Success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        UnregisterCurrent();
        _window.Dispose();
    }

    private void UnregisterCurrent()
    {
        if (_registeredId != 0)
        {
            _ = UnregisterHotKey(_window.Handle, _registeredId);
        }
        _registeredId = 0;
        _registeredChord = null;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == HotkeyMessage && wParam.ToInt32() == _registeredId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
