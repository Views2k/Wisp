using System.Globalization;
using Wisp.App.CrashDiagnostics;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class WindowsFaultEventReaderTests
{
    internal static readonly DateTimeOffset Started = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    internal static RunExitMarker Marker() => new(Guid.NewGuid(), 42, Started.UtcDateTime.ToFileTimeUtc(), Started,
        "2.6.1.0", DiagnosticBuildIdentity.Current);
    private const string ReportId = "10000000-0000-0000-0000-000000000001";

    [Fact]
    public void ExactProcessInstanceAndReportIdAreCorrelatedWithoutPrivateEventFields()
    {
        var marker = Marker();
        var result = WindowsFaultEventReader.Correlate([Wer(), Fault(marker)], marker, Started.AddMinutes(5));
        Assert.Equal(WindowsFaultLookupStatus.Matched, result.Status);
        Assert.NotNull(result.Fault);
        Assert.Equal(0xc0000005u, result.Fault.ExceptionCode);
        Assert.Equal(0x1234ul, result.Fault.FaultOffset);
        Assert.Equal("libmpv-2.dll", result.Fault.Module);
        Assert.True(result.Fault.CorrelatedWerReport);
        Assert.DoesNotContain("PRIVATE_", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AppName", "Other.exe")]
    [InlineData("AppVersion", "2.6.0.0")]
    [InlineData("ProcessId", "43")]
    [InlineData("ProcessCreationTime", "0x1234")]
    [InlineData("ProcessCreationTime", "")]
    [InlineData("ExceptionCode", "PRIVATE_CODE")]
    public void MissingOrMismatchedIdentityNeverConfirmsACrash(string field, string value)
    {
        var marker = Marker();
        var result = WindowsFaultEventReader.Correlate([Fault(marker, field, value), Wer()], marker, Started.AddMinutes(5));
        Assert.Equal(WindowsFaultLookupStatus.NoMatchingEvent, result.Status);
        Assert.Null(result.Fault);
    }

    [Theory]
    [InlineData("PRIVATE_PERSON.dll")]
    [InlineData("C:\\PRIVATE_PATH\\libmpv-2.dll")]
    [InlineData("libmpv-2.dll/PRIVATE_PATH")]
    public void OnlyKnownModuleBasenamesCanBeCopied(string module)
    {
        var marker = Marker();
        var result = WindowsFaultEventReader.Correlate([Fault(marker, "ModuleName", module)], marker, Started.AddMinutes(5));
        Assert.NotNull(result.Fault); Assert.Null(result.Fault.Module);
        Assert.Equal(0xc0000005u, result.Fault.ExceptionCode);
    }

    [Fact]
    public void WrongProviderOutOfWindowWerAloneAndOversizedXmlAreNotEvidence()
    {
        var marker = Marker();
        foreach (var xml in new[]
        {
            Wer(), Fault(marker).Replace("Application Error", "PRIVATE_PROVIDER", StringComparison.Ordinal),
            Fault(marker).Replace("12:01:00", "11:59:00", StringComparison.Ordinal),
            Fault(marker).Replace("12:01:00", "12:06:00", StringComparison.Ordinal),
            new string('x', WindowsFaultEventReader.MaximumXmlCharacters + 1),
            "<!DOCTYPE Event [<!ENTITY injected SYSTEM 'file:///PRIVATE_PATH'>]><Event>&injected;</Event>"
        }) Assert.Null(WindowsFaultEventReader.Correlate([xml], marker, Started.AddMinutes(5)).Fault);
        var unmatchedWer = WindowsFaultEventReader.Correlate([Fault(marker), Wer().Replace(ReportId,
            "20000000-0000-0000-0000-000000000002", StringComparison.Ordinal)], marker, Started.AddMinutes(5));
        Assert.NotNull(unmatchedWer.Fault); Assert.False(unmatchedWer.Fault.CorrelatedWerReport);
    }

    [Fact]
    public void ScanStopsAtTheFixedRecordLimit()
    {
        var marker = Marker();
        Assert.Null(WindowsFaultEventReader.Correlate(Enumerable.Repeat(Wer(), WindowsFaultEventReader.MaximumEvents)
            .Append(Fault(marker)), marker, Started.AddMinutes(5)).Fault);
    }

    internal static string Fault(RunExitMarker marker, string? replaceField = null, string? replacement = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["AppName"] = "Wisp.exe",
            ["AppVersion"] = marker.ExecutableVersion,
            ["ProcessId"] = "0x" + marker.ProcessId.ToString("x", CultureInfo.InvariantCulture),
            ["ProcessCreationTime"] = "0x" + marker.ProcessCreationFileTime.ToString("x", CultureInfo.InvariantCulture),
            ["ExceptionCode"] = "c0000005",
            ["FaultingOffset"] = "0000000000001234",
            ["ModuleName"] = "libmpv-2.dll",
            ["IntegratorReportId"] = ReportId,
            ["AppPath"] = "C:\\PRIVATE_PATH\\Wisp.exe",
            ["ModulePath"] = "C:\\PRIVATE_PATH\\libmpv-2.dll"
        };
        if (replaceField is not null) fields[replaceField] = replacement!;
        return Event("Application Error", 1000, fields);
    }

    private static string Wer() => Event("Windows Error Reporting", 1001,
        new() { ["P1"] = "Wisp.exe", ["ReportId"] = ReportId, ["StorePath"] = "PRIVATE_PATH" });
    private static string Event(string provider, int id, Dictionary<string, string> fields) =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{provider}'/>" +
        $"<EventID>{id}</EventID><TimeCreated SystemTime='2026-10-03T12:01:00Z'/><Computer>PRIVATE_HOST</Computer>" +
        "<Security UserID='PRIVATE_ACCOUNT'/></System><EventData>" +
        string.Concat(fields.Select(field => $"<Data Name='{field.Key}'>{System.Security.SecurityElement.Escape(field.Value)}</Data>")) +
        "</EventData></Event>";
}
