using System.IO;
using Wisp.App.Drift;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DriftZoneCurveCheckTests : IDisposable
{
    private const string Build = "steam:6.999.1.0:1:2:ABC";
    private static readonly DriftZoneScoringProfile Curve = new(10, 110, (double)59.4f, 30, 40);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wisp-drift-check-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTime _time = new();
    private readonly List<(string Outcome, double? Duration)> _reports = [];
    private readonly Queue<DriftZoneCurveVerdict> _verdicts = new();
    private int _opens;
    private int _scans;
    private string? _sessionBuild = Build;
    private bool _openFails;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string StorePath => Path.Combine(_directory, "drift-zone-checks.json");

    private DriftZoneCurveCheck Create(Action<Action>? run = null) => new(
        Curve,
        _ =>
        {
            _opens++;
            return _openFails || _sessionBuild is null ? null : new DriftZoneScanSession(_sessionBuild, new NoMemory(), new Owner());
        },
        new DriftZoneCheckStore(StorePath),
        (outcome, duration) => _reports.Add((outcome, duration)),
        _time,
        (_, _) =>
        {
            _scans++;
            var verdict = _verdicts.Count > 0 ? _verdicts.Dequeue() : DriftZoneCurveVerdict.Verified;
            return new DriftZoneCurveScanResult(verdict, verdict == DriftZoneCurveVerdict.Verified ? 20 : 0, 1024, TimeSpan.FromMilliseconds(1500));
        },
        run ?? (work => work()));

    [Fact]
    public void AListedBuildHasTheCurveAtOnceAndIsReadOnceInTheBackground()
    {
        using var check = Create();

        Assert.Same(Curve, check.Resolve(Build, Curve, driving: false, allowed: true));
        Assert.Equal(0, _scans);
        Assert.Same(Curve, check.Resolve(Build, Curve, driving: true, allowed: true));
        Assert.Same(Curve, check.Resolve(Build, Curve, driving: true, allowed: true));

        Assert.Equal(1, _scans);
        Assert.Equal([("success", 1500d)], _reports);
    }

    [Fact]
    public void AnUnlistedBuildGetsTheCurveOnlyAfterTheGameWasReadAndHeldIt()
    {
        using var check = Create();

        Assert.Null(check.Resolve(Build, null, driving: false, allowed: true));
        Assert.Equal(0, _scans);
        // The read runs inline in this test, so the first driving frame already sees its result on the next call.
        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Same(Curve, check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Same(Curve, check.Resolve(Build, null, driving: false, allowed: true));
        Assert.Equal(1, _scans);
    }

    [Fact]
    public void AVerifiedBuildIsRememberedAcrossStartsAndReadAgainAfterTwoWeeks()
    {
        using (var first = Create())
        {
            first.Resolve(Build, null, driving: true, allowed: true);
        }

        using var second = Create();
        Assert.Same(Curve, second.Resolve(Build, null, driving: true, allowed: true));
        Assert.Equal(1, _scans);

        _time.Advance(DriftZoneCurveCheck.RevalidateAfter);
        _verdicts.Enqueue(DriftZoneCurveVerdict.NotFound);
        // The stored result stands while the new read runs, then the new result replaces it.
        Assert.Same(Curve, second.Resolve(Build, null, driving: true, allowed: true));
        Assert.Equal(2, _scans);
        Assert.Null(second.Resolve(Build, null, driving: true, allowed: true));
    }

    [Fact]
    public void ABuildWithoutTheCurveStaysAngleOnlyAndIsRetriedWithGrowingPauses()
    {
        foreach (var _ in Enumerable.Range(0, 5)) _verdicts.Enqueue(DriftZoneCurveVerdict.NotFound);
        using var check = Create();

        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Equal(1, _scans);

        _time.Advance(TimeSpan.FromMinutes(2));
        check.Resolve(Build, null, driving: true, allowed: true);
        Assert.Equal(2, _scans);

        _time.Advance(TimeSpan.FromMinutes(9));
        check.Resolve(Build, null, driving: true, allowed: true);
        Assert.Equal(2, _scans);
        _time.Advance(TimeSpan.FromMinutes(1));
        check.Resolve(Build, null, driving: true, allowed: true);
        Assert.Equal(3, _scans);

        _time.Advance(TimeSpan.FromMinutes(59));
        check.Resolve(Build, null, driving: true, allowed: true);
        Assert.Equal(3, _scans);
        _time.Advance(TimeSpan.FromMinutes(1));
        check.Resolve(Build, null, driving: true, allowed: true);
        Assert.Equal(4, _scans);

        // One report per result and build in a run, however often it repeats.
        Assert.Equal([("unsupported", 1500d)], _reports);
    }

    [Fact]
    public void APausedCheckNeitherReadsTheGameNorGrantsTheCurveToAnUnlistedBuild()
    {
        using (var first = Create())
        {
            first.Resolve(Build, null, driving: true, allowed: true);
        }

        using var paused = Create();
        Assert.Null(paused.Resolve(Build, null, driving: true, allowed: false));
        Assert.Same(Curve, paused.Resolve(Build, Curve, driving: true, allowed: false));
        Assert.Null(paused.Resolve("store:other", null, driving: true, allowed: false));
        Assert.Equal(1, _scans);
    }

    [Fact]
    public void AReadThatCannotStartOrFinishKeepsTheStoredResultAndBacksOff()
    {
        using var check = Create();
        _openFails = true;
        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Equal((1, 0), (_opens, _scans));
        Assert.Equal("failure", _reports[^1].Outcome);
        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Equal(1, _opens);

        _openFails = false;
        _verdicts.Enqueue(DriftZoneCurveVerdict.Incomplete);
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Equal("timeout", _reports[^1].Outcome);
        Assert.False(File.Exists(StorePath));

        _time.Advance(TimeSpan.FromMinutes(10));
        check.Resolve(Build, null, driving: true, allowed: true);
        Assert.Same(Curve, check.Resolve(Build, null, driving: true, allowed: true));
    }

    [Fact]
    public void AReadOfAnotherGameBuildDoesNotCountForThisOne()
    {
        using var check = Create();
        _sessionBuild = "steam:6.999.2.0:1:2:DEF";

        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));
        Assert.Null(check.Resolve(Build, null, driving: true, allowed: true));

        Assert.Equal((1, 0), (_opens, _scans));
        Assert.Equal("failure", _reports[^1].Outcome);
    }

    [Fact]
    public void OnlyOneReadRunsAtATimeAndNoneAfterDisposal()
    {
        var pending = new List<Action>();
        var check = Create(pending.Add);

        check.Resolve(Build, null, driving: true, allowed: true);
        check.Resolve(Build, null, driving: true, allowed: true);
        check.Resolve("store:other", null, driving: true, allowed: true);
        Assert.Single(pending);

        check.Dispose();
        pending[0]();
        Assert.Empty(_reports);
        Assert.False(File.Exists(StorePath));
        check.Resolve(Build, null, driving: true, allowed: true);
        Assert.Single(pending);
    }

    [Fact]
    public void WithoutAnAttachedGameTheCatalogAnswerStands()
    {
        using var check = Create();

        Assert.Null(check.Resolve(null, null, driving: true, allowed: true));
        Assert.Same(Curve, check.Resolve(null, Curve, driving: true, allowed: true));
        Assert.Equal(0, _opens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Version\":2,\"Builds\":{}}")]
    [InlineData("{\"Version\":1,\"Builds\":{\"steam:6.999.1.0:1:2:ABC\":{\"Outcome\":\"Granted\",\"CheckedAt\":\"2026-11-20T00:00:00+00:00\",\"Entries\":20}}}")]
    public void ADamagedOrUnknownResultFileIsTreatedAsEmpty(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, content);

        Assert.Null(new DriftZoneCheckStore(StorePath).Find(Build));
    }

    [Fact]
    public void ResultsRoundTripThroughTheFile()
    {
        var checkedAt = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
        new DriftZoneCheckStore(StorePath).Save(Build, new DriftZoneCheckRecord(DriftZoneCheckOutcome.Verified, checkedAt, 20));

        Assert.Equal(new DriftZoneCheckRecord(DriftZoneCheckOutcome.Verified, checkedAt, 20), new DriftZoneCheckStore(StorePath).Find(Build));
        Assert.Null(new DriftZoneCheckStore(null).Find(Build));
    }

    [Theory]
    [InlineData("Verified", "success")]
    [InlineData("NotFound", "unsupported")]
    [InlineData("Unstable", "unknown")]
    [InlineData("Incomplete", "timeout")]
    [InlineData("Unavailable", "failure")]
    public void EveryResultIsReportedWithAnOutcomeTheReportFormatKnows(string outcome, string expected)
    {
        Assert.Equal(expected, DriftZoneCurveCheck.Token(Enum.Parse<DriftZoneCheckOutcome>(outcome)));
        Assert.Equal(5, Enum.GetValues<DriftZoneCheckOutcome>().Length);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Owner : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class NoMemory : IDriftZoneScanMemory
    {
        public IReadOnlyList<DriftZoneScanRegion> Regions() => [];

        public void ReadChunk(ulong address, Span<byte> destination)
        {
        }

        public bool Read(ulong address, Span<byte> destination) => false;
    }
}
