using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryNativeStatusReportsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly NativeGameProcess Game = new(4120, Now.UtcDateTime.AddMinutes(-10));

    [Theory]
    [InlineData(NativeAssistProviderStatus.AccessDenied)]
    [InlineData(NativeAssistProviderStatus.UnsupportedBuild)]
    public void PauseAndResumeOnOneGameProcessReportOnce(NativeAssistProviderStatus status)
    {
        var reports = new SupplementaryNativeStatusReports();
        // Each pause or menu detaches the reader (Unavailable); each resume re-attaches and repeats the status.
        var first = Collect(reports, Game, Sample(0, status), Sample(2, status), Sample(4, NativeAssistProviderStatus.Unavailable),
            Sample(6, NativeAssistProviderStatus.Unavailable), Sample(8, status), Sample(10, NativeAssistProviderStatus.Unavailable), Sample(12, status));
        var later = Collect(reports, Game, Sample(70, NativeAssistProviderStatus.Unavailable), Sample(72, status),
            Sample(74, NativeAssistProviderStatus.Unavailable), Sample(76, status));

        Assert.Equal([Now], first);
        Assert.Empty(later);
    }

    [Fact]
    public void NewGameProcessReportsTheSameStatusAgain()
    {
        var reports = new SupplementaryNativeStatusReports();
        Assert.Single(Collect(reports, Game, Sample(0, NativeAssistProviderStatus.AccessDenied)));

        var restarted = Game with { StartedUtc = Now.UtcDateTime.AddSeconds(30) };
        Assert.Equal([Now.AddSeconds(60)], Collect(reports, restarted, Sample(60, NativeAssistProviderStatus.AccessDenied)));

        var other = new NativeGameProcess(5216, Now.UtcDateTime.AddSeconds(90));
        Assert.Equal([Now.AddSeconds(120)], Collect(reports, other, Sample(120, NativeAssistProviderStatus.AccessDenied)));
    }

    [Fact]
    public void EachStatusIsReportedOncePerProcess()
    {
        var reports = new SupplementaryNativeStatusReports();

        var reported = Collect(reports, Game, Sample(0, NativeAssistProviderStatus.AccessDenied), Sample(2, NativeAssistProviderStatus.Unavailable),
            Sample(4, NativeAssistProviderStatus.UnsupportedBuild), Sample(6, NativeAssistProviderStatus.Unavailable), Sample(8, NativeAssistProviderStatus.AccessDenied));

        Assert.Equal([Now, Now.AddSeconds(4)], reported);
    }

    [Fact]
    public void SamplesFromBeforeARestartStayWithThePreviousProcess()
    {
        var reports = new SupplementaryNativeStatusReports();
        Assert.Single(Collect(reports, Game, Sample(0, NativeAssistProviderStatus.AccessDenied)));

        // The game restarted between collections: the earlier sample is the old process, the later one the new.
        var restarted = new NativeGameProcess(5216, Now.UtcDateTime.AddSeconds(40));
        var reported = Collect(reports, restarted, Sample(30, NativeAssistProviderStatus.AccessDenied), Sample(80, NativeAssistProviderStatus.AccessDenied));

        Assert.Equal([Now.AddSeconds(80)], reported);
    }

    [Fact]
    public void GameExitBeforeCollectionKeepsTheLastProcess()
    {
        var reports = new SupplementaryNativeStatusReports();
        Assert.Single(Collect(reports, Game, Sample(0, NativeAssistProviderStatus.UnsupportedBuild)));

        Assert.Empty(Collect(reports, null, Sample(60, NativeAssistProviderStatus.Unavailable), Sample(62, NativeAssistProviderStatus.UnsupportedBuild)));
    }

    [Fact]
    public void UnknownStartTimeStillReportsOncePerProcess()
    {
        var reports = new SupplementaryNativeStatusReports();
        var game = new NativeGameProcess(4120, null);

        Assert.Single(Collect(reports, game, Sample(0, NativeAssistProviderStatus.AccessDenied), Sample(2, NativeAssistProviderStatus.Unavailable),
            Sample(4, NativeAssistProviderStatus.AccessDenied)));
        Assert.Empty(Collect(reports, game, Sample(70, NativeAssistProviderStatus.AccessDenied)));
    }

    [Fact]
    public void RefusedReportIsOfferedAgain()
    {
        var reports = new SupplementaryNativeStatusReports();

        Assert.Empty(Collect(reports, Game, accepted: false, Sample(0, NativeAssistProviderStatus.AccessDenied)));
        Assert.Equal([Now], Collect(reports, Game, Sample(0, NativeAssistProviderStatus.AccessDenied)));
    }

    [Theory]
    [InlineData(NativeAssistProviderStatus.Ready)]
    [InlineData(NativeAssistProviderStatus.Unavailable)]
    [InlineData(NativeAssistProviderStatus.GameNotRunning)]
    [InlineData(NativeAssistProviderStatus.ReadFailure)]
    [InlineData(NativeAssistProviderStatus.PlayerNotUnique)]
    public void OtherStatusesAreNotReported(NativeAssistProviderStatus status)
    {
        Assert.False(SupplementaryNativeStatusReports.Reportable(status));
        Assert.Empty(Collect(new SupplementaryNativeStatusReports(), Game, Sample(0, status)));
    }

    private static List<DateTimeOffset> Collect(SupplementaryNativeStatusReports reports, NativeGameProcess? process,
        params HealthContextSample[] samples) => Collect(reports, process, accepted: true, samples);

    // Mirrors the reporting collector: the process is looked up when a sample may need a report.
    private static List<DateTimeOffset> Collect(SupplementaryNativeStatusReports reports, NativeGameProcess? process,
        bool accepted, params HealthContextSample[] samples)
    {
        if (samples.Any(sample => SupplementaryNativeStatusReports.Reportable(sample.NativeStatus))) reports.Observe(process);
        var reported = new List<DateTimeOffset>();
        foreach (var sample in samples)
        {
            if (!reports.ShouldReport(sample)) continue;
            if (!accepted) break;
            reports.MarkReported(sample);
            reported.Add(sample.TimestampUtc);
        }
        return reported;
    }

    private static HealthContextSample Sample(int seconds, NativeAssistProviderStatus status) =>
        new() { TimestampUtc = Now.AddSeconds(seconds), NativeStatus = status };
}
