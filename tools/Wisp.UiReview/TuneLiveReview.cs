using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.App;
using Wisp.App.Tunes;
using Wisp.Core.Tunes;

namespace Wisp.UiReview;

// Explicit live opt-in only. This path never constructs WPF or changes foreground focus.
internal static class TuneLiveReview
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<int> RunAsync(string output)
    {
        var report = new LiveReport();
        var reportPath = Path.Combine(output, "tune-live-check.json");
        var started = Stopwatch.GetTimestamp();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Hard termination must not wait for a file write or the main thread's cleanup.
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);

        TuneCaptureService? service = null;
        try
        {
            report.Phase = "foreground-before";
            report.ForegroundAtStart = ShiftCaptureSession.IsForzaForeground();
            if (report.ForegroundAtStart != true)
                Fail("forza-not-foreground", "Forza must already be foreground; no capture was requested.");

            service = new TuneCaptureService();
            report.Phase = "first-capture";
            var first = await CaptureAsync(service, budget.Token).ConfigureAwait(false);
            report.FirstCaptureMilliseconds = first.ElapsedMilliseconds;
            report.FirstStatus = first.Result.Status;
            var snapshot = Validate(first.Result, "first");
            report.Snapshot = snapshot;
            Require(snapshot.IsComplete, "incomplete-tune", "One or more tuning controls are unavailable; inspect the field statuses.");
            report.FirstWasCurrent = service.IsCurrent(snapshot);
            Require(report.FirstWasCurrent, "first-identity-expired", "The accepted car or game session changed after the first read.");

            report.Phase = "foreground-between";
            report.ForegroundBetweenReads = ShiftCaptureSession.IsForzaForeground();
            Require(report.ForegroundBetweenReads == true, "foreground-changed", "Forza lost foreground before the confirmation read.");

            report.Phase = "confirmation-capture";
            var confirmation = await CaptureAsync(service, budget.Token).ConfigureAwait(false);
            report.ConfirmationCaptureMilliseconds = confirmation.ElapsedMilliseconds;
            report.ConfirmationStatus = confirmation.Result.Status;
            var confirmed = Validate(confirmation.Result, "confirmation");
            Require(confirmed.IsComplete, "incomplete-confirmation", "The confirmation read contains unavailable tuning controls.");
            report.SessionStillCurrent = service.IsCurrent(snapshot) && service.IsCurrent(confirmed);
            Require(report.SessionStillCurrent, "session-changed", "The car or game session changed between the two reads.");
            report.SetupMatched = TuneComparison.HaveSameSetupIdentity(snapshot, confirmed);
            Require(report.SetupMatched, "setup-changed", "The supported tuning values or installed-part context changed between reads.");
            budget.Token.ThrowIfCancellationRequested();
            report.Passed = true;
        }
        catch (ReviewFailure error)
        {
            report.Passed = false;
            report.FailurePhase = report.Phase;
            report.Failure = error.Code;
            report.Reason = error.Message; // Only fixed, local review text enters this exception.
        }
        catch (OperationCanceledException)
        {
            report.Passed = false;
            report.FailurePhase = report.Phase;
            report.Failure = "capture-timeout";
            report.Reason = "The live check exceeded its 15-second cancellation budget.";
        }
        catch (Exception error)
        {
            report.Passed = false;
            report.FailurePhase = report.Phase;
            report.Failure = "unexpected-read-failure";
            report.Reason = "The production reader could not complete this check.";
            report.ExceptionType = error.GetType().Name;
            report.ExceptionHResult = error.HResult;
        }
        finally
        {
            report.Phase = "dispose";
            if (service is not null)
            {
                try
                {
                    await service.DisposeAsync().ConfigureAwait(false);
                    report.DisposalCompleted = true;
                }
                catch (Exception error)
                {
                    report.Passed = false;
                    report.FailurePhase = report.Phase;
                    report.Failure = "cleanup-failed";
                    report.Reason = "Production capture cleanup did not complete successfully.";
                    report.ExceptionType = error.GetType().Name;
                    report.ExceptionHResult = error.HResult;
                }
            }
            report.ForegroundAtCompletion = ShiftCaptureSession.IsForzaForeground();
            if (report.Passed && report.ForegroundAtCompletion != true)
            {
                report.Passed = false;
                report.FailurePhase = "foreground-after";
                report.Failure = "foreground-changed";
                report.Reason = "Forza was no longer foreground at completion; this check is invalid.";
            }
            report.Phase = "complete";
            report.ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine(report.Passed ? "Tune live check passed; no game writes or visible windows."
            : $"Tune live check failed: {report.Failure}. {report.Reason}");
        return report.Passed ? 0 : report.Failure == "capture-timeout" ? 124 : 2;

        async Task<(TuneCaptureResult Result, double ElapsedMilliseconds)> CaptureAsync(
            TuneCaptureService capture, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            report.CaptureRequests++;
            var captureStarted = Stopwatch.GetTimestamp();
            var result = await capture.RequestSnapshotAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return (result, Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds);
        }
    }

    private static TuneSnapshot Validate(TuneCaptureResult result, string read)
    {
        if (!result.Success || result.Snapshot is null)
        {
            var reason = result.Status switch
            {
                TuneCaptureStatus.GameNotRunning => "The production reader did not find a running Forza process.",
                TuneCaptureStatus.UnsupportedBuild => "The running game build is not supported by this tune reader.",
                TuneCaptureStatus.Changed => "The car, tune or game changed during the read.",
                TuneCaptureStatus.Cancelled => "The production tune read was cancelled.",
                _ => "The production reader could not verify this tune or its required metadata."
            };
            Fail($"{read}-capture-{result.Status}", reason);
        }
        if (!TuneSnapshotValidator.TryValidate(result.Snapshot, out _))
            Fail($"{read}-snapshot-invalid", "The captured snapshot failed the production consistency validator.");
        return result.Snapshot!;
    }

    private static void Require(bool condition, string code, string reason)
    {
        if (!condition) Fail(code, reason);
    }

    private static void Fail(string code, string reason) => throw new ReviewFailure(code, reason);
    private sealed class ReviewFailure(string code, string reason) : Exception(reason)
    {
        internal string Code { get; } = code;
    }

    private sealed class LiveReport
    {
        public bool Passed { get; set; }
        public string Phase { get; set; } = "initialize";
        public string? Failure { get; set; }
        public string? FailurePhase { get; set; }
        public string? Reason { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public bool? ForegroundAtStart { get; set; }
        public bool? ForegroundBetweenReads { get; set; }
        public bool? ForegroundAtCompletion { get; set; }
        public int CaptureRequests { get; set; }
        public TuneCaptureStatus? FirstStatus { get; set; }
        public TuneCaptureStatus? ConfirmationStatus { get; set; }
        public bool FirstWasCurrent { get; set; }
        public bool SessionStillCurrent { get; set; }
        public bool SetupMatched { get; set; }
        public bool DisposalCompleted { get; set; }
        public double? FirstCaptureMilliseconds { get; set; }
        public double? ConfirmationCaptureMilliseconds { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public TuneSnapshot? Snapshot { get; set; }
        public int CancellationBudgetSeconds => 15;
        public int HardProcessLimitSeconds => 30;
        public int VisibleWindows => 0;
        public int RequestedFocusChanges => 0;
        public int GameWrites => 0;
        public int UserLibraryWrites => 0;
        public string Scope => "Two read-only production captures with foreground checks at their boundaries. Does not verify menu screenshot values, continuous foreground, every car/build, driving performance or the performance panel.";
    }
}
