using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal sealed class ForeignWindowOwner : IDisposable
{
    private readonly Process _process;
    internal IntPtr Handle { get; private set; }

    private ForeignWindowOwner(Process process) => _process = process;

    internal static ForeignWindowOwner Start()
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(OwnerScript)));
        var owner = new ForeignWindowOwner(Process.Start(start) ??
            throw new InvalidOperationException("The foreign window owner did not start."));
        try
        {
            var ready = owner._process.StandardOutput.ReadLineAsync();
            PumpUntil(() => ready.IsCompleted, 10000);
            Assert.True(long.TryParse(ready.GetAwaiter().GetResult(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var handle), "The foreign window owner did not return its HWND.");
            owner.Handle = new IntPtr(handle);
            Assert.True(IsWindow(owner.Handle));
            Assert.False(IsWindowVisible(owner.Handle));
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    internal void Exit(bool closeWindow)
    {
        _process.StandardInput.WriteLine(closeWindow ? "close" : "exit");
        _process.StandardInput.Flush();
        PumpUntil(() => _process.HasExited, 5000);
        Assert.Equal(0, _process.ExitCode);
        Assert.False(IsWindow(Handle));
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                PumpUntil(() => _process.HasExited, 5000);
            }
        }
        finally { _process.Dispose(); }
    }

    private static void PumpUntil(Func<bool> predicate, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < milliseconds)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(5);
        }
        Assert.True(predicate(), "The foreign window owner did not complete its bounded lifecycle operation.");
    }

    // A real second process is essential: closing a same-process owner destroys
    // its owned windows, whereas game exit clears their Win32 owner handles.
    private const string OwnerScript = """
        $ErrorActionPreference = 'Stop'
        Add-Type -TypeDefinition @'
        using System;
        using System.ComponentModel;
        using System.Globalization;
        using System.Runtime.InteropServices;
        using System.Threading;
        using System.Threading.Tasks;
        public static class WispTestWindowOwner
        {
            public static void Run()
            {
                var window = CreateWindowEx(0x08000080, "STATIC", "", 0x00CF0000,
                    0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    Console.WriteLine(window.ToInt64().ToString(CultureInfo.InvariantCulture));
                    Console.Out.Flush();
                    var input = Task.Run(() => Console.ReadLine());
                    while (!input.IsCompleted)
                    {
                        Message message;
                        while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1))
                        {
                            TranslateMessage(ref message);
                            DispatchMessage(ref message);
                        }
                        Thread.Sleep(5);
                    }
                    if (input.Result == "exit") Environment.Exit(0);
                    if (input.Result != "close") throw new InvalidOperationException("Unknown owner command.");
                }
                finally { DestroyWindow(window); }
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct Message
            {
                public IntPtr Window;
                public uint Value;
                public UIntPtr Word;
                public IntPtr Data;
                public uint Time;
                public int X, Y;
                public uint Private;
            }
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowEx(uint extended, string className, string title,
                uint style, int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool DestroyWindow(IntPtr window);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool TranslateMessage(ref Message message);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr DispatchMessage(ref Message message);
        }
        '@
        [WispTestWindowOwner]::Run()
        """;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
