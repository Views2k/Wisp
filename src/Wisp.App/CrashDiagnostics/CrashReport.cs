using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.App.DebugLogging;
using Wisp.Core;
using Wisp.Telemetry;
using Wisp.Update;

namespace Wisp.App.CrashDiagnostics;

internal enum CrashOrigin { UiDispatcher, BackgroundThread, UnobservedTask, UnexpectedExit, WindowsApplicationFault }
internal sealed record CrashExceptionInfo(string Type, int HResult, string[] Methods);
internal sealed record CrashReport(Guid Id, DateTimeOffset TimeUtc, string WispVersion, string? PrivateBuildId,
    string RuntimeVersion, CrashOrigin Origin, bool IsTerminating, CrashExceptionInfo[] Exceptions)
{
    internal const int MaximumExceptions = 6;
    internal const int MaximumMethods = 8;
    public Guid? ModuleVersionId { get; init; }
    public string WindowsVersion { get; init; } = "Not available";
    public Architecture? ProcessArchitecture { get; init; }
    public Guid? RunId { get; init; }
    public RecoveredExitInfo? RecoveredExit { get; init; }
    public HealthContextSnapshot? Context { get; init; }

    internal static CrashReport Capture(Exception? exception, CrashOrigin origin, bool terminating, DateTimeOffset now)
    {
        var errors = new List<CrashExceptionInfo>();
        var pending = new Queue<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        if (exception is not null) pending.Enqueue(exception);
        while (pending.Count > 0 && errors.Count < MaximumExceptions)
        {
            var error = pending.Dequeue();
            if (!seen.Add(error)) continue;
            errors.Add(new(CrashReportSymbols.ExceptionType(error.GetType()), error.HResult,
                new StackTrace(error, false).GetFrames().Take(64)
                    .Select(frame => CrashReportSymbols.Method(frame.GetMethod())).OfType<string>()
                    .Distinct(StringComparer.Ordinal).Take(MaximumMethods).ToArray()));
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(MaximumExceptions)) pending.Enqueue(inner);
            }
            else if (error.InnerException is { } inner) pending.Enqueue(inner);
        }
        return new(Guid.NewGuid(), now.ToUniversalTime(), ApplicationVersionInfo.MachineVersion,
            ApplicationVersionInfo.DiagnosticBuildId, Environment.Version.ToString(), origin, terminating, errors.ToArray())
        {
            ModuleVersionId = typeof(App).Assembly.ManifestModule.ModuleVersionId,
            WindowsVersion = Environment.OSVersion.Version.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture
        };
    }

    // Revalidate disk content before showing or copying it, even though only safe fields are written.
    internal CrashReport? Sanitize()
    {
        if (Id == Guid.Empty || !Enum.IsDefined(Origin) || Exceptions is null || Exceptions.Length > MaximumExceptions) return null;
        var exit = RecoveredExit?.Sanitize();
        if (Origin is CrashOrigin.UnexpectedExit or CrashOrigin.WindowsApplicationFault &&
            (exit is null || RunId is null || RunId == Guid.Empty ||
             (Origin == CrashOrigin.WindowsApplicationFault) != (exit.Fault is not null))) return null;
        var exceptions = Exceptions.Where(error => error is not null).Select(error => new CrashExceptionInfo(
            CrashReportSymbols.SafeExceptionType(error.Type), error.HResult,
            (error.Methods ?? []).Take(MaximumMethods).Where(CrashReportSymbols.IsKnownMethod).ToArray())).ToArray();
        return this with
        {
            TimeUtc = TimeUtc.ToUniversalTime(),
            WispVersion = SafeVersion(WispVersion),
            RuntimeVersion = SafeVersion(RuntimeVersion),
            PrivateBuildId = PrivateBuildId is { Length: >= 6 and <= 80 } id &&
                id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ? id : null,
            ModuleVersionId = ModuleVersionId is { } moduleId && moduleId != Guid.Empty ? moduleId : null,
            WindowsVersion = SafeVersion(WindowsVersion),
            ProcessArchitecture = ProcessArchitecture is { } architecture && Enum.IsDefined(architecture) ? architecture : null,
            RunId = RunId is { } runId && runId != Guid.Empty ? runId : null,
            RecoveredExit = Origin is CrashOrigin.UnexpectedExit or CrashOrigin.WindowsApplicationFault ? exit : null,
            Exceptions = exceptions,
            Context = HealthContextRecorder.IsValidSnapshot(Context) ? Context : null
        };
    }

    internal string Format()
    {
        var safe = Sanitize();
        if (safe is null) return "Wisp error details are not available.";
        var report = new StringBuilder("Wisp error report\n");
        report.AppendLine($"UTC: {safe.TimeUtc.ToString("O", CultureInfo.InvariantCulture)}");
        report.AppendLine($"Wisp: {safe.WispVersion}");
        if (safe.PrivateBuildId is not null) report.AppendLine($"Private build: {safe.PrivateBuildId}");
        report.AppendLine($"Assembly module: {safe.ModuleVersionId?.ToString("D") ?? "Not available"}");
        report.AppendLine($"CLR: {safe.RuntimeVersion}");
        report.AppendLine($"Windows: {safe.WindowsVersion}");
        report.AppendLine($"Process architecture: {safe.ProcessArchitecture?.ToString() ?? "Not available"}");
        report.AppendLine($"Origin: {safe.Origin}");
        report.AppendLine($"Process terminating: {(safe.IsTerminating ? "Yes" : "No")}");
        if (safe.RecoveredExit is { } exit)
        {
            report.AppendLine($"Previous run started UTC: {exit.RunStartedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Exit detected UTC: {exit.DetectedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Windows fault lookup: {exit.LookupStatus}");
            if (exit.Fault is { } fault)
            {
                report.AppendLine("Windows recorded an application crash for this process instance.");
                report.AppendLine($"Windows exception code: 0x{fault.ExceptionCode.ToString("X8", CultureInfo.InvariantCulture)}");
                report.AppendLine($"Fault module: {fault.Module ?? "Not available"}");
                report.AppendLine($"Fault offset: {(fault.FaultOffset is { } offset ? "0x" + offset.ToString("X", CultureInfo.InvariantCulture) : "Not available")}");
                report.AppendLine($"Matching WER report: {(fault.CorrelatedWerReport ? "Yes" : "Not available")}");
                report.AppendLine("The faulting module alone does not establish the root cause.");
            }
            else report.AppendLine("The previous run did not finish normal shutdown. The cause is not established; termination or power loss can also leave this marker.");
        }
        for (var i = 0; i < safe.Exceptions.Length; i++)
        {
            var error = safe.Exceptions[i];
            report.AppendLine($"Exception {i + 1}: {error.Type}");
            report.AppendLine($"HRESULT: 0x{error.HResult.ToString("X8", CultureInfo.InvariantCulture)}");
            if (error.Methods.Length == 0) report.AppendLine("Product methods: Not available");
            else foreach (var method in error.Methods) report.AppendLine($"Method: {method}");
        }
        if (safe.Exceptions.Length == 0) report.AppendLine("Exception: Not available");
        if (safe.Context is { } context)
        {
            report.AppendLine("Recent health context (sampled every two seconds):");
            report.AppendLine("Composition callbacks and renderer submissions are not displayed-frame measurements.");
            report.AppendLine(JsonSerializer.Serialize(context, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            }));
        }
        else report.AppendLine("Recent health context: Not available");
        return report.ToString().TrimEnd();
    }

    private static string SafeVersion(string? value) => value is { Length: > 0 and <= 32 } &&
        value.All(character => character is >= '0' and <= '9' or '.') && Version.TryParse(value, out _) ? value : "Not available";
}

internal static class CrashReportSymbols
{
    private static readonly Assembly[] ProductAssemblies =
        [typeof(App).Assembly, typeof(VehicleState).Assembly, typeof(TelemetryUdpReceiver).Assembly, typeof(WispUpdateClient).Assembly];
    private static readonly HashSet<string> ExceptionTypes = new(StringComparer.Ordinal)
    {
        nameof(Exception), nameof(AggregateException), nameof(ArgumentException), nameof(ArgumentNullException),
        nameof(ArgumentOutOfRangeException), nameof(InvalidOperationException), nameof(ObjectDisposedException),
        nameof(NullReferenceException), nameof(IndexOutOfRangeException), nameof(InvalidCastException),
        nameof(NotSupportedException), nameof(NotImplementedException), nameof(FormatException), nameof(OverflowException),
        nameof(IOException), nameof(InvalidDataException), nameof(FileNotFoundException), nameof(DirectoryNotFoundException),
        nameof(FileLoadException), nameof(UnauthorizedAccessException), nameof(Win32Exception), nameof(SocketException),
        nameof(COMException), nameof(SEHException), nameof(AccessViolationException), nameof(OutOfMemoryException),
        nameof(StackOverflowException), nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(TimeoutException),
        nameof(DllNotFoundException), nameof(EntryPointNotFoundException), nameof(TypeLoadException), nameof(TypeInitializationException),
        "ApplicationUpdateHelperUnavailableException", "LosslessMpvException", "RecorderClientException",
        "NativeCompatibilityEnvelopeException", "RejectedResponseException", "ByteLimitException", "TuneLayoutException",
        "TuneChangedException", "TuneAssetValidationException", "TuneAssetStreamValidationException", "RunLibraryFullException",
        "UpdateSecurityException"
    };
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static string ExceptionType(Type type) =>
        ExceptionTypes.Contains(type.Name) && (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true ||
            ProductAssemblies.Contains(type.Assembly)) ? type.Name : "Exception";

    internal static string SafeExceptionType(string? type) => type is not null && ExceptionTypes.Contains(type) ? type : "Exception";

    internal static string? Method(MethodBase? method)
    {
        if (method?.DeclaringType is not { } owner || !ProductAssemblies.Contains(owner.Assembly)) return null;
        if (owner.GetCustomAttribute<CompilerGeneratedAttribute>() is not null && owner.DeclaringType is { } parent)
        {
            method = parent.GetMethods(Members).FirstOrDefault(candidate =>
                candidate.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType == owner);
            if (method is null) return null;
            owner = parent;
        }
        var name = method.IsConstructor ? method.IsStatic ? "cctor" : "ctor" : method.Name;
        var symbol = $"{owner.FullName}.{name}";
        return IsKnownMethod(symbol) ? symbol : null;
    }

    internal static bool IsKnownMethod(string? symbol)
    {
        if (symbol is not { Length: > 0 and <= 240 } ||
            !symbol.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+')) return false;
        var split = symbol.LastIndexOf('.');
        if (split <= 0) return false;
        var typeName = symbol[..split]; var method = symbol[(split + 1)..];
        foreach (var assembly in ProductAssemblies)
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type is null) continue;
            if (method is "ctor" or "cctor")
            {
                if (type.GetConstructors(Members).Any(candidate => candidate.IsStatic == (method == "cctor"))) return true;
            }
            else if (type.GetMethods(Members).Any(candidate => candidate.Name == method)) return true;
        }
        return false;
    }
}
