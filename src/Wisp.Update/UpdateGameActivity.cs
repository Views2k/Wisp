using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace Wisp.Update;

public static class UpdateGameActivity
{
    public static bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("ForzaHorizon6");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or SecurityException)
        {
            return true;
        }
    }
}
