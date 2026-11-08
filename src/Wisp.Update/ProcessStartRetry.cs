using System.ComponentModel;

namespace Wisp.Update;

// Windows can refuse to start a freshly staged executable for a moment with access denied (5) or a
// sharing violation (32). On 2.7.3 one PC was refused four times in six starts of the update helper
// and the installer, and each start that was tried again later succeeded. A refused start is tried
// again after 1, 2, 4 and 8 s, about 15 s in all; any other failure is not retried.
public static class ProcessStartRetry
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    public static IReadOnlyList<TimeSpan> Delays { get; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    public static bool IsTransient(Exception exception) =>
        exception is Win32Exception { NativeErrorCode: ErrorAccessDenied or ErrorSharingViolation };

    // start runs again after each delay while canContinue allows; the last refusal is rethrown.
    public static T Start<T>(Func<T> start, Func<bool> canContinue, Action<TimeSpan> wait)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(canContinue);
        ArgumentNullException.ThrowIfNull(wait);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return start();
            }
            catch (Exception exception) when (attempt < Delays.Count && IsTransient(exception))
            {
                wait(Delays[attempt]);
                if (!canContinue()) throw;
            }
        }
    }

    public static async Task<T> StartAsync<T>(Func<T> start, Func<bool> canContinue, Func<TimeSpan, Task> wait)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(canContinue);
        ArgumentNullException.ThrowIfNull(wait);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return start();
            }
            catch (Exception exception) when (attempt < Delays.Count && IsTransient(exception))
            {
                await wait(Delays[attempt]).ConfigureAwait(true);
                if (!canContinue()) throw;
            }
        }
    }
}
