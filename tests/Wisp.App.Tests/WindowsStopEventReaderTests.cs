using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class WindowsStopEventReaderTests
{
    private static readonly DateTimeOffset LastSeen = new(2026, 10, 18, 5, 13, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset NextStart = LastSeen.AddMinutes(4.5);

    [Fact]
    public void AnUncleanBootRightAfterTheLastCheckpointIsAWindowsStop()
    {
        // The sequence Windows logged on a PC that stopped while Wisp was running and rebooted 39 s later.
        Assert.True(WindowsStopEventReader.Correlate([Boot(39), PowerLoss(41), UncleanShutdown(66)], LastSeen, NextStart));
        Assert.True(WindowsStopEventReader.Correlate([Boot(39), UncleanShutdown(66)], LastSeen, NextStart));
        Assert.True(WindowsStopEventReader.Correlate([Boot(39), PowerLoss(41)], LastSeen, NextStart));
    }

    [Fact]
    public void ACleanRestartIsNotAWindowsStop()
    {
        Assert.False(WindowsStopEventReader.Correlate([Boot(39)], LastSeen, NextStart));
        Assert.False(WindowsStopEventReader.Correlate([], LastSeen, NextStart));
    }

    [Fact]
    public void ABootLongAfterTheLastCheckpointLeavesTheExitUnexplained()
    {
        var bootDelay = WindowsStopEventReader.MaximumBootDelay.TotalSeconds;
        var nextStart = LastSeen.AddHours(1);
        Assert.False(WindowsStopEventReader.Correlate([Boot(bootDelay + 1), PowerLoss(bootDelay + 3)], LastSeen, nextStart));
        Assert.True(WindowsStopEventReader.Correlate([Boot(bootDelay - 1), PowerLoss(bootDelay + 1)], LastSeen, nextStart));
    }

    [Fact]
    public void OnlyTheFirstBootAfterTheLastCheckpointCounts()
    {
        var nextStart = LastSeen.AddMinutes(30);
        // A clean restart, then a later unclean one: the run ended at the clean restart.
        Assert.False(WindowsStopEventReader.Correlate([Boot(60), Boot(600), PowerLoss(602)], LastSeen, nextStart));
        // An unclean boot before the run was last seen belongs to an earlier stop.
        Assert.False(WindowsStopEventReader.Correlate([PowerLoss(-120), Boot(60)], LastSeen, nextStart));
        // A record written long after the boot is not that boot's stop record.
        Assert.False(WindowsStopEventReader.Correlate([Boot(60), UncleanShutdown(60 + WindowsStopEventReader.MaximumEventDelay.TotalSeconds + 1)],
            LastSeen, nextStart));
    }

    [Fact]
    public void ForeignProvidersAndMalformedRecordsAreIgnored()
    {
        Assert.False(WindowsStopEventReader.Correlate([Boot(39), Event("PRIVATE_PROVIDER", 41, 41)], LastSeen, NextStart));
        Assert.False(WindowsStopEventReader.Correlate([Boot(39), Event("Microsoft-Windows-Kernel-Power", 42, 41)], LastSeen, NextStart));
        Assert.False(WindowsStopEventReader.Correlate([Boot(39), new string('x', WindowsStopEventReader.MaximumXmlCharacters + 1),
            "<!DOCTYPE Event [<!ENTITY injected SYSTEM 'file:///PRIVATE_PATH'>]><Event>&injected;</Event>", "not xml"], LastSeen, NextStart));
    }

    private static string Boot(double seconds) => Event("Microsoft-Windows-Kernel-General", 12, seconds);
    private static string PowerLoss(double seconds) => Event("Microsoft-Windows-Kernel-Power", 41, seconds);
    private static string UncleanShutdown(double seconds) => Event("EventLog", 6008, seconds);
    private static string Event(string provider, int id, double seconds) =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{provider}'/>" +
        $"<EventID>{id}</EventID><TimeCreated SystemTime='{LastSeen.AddSeconds(seconds).UtcDateTime:O}'/>" +
        "<Computer>PRIVATE_HOST</Computer></System><EventData><Data Name='BugcheckCode'>0</Data></EventData></Event>";
}
