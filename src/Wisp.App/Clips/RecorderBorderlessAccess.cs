using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Wisp.App.Clips;

internal sealed class RecorderBorderlessAccess(string helperPath, Func<ProcessStartInfo, IThumbnailChild>? launch = null)
{
    private static IThumbnailChild? _unconfirmed;
    private readonly Func<ProcessStartInfo, IThumbnailChild> _launch = launch ?? (start => new ThumbnailProcessChild(start));

    internal async Task<ClipBorderlessAccessResult> RequestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _unconfirmed) is not null) return ClipBorderlessAccessResult.Unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        IThumbnailChild? child = null;
        Task[] work = [];
        var exited = false;
        try
        {
            var start = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helperPath)!
            };
            start.ArgumentList.Add("--borderless-access-stdio");
            child = _launch(start);
            if (!child.IsReady) return ClipBorderlessAccessResult.Unavailable;
            var response = ReadBoundedAsync(child.Output, 512, deadline.Token);
            var errors = ReadBoundedAsync(child.Error, 1024, deadline.Token);
            var exit = child.WaitForExitAsync(deadline.Token);
            work = [response, errors, exit];
            await child.Input.WriteAsync("request-borderless-v1\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
            await child.Input.FlushAsync(deadline.Token).ConfigureAwait(false);
            child.Input.Close();
            await Task.WhenAll(work).ConfigureAwait(false);
            exited = true;
            return child.ExitCode == 0 ? Parse(await response.ConfigureAwait(false)) : ClipBorderlessAccessResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return ClipBorderlessAccessResult.Unavailable; }
        finally
        {
            deadline.Cancel();
            _ = Task.WhenAll(work).ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (child is not null)
            {
                if (!exited)
                {
                    try { child.Kill(); } catch (Exception error) when (error is not OutOfMemoryException) { }
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await child.WaitForExitAsync(stop.Token).ConfigureAwait(false); exited = true; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
                if (exited) child.Dispose();
                else Interlocked.CompareExchange(ref _unconfirmed, child, null);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        var bytes = new byte[maximum + 1];
        var length = 0;
        while (true)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) return bytes[..length];
            length += count;
            if (length > maximum) throw new InvalidDataException("The capture permission response is invalid.");
        }
    }

    internal static ClipBorderlessAccessResult Parse(ReadOnlyMemory<byte> response)
    {
        if (response.Length is 0 or > 512) return ClipBorderlessAccessResult.Unavailable;
        try
        {
            using var document = JsonDocument.Parse(response, new() { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ClipBorderlessAccessResult.Unavailable;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return ClipBorderlessAccessResult.Unavailable;
            if (!names.SetEquals(["v", "mode", "status"]) || root.GetProperty("v").GetInt32() != 1 ||
                root.GetProperty("mode").GetString() != "borderless_access") return ClipBorderlessAccessResult.Unavailable;
            return root.GetProperty("status").GetString() switch
            {
                "allowed" => ClipBorderlessAccessResult.Allowed,
                "denied" => ClipBorderlessAccessResult.Denied,
                _ => ClipBorderlessAccessResult.Unavailable
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { return ClipBorderlessAccessResult.Unavailable; }
    }
}
