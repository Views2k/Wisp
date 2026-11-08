namespace Wisp.Update;

// Whether a request opened its own connection and how far that connection got.
public enum UpdateConnectionState
{
    // The request opened no connection: an open one served it, or it was still waiting for one.
    None,
    // Name resolution or the TCP connection had not completed.
    Connecting,
    // The TCP connection completed; ConnectDuration says how long it took.
    Connected
}

// Where one request of the release check or installer download spent its time before it failed.
// ConnectDuration covers name resolution and the TCP connection; the rest of Elapsed is the TLS
// handshake, including its certificate revocation check, and the wait for the response headers.
public sealed record UpdateRequestTiming(
    string Request,
    TimeSpan Elapsed,
    UpdateConnectionState Connection,
    TimeSpan? ConnectDuration)
{
    private const string DataKey = "Wisp.Update.RequestTiming";

    // The timing of the request whose response deadline produced this exception, if any.
    public static UpdateRequestTiming? From(Exception error) =>
        error.Data[DataKey] as UpdateRequestTiming;

    internal static void Attach(Exception error, UpdateRequestTiming timing)
    {
        try { error.Data[DataKey] = timing; }
        catch (ArgumentException) { /* Diagnostics never replace the original failure. */ }
    }
}

internal sealed class UpdateRequestTimer(string request, TimeProvider clock)
{
    private const long NotSet = long.MinValue;
    private readonly long _started = clock.GetTimestamp();
    private long _connectStarted = NotSet;
    private long _connected = NotSet;

    internal void ConnectStarted() => Interlocked.CompareExchange(ref _connectStarted, clock.GetTimestamp(), NotSet);

    internal void Connected() => Interlocked.CompareExchange(ref _connected, clock.GetTimestamp(), NotSet);

    internal UpdateRequestTiming Snapshot()
    {
        var connectStarted = Interlocked.Read(ref _connectStarted);
        var connected = Interlocked.Read(ref _connected);
        return new UpdateRequestTiming(
            request,
            clock.GetElapsedTime(_started),
            connectStarted == NotSet ? UpdateConnectionState.None
                : connected == NotSet ? UpdateConnectionState.Connecting
                : UpdateConnectionState.Connected,
            connectStarted != NotSet && connected != NotSet ? clock.GetElapsedTime(connectStarted, connected) : null);
    }
}
