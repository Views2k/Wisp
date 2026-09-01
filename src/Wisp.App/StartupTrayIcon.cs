using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Wisp.App;

internal sealed class StartupTrayIcon : IDisposable
{
    private const uint AddIcon = 0;
    private const uint ModifyIcon = 1;
    private const uint DeleteIcon = 2;
    private const int TrayMessage = 0x8001;
    private const int LeftButtonUp = 0x0202;
    private const int RightButtonUp = 0x0205;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly ContextMenu _menu;
    private HwndSource? _window;
    private NotifyIconData _data;
    private IntPtr _icon;
    private bool _ownsIcon;
    private bool _waiting = true;
    private bool _disposed;

    internal StartupTrayIcon()
    {
        var open = new MenuItem { Header = CreateMenuHeader("Open Wisp") };
        var exit = new MenuItem { Header = CreateMenuHeader("Exit Wisp") };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Items.Add(open);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);

        try
        {
            var smallIcons = new IntPtr[1];
            if (Environment.ProcessPath is { } path &&
                ExtractIconEx(path, 0, null, smallIcons, 1) > 0)
            {
                _icon = smallIcons[0];
                _ownsIcon = _icon != IntPtr.Zero;
            }
            if (_icon == IntPtr.Zero)
            {
                _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
            }

            // A private, invisible Wisp message window handles only its own
            // notification-area callbacks. No game/global window hook is used.
            _window = new HwndSource(new HwndSourceParameters("Wisp startup companion")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                HwndSourceHook = OnWindowMessage
            });
            _data = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                Window = _window.Handle,
                Id = 1,
                Flags = 1 | 2 | 4,
                CallbackMessage = TrayMessage,
                Icon = _icon,
                Tip = "Wisp - Waiting for Forza",
                Info = string.Empty,
                InfoTitle = string.Empty
            };
            IsAvailable = ShellNotifyIcon(AddIcon, ref _data);
            if (!IsAvailable)
            {
                throw new Win32Exception("Windows could not create Wisp's notification-area icon.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal event EventHandler? OpenRequested;
    internal event EventHandler? ExitRequested;
    internal bool IsAvailable { get; private set; }

    internal static TextBlock CreateMenuHeader(string text)
    {
        var header = new TextBlock { Text = text };
        // The app-wide TextBlock style is intentionally light for Wisp's dark
        // window. A tray popup follows Windows menu colors and may be light,
        // so its label must follow the system menu color instead of inheriting that style.
        header.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.MenuTextBrushKey);
        return header;
    }

    internal void SetWaiting(bool waiting)
    {
        if (_disposed || _waiting == waiting)
        {
            return;
        }

        _waiting = waiting;
        _data.Tip = waiting ? "Wisp - Waiting for Forza" : "Wisp - Forza companion enabled";
        if (IsAvailable)
        {
            _ = ShellNotifyIcon(ModifyIcon, ref _data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _menu.IsOpen = false;
        if (IsAvailable)
        {
            _ = ShellNotifyIcon(DeleteIcon, ref _data);
            IsAvailable = false;
        }
        _window?.Dispose();
        _window = null;
        if (_ownsIcon)
        {
            _ = DestroyIcon(_icon);
            _ownsIcon = false;
        }
        _icon = IntPtr.Zero;
    }

    private IntPtr OnWindowMessage(
        IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
        {
            return IntPtr.Zero;
        }

        if ((uint)message == _taskbarCreated && _taskbarCreated != 0)
        {
            IsAvailable = ShellNotifyIcon(AddIcon, ref _data);
        }
        else if (message == TrayMessage)
        {
            handled = true;
            var action = unchecked((int)lParam.ToInt64());
            if (action == LeftButtonUp)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (action == RightButtonUp)
            {
                _ = SetForegroundWindow(window);
                _menu.IsOpen = true;
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string path, int index, [Out] IntPtr[]? largeIcons, [Out] IntPtr[]? smallIcons, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
