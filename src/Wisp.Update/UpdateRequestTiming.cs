namespace Wisp.Update;

// Where one request of the release check or installer download spent its time before its response
// deadline. Connect is name resolution and the TCP connection of a connection this request opened, Tls
// is that connection's TLS handshake (with its certificate revocation check), and Response is the wait
// for the response headers after that, or from the start of the request when it opened no connection.
// A phase that had not begun is null; the last phase present is the one the deadline interrupted.
public sealed record UpdateRequestTiming(
    string Request,
    TimeSpan Elapsed,
    TimeSpan? Connect,
    TimeSpan? Tls,
    TimeSpan? Response)
{
    private const string DataKey = "Wisp.Update.RequestTiming";

    // The timing of the request whose response deadline produced this exception, if any.
    public static UpdateRequestTiming? From(Exception error) =>
        error.Data[DataKey] as UpdateRequestTiming;

    public static void Attach(Exception error, UpdateRequestTiming timing)
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
    private long _ready = NotSet;

    internal void ConnectStarted() => Interlocked.CompareExchange(ref _connectStarted, clock.GetTimestamp(), NotSet);

    internal void Connected() => Interlocked.CompareExchange(ref _connected, clock.GetTimestamp(), NotSet);

    // The connection this request opened finished its TLS handshake and is ready for HTTP.
    internal void Ready() => Interlocked.CompareExchange(ref _ready, clock.GetTimestamp(), NotSet);

    internal UpdateRequestTiming Snapshot()
    {
        var now = clock.GetTimestamp();
        var connectStarted = Interlocked.Read(ref _connectStarted);
        var connected = Interlocked.Read(ref _connected);
        var ready = Interlocked.Read(ref _ready);
        TimeSpan? Phase(long start, long end) => start == NotSet ? null : clock.GetElapsedTime(start, end == NotSet ? now : end);
        return connectStarted == NotSet
            ? new(request, clock.GetElapsedTime(_started, now), null, null, clock.GetElapsedTime(_started, now))
            : new(request, clock.GetElapsedTime(_started, now),
                Phase(connectStarted, connected),
                connected == NotSet ? null : Phase(connected, ready),
                ready == NotSet ? null : Phase(ready, NotSet));
    }
}
