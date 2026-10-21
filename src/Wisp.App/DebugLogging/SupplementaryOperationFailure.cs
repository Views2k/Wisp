using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;
using Wisp.App.CrashDiagnostics;
using Wisp.App.Runs;
using Wisp.App.Clips;
using Wisp.App.Supplementary;
using Wisp.Update;

namespace Wisp.App.DebugLogging;

internal static class SupplementaryOperationFailure
{
    internal static SupplementaryIncident Receiver(Wisp.Telemetry.ReceiverFailure failure) =>
        SupplementaryIncidentSchema.Create("telemetry", failure.IsSocketError ? failure.Code switch
        {
            10048 => "port-in-use",
            10013 => "access-denied",
            _ => "socket-failed"
        } : "managed-operation-failed", "telemetry-ready", unchecked((uint)failure.Code),
            [new(failure.IsSocketError ? "SocketException" : "Exception", unchecked((uint)failure.Code), [])]);

    internal static SupplementaryIncident? Capture(string feature, string stage, Exception error, DateTimeOffset now)
    {
        try
        {
            var failureStage = $"{feature}-{stage}";
            if (!SupplementaryIncidentSchema.FailureStages.Contains(failureStage)) return null;
            var category = feature switch
            {
                "update" => "update",
                "settings" => "settings",
                "setup" or "telemetry" => "telemetry",
                "clips" when stage is "decode" or "playback" => "playback",
                _ => "saved-data"
            };
            var code = error switch
            {
                LosslessMpvException { NativeErrorCode: { } native } => unchecked((uint)native),
                SocketException socket => unchecked((uint)socket.NativeErrorCode),
                Win32Exception win32 => unchecked((uint)win32.NativeErrorCode),
                HttpRequestException { StatusCode: { } status } => (uint)status,
                _ => unchecked((uint)error.HResult)
            };
            return SupplementaryIncidentSchema.Create(category, Reason(error), failureStage, code,
                [new(CrashReportSymbols.ExceptionType(error.GetType()), unchecked((uint)error.HResult), [])],
                SupplementaryIncidentCapture.Context(HealthContextRecorder.Current.Snapshot(), now));
        }
        catch (Exception) { return null; }
    }

    internal static string Reason(Exception error) => error switch
    {
        LosslessMpvException { Code: "packet-queue-full" or "event-queue-overflow" or "event-batch-limit" } => "media-queue-full",
        LosslessMpvException { Code: "decode-failed" } => "invalid-data",
        LosslessMpvException => "native-failure",
        UpdateSecurityException => "update-verification-failed",
        UpdateRateLimitedException => "rate-limited",
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => "port-in-use",
        SocketException { SocketErrorCode: SocketError.AccessDenied } => "access-denied",
        SocketException => "socket-failed",
        HttpRequestException { StatusCode: not null } => "http-status",
        HttpRequestException http => http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "network-name-resolution",
            HttpRequestError.ConnectionError => "network-connection",
            HttpRequestError.SecureConnectionError => "network-tls",
            HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse or
                HttpRequestError.ResponseEnded => "network-response",
            _ => "network-request"
        },
        TimeoutException or OperationCanceledException => "timeout",
        UnauthorizedAccessException or SecurityException => "access-denied",
        RunLibraryFullException => "library-full",
        FileNotFoundException or DirectoryNotFoundException => "file-unavailable",
        PathTooLongException => "path-too-long",
        InvalidDataException or JsonException or FormatException => "invalid-data",
        IOException when (error.HResult & 0xffff) is 39 or 112 => "disk-full",
        IOException when (error.HResult & 0xffff) == 5 => "access-denied",
        IOException when (error.HResult & 0xffff) is 32 or 33 => "file-in-use",
        IOException => "storage",
        ArgumentException => "validation",
        _ => "managed-operation-failed"
    };
}
