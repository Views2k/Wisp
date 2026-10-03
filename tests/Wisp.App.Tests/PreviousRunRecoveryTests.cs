using System.IO;
using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PreviousRunRecoveryTests
{
    [Fact]
    public void ManagedFatalReportsCarryTheActiveRunIdentity()
    {
        using var fixture = new Fixture();
        var marker = WindowsFaultEventReaderTests.Marker();
        new CrashExceptionMonitor(fixture.Store, runId: () => marker.RunId).UiException(new IOException("PRIVATE_MESSAGE"));
        Assert.Equal(marker.RunId, fixture.Store.LatestPending()!.RunId);
    }

    [Fact]
    public void RecoveredFaultFieldsAreSanitizedAgainBeforePersistenceAndCopy()
    {
        using var fixture = new Fixture();
        var marker = WindowsFaultEventReaderTests.Marker();
        var detected = marker.StartedAtUtc.AddMinutes(5);
        var report = marker.ToReport(detected) with
        {
            Origin = CrashOrigin.WindowsApplicationFault,
            RecoveredExit = new(marker.StartedAtUtc, detected, WindowsFaultLookupStatus.Matched,
                new(marker.StartedAtUtc.AddMinutes(1), 0xc0000005, 1234, "C:\\PRIVATE_PATH\\ntdll.dll", false))
        };
        Assert.True(fixture.Store.TrySave(report));
        var loaded = fixture.Store.LatestPending()!;
        Assert.Null(loaded.RecoveredExit!.Fault!.Module);
        Assert.DoesNotContain("PRIVATE_", loaded.Format(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_", System.Text.Json.JsonSerializer.Serialize(loaded), StringComparison.Ordinal);
        Assert.Null((report with
        {
            RecoveredExit = report.RecoveredExit with
            { DetectedAtUtc = marker.StartedAtUtc.AddMinutes(-1) }
        }).Sanitize());
    }

    [Theory]
    [InlineData((int)WindowsFaultLookupStatus.NoMatchingEvent)]
    [InlineData((int)WindowsFaultLookupStatus.AccessDenied)]
    [InlineData((int)WindowsFaultLookupStatus.TimedOut)]
    public async Task UnconfirmedExitsKeepThePreviousBuildAndDoNotClaimACrash(int status)
    {
        using var fixture = new Fixture();
        var marker = WindowsFaultEventReaderTests.Marker();
        marker = marker with { Build = marker.Build with { PrivateBuildId = "historical-build-01", ModuleVersionId = Guid.NewGuid() } };
        var source = new Source(_ => new((WindowsFaultLookupStatus)status));
        var report = await fixture.Recover(marker, source);
        Assert.NotNull(report); Assert.Equal(1, source.Calls);
        Assert.Equal(CrashOrigin.UnexpectedExit, report.Origin);
        Assert.Equal(marker.Build.ModuleVersionId, report.ModuleVersionId);
        Assert.Equal("historical-build-01", report.PrivateBuildId);
        Assert.Contains("cause is not established", report.Format(), StringComparison.Ordinal);
        Assert.Null(report.Context);
        Assert.Equal(marker.RunId, fixture.Store.LatestPending()!.RunId);
    }

    [Fact]
    public async Task MatchedWindowsFaultIsAnApplicationCrashWithSanitizedDetails()
    {
        using var fixture = new Fixture();
        var marker = WindowsFaultEventReaderTests.Marker();
        var source = new Source(_ => WindowsFaultEventReader.Correlate([WindowsFaultEventReaderTests.Fault(marker)],
            marker, WindowsFaultEventReaderTests.Started.AddMinutes(5)));
        var report = await fixture.Recover(marker, source);
        Assert.NotNull(report); Assert.Equal(CrashOrigin.WindowsApplicationFault, report.Origin);
        Assert.Contains("Windows exception code: 0xC0000005", report.Format(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_", report.Format(), StringComparison.Ordinal);
        Assert.DoesNotContain("native crash", report.Format(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    public async Task OnlyATerminatingManagedReportForThisExactRunSuppressesRecovery(bool terminating, bool sameRun, int calls)
    {
        using var fixture = new Fixture();
        var marker = WindowsFaultEventReaderTests.Marker();
        Assert.True(fixture.Store.TrySave(CrashReportTests.Report() with
        { RunId = sameRun ? marker.RunId : Guid.NewGuid(), IsTerminating = terminating }));
        var source = new Source(_ => new(WindowsFaultLookupStatus.NoMatchingEvent));
        var report = await fixture.Recover(marker, source);
        Assert.Equal(calls, source.Calls);
        Assert.Equal(calls == 0, report is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownOrDeletionDuringLookupCannotPublishALateReport(bool delete)
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        var marker = WindowsFaultEventReaderTests.Marker();
        var source = new Source(_ =>
        {
            if (delete) Assert.True(fixture.Store.TryDeleteAll());
            else cancelled.Cancel();
            return new(WindowsFaultLookupStatus.NoMatchingEvent);
        });
        Assert.Null(await fixture.Recover(marker, source, cancelled.Token));
        Assert.Null(fixture.Store.LatestPending());
    }

    [Fact]
    public async Task DismissalDuringLookupCannotResurrectThePreviousSessionNotice()
    {
        using var fixture = new Fixture();
        var marker = WindowsFaultEventReaderTests.Marker();
        var existing = CrashReportTests.Report(false);
        Assert.True(fixture.Store.TrySave(existing));
        var source = new Source(_ =>
        {
            Assert.True(fixture.Store.AcknowledgeThrough(existing.Id));
            return new(WindowsFaultLookupStatus.NoMatchingEvent);
        });
        Assert.Null(await fixture.Recover(marker, source));
        Assert.Null(fixture.Store.LatestPending());
    }

    [Fact]
    public async Task CleanMarkerSkipsLookupAndSourceFailureRemainsUnconfirmed()
    {
        using var fixture = new Fixture();
        var marker = WindowsFaultEventReaderTests.Marker();
        var source = new Source(_ => throw new IOException("PRIVATE_STORAGE"));
        Assert.Null(await fixture.Recover(marker with { CleanExit = true }, source)); Assert.Equal(0, source.Calls);
        var report = await fixture.Recover(marker, source);
        Assert.NotNull(report); Assert.Equal(WindowsFaultLookupStatus.ReadFailed, report.RecoveredExit!.LookupStatus);
        Assert.DoesNotContain("PRIVATE_", report.Format(), StringComparison.Ordinal);
    }

    private sealed class Source(Func<CancellationToken, WindowsFaultLookup> read) : IWindowsFaultEventSource
    {
        internal int Calls { get; private set; }
        public WindowsFaultLookup Read(RunExitMarker previous, DateTimeOffset nextRunStartedAtUtc, CancellationToken cancellationToken)
        { Calls++; return read(cancellationToken); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispPreviousRunTests", Guid.NewGuid().ToString("N"));
        internal CrashReportStore Store { get; }
        internal Fixture() { Store = new(_directory); }
        internal Task<CrashReport?> Recover(RunExitMarker marker, Source source, CancellationToken? token = null) =>
            new PreviousRunRecovery(Store, source).RecoverAsync(marker, WindowsFaultEventReaderTests.Started.AddMinutes(5),
                token ?? TestContext.Current.CancellationToken);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
