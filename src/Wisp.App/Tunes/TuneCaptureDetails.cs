using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Wisp.Core;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

internal enum TuneCaptureStage { OpenGame, VerifySession, ReadTune, DecodeTune, VerifyCurrent }

internal static class TuneCaptureDetails
{
    private static readonly HashSet<string> SafeMessages = new(StringComparer.Ordinal)
    {
        "Tune read exceeded its bounds.",
        "The current car could not be read.",
        "The current car structure is unavailable.",
        "The tune contains an invalid value.",
        "The tune contains an invalid conversion.",
        "The current car's tune is not available yet.",
        "The game's tuning metadata could not be verified.",
        "The tune layout is incomplete.",
        "The required tuning table schema does not match the supported build.",
        "The required tuning values do not match the supported build.",
        "The required tuning projection is missing.",
        "The required tuning projection has an invalid row count.",
        "The required tuning values contain an invalid or duplicate identity.",
        "The car name token is unavailable.",
        "The car or tune changed while reading. Refresh and try again."
    };
    private static readonly HashSet<string> SqliteChecks = new(StringComparer.Ordinal)
    {
        "sqlite-query-only-failed", "sqlite-query-only-not-enabled", "required-table-missing",
        "required-table-not-ordinary", "required-table-integrity-failed", "projection-row-limit",
        "projection-null-identity", "projection-duplicate-identity", "sqlite-close-failed",
        "sqlite-borrowed-buffer-changed", "name-schema-unavailable", "name-integrity-unavailable",
        "name-identity-unavailable", "projection-not-integer", "projection-integer-overflow", "sqlite-text-invalid"
    };
    private static readonly HashSet<string> SqliteOperations = new(StringComparer.Ordinal)
    {
        "sqlite-open", "sqlite-disable-extensions", "sqlite-defensive", "sqlite-untrusted-schema",
        "sqlite-readonly-deserialize", "sqlite-authorizer", "sqlite-read-rows", "name-read", "sqlite-prepare"
    };
    private static readonly HashSet<Type> SafeMethodOwners =
    [
        typeof(NativeTuneRead), typeof(NativeTuneCapture), typeof(NativeTuneLayout),
        typeof(TuneAssetCapture), typeof(TuneAssetSqlite), typeof(TuneAssetContract),
        typeof(TuneDecoder), typeof(TuneCaptureService)
    ];

    internal static string Create(TuneCaptureStage stage, NativeHudCompatibilityPack? build = null,
        Exception? exception = null, NativeAssistProviderStatus? providerStatus = null,
        TuneDecodeFailure? decodeFailure = null)
    {
        var report = new StringBuilder("Wisp Tune read details\n");
        report.AppendLine($"Wisp: {ApplicationVersionInfo.MachineVersion}");
        if (ApplicationVersionInfo.DiagnosticBuildId is { Length: > 0 } id)
            report.AppendLine($"Private build: {SafeBuildId(id)}");
        report.AppendLine($"Game platform: {(build is null ? "Not verified" : build.StoreIdentity is null ? "Steam" : "Xbox/Store")}");
        report.AppendLine($"Game version: {(build is null ? "Not verified" : SafeVersion(build.GameVersion))}");
        report.AppendLine($"Stage: {KnownEnum(stage)}");
        if (providerStatus is { } provider) report.AppendLine($"Open status: {KnownEnum(provider)}");
        if (decodeFailure is { } decode) report.AppendLine($"Decode status: {KnownEnum(decode)}");
        if (exception is null)
        {
            report.AppendLine("Error type: Validation result");
            report.AppendLine($"Method: {(stage == TuneCaptureStage.DecodeTune ? "TuneDecoder.TryDecode" : "TuneCaptureService.CaptureAsync")}");
        }
        else
        {
            report.AppendLine($"Error type: {SafeType(exception)}");
            report.AppendLine($"Error code: 0x{exception.HResult.ToString("X8", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Message: {SafeMessage(exception)}");
            report.AppendLine($"Method: {SafeMethod(exception)}");
            if (exception is Win32Exception native)
                report.AppendLine(FormattableString.Invariant($"Native error code: {native.NativeErrorCode}"));
            if (exception is TuneLayoutException layout)
                report.AppendLine($"Layout check: {KnownEnum(layout.Failure)}");
            if (exception is TuneCarSelectionException car)
            {
                report.AppendLine($"Car selection: {KnownEnum(car.Failure)}");
                report.AppendLine(FormattableString.Invariant($"Verified local car candidates: {car.CandidateCount}"));
            }
            if (exception is TuneAssetValidationException asset)
            {
                report.AppendLine($"Asset check: {KnownEnum(asset.FailureCode)}");
                report.AppendLine(FormattableString.Invariant($"Actual asset size (bytes): {asset.ActualSizeBytes}"));
                report.AppendLine(FormattableString.Invariant($"Maximum asset size (bytes): {asset.MaximumSizeBytes}"));
                if (asset.ActualPageCount is { } pages)
                    report.AppendLine(FormattableString.Invariant($"Actual page count: {pages}"));
            }
            if (exception is TuneAssetStreamValidationException stream)
            {
                report.AppendLine(FormattableString.Invariant($"Stream flag: {stream.Flag}"));
                report.AppendLine(FormattableString.Invariant($"Chunk size (bytes): {stream.ChunkSize}"));
                report.AppendLine(FormattableString.Invariant($"Allocated size (bytes): {stream.Allocated}"));
                report.AppendLine(FormattableString.Invariant($"Chunk vector size (bytes): {stream.VectorBytes}"));
            }
        }
        var details = report.ToString().TrimEnd();
        DebugLogging.ComponentDiagnosticHistory.Current.RecordGenerated(DebugLogging.DiagnosticComponent.Tune, details);
        return details;
    }

    private static string KnownEnum<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value) ? value.ToString() : "Unknown";

    private static string SafeBuildId(string value) => value.Length is >= 6 and <= 80 &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ? value : "Not available";

    private static string SafeVersion(string value) => value.Length <= 32 &&
        value.All(character => character is >= '0' and <= '9' or '.') && Version.TryParse(value, out _) ? value : "Not verified";

    private static string SafeType(Exception error) => error switch
    {
        TuneAssetStreamValidationException => nameof(TuneAssetStreamValidationException),
        TuneAssetValidationException => nameof(TuneAssetValidationException),
        TuneLayoutException => nameof(TuneLayoutException),
        TuneCarSelectionException => nameof(TuneCarSelectionException),
        TuneChangedException => nameof(TuneChangedException),
        InvalidDataException => nameof(InvalidDataException),
        UnauthorizedAccessException => nameof(UnauthorizedAccessException),
        Win32Exception => nameof(Win32Exception),
        DllNotFoundException => nameof(DllNotFoundException),
        EntryPointNotFoundException => nameof(EntryPointNotFoundException),
        ArgumentException => nameof(ArgumentException),
        InvalidOperationException => nameof(InvalidOperationException),
        OverflowException => nameof(OverflowException),
        IOException => nameof(IOException),
        _ => "Exception"
    };

    private static string SafeMessage(Exception error) => error switch
    {
        TuneAssetValidationException => "The tuning asset did not pass validation.",
        TuneLayoutException => "The tune layout did not match the supported build.",
        TuneCarSelectionException => "The current car could not be selected.",
        _ => SafeMessages.Contains(error.Message) ? error.Message : SafeSqliteMessage(error.Message) ??
            "Message omitted because it may contain private information."
    };

    private static string? SafeSqliteMessage(string message)
    {
        const string prefix = "Local tuning metadata could not be verified (";
        if (!message.StartsWith(prefix, StringComparison.Ordinal) || !message.EndsWith(").", StringComparison.Ordinal)) return null;
        var stage = message[prefix.Length..^2];
        if (SqliteChecks.Contains(stage)) return $"{prefix}{stage}).";
        var separator = stage.LastIndexOf("-code-", StringComparison.Ordinal);
        if (separator < 0 || !SqliteOperations.Contains(stage[..separator]) ||
            !int.TryParse(stage.AsSpan(separator + 6), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)) return null;
        return FormattableString.Invariant($"{prefix}{stage[..separator]}-code-{code}).");
    }

    private static string SafeMethod(Exception error)
    {
        string? helper = null;
        foreach (var frame in new StackTrace(error, false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType is { } owner && SafeMethodOwners.Contains(owner) &&
                method.Name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                var symbol = $"{owner.Name}.{method.Name}";
                if (method.Name is "Require" or "Fail" or "Check") helper ??= symbol;
                else return symbol;
            }
        }
        return helper ?? "TuneCaptureService.CaptureAsync";
    }
}
