using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Core;
using Wisp.Telemetry;
using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

[Collection("Supplementary instrumentation")]
public sealed class SupplementaryOperationFailureTests
{
    [Theory]
    [InlineData(true, 10013, "access-denied")]
    [InlineData(true, 10048, "port-in-use")]
    [InlineData(true, 10054, "socket-failed")]
    [InlineData(false, unchecked((int)0x80131622), "managed-operation-failed")]
    public void ListenerFaultRetainsKindAndNativeCode(bool socket, int code, string reason)
    {
        var incident = SupplementaryOperationFailure.Receiver(new(socket, code));
        Assert.Equal(reason, incident.Reason);
        Assert.Equal(unchecked((uint)code), incident.Code);
        Assert.Equal("telemetry-ready", incident.FailureStage);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
    }

    [Theory]
    [InlineData("decode-failed", null, "invalid-data")]
    [InlineData("packet-queue-full", null, "media-queue-full")]
    [InlineData("command-loadfile/-13", -13, "native-failure")]
    public void NativePlaybackRetainsBoundedReasonAndActualNativeCode(string token, int? code, string reason)
    {
        var error = new Wisp.App.Clips.LosslessMpvException(token, code);
        var incident = SupplementaryOperationFailure.Capture("clips", "decode", error, DateTimeOffset.UtcNow)!;
        Assert.Equal(reason, incident.Reason);
        Assert.Equal(unchecked((uint)(code ?? error.HResult)), incident.Code);
        Assert.Equal("clips-decode", incident.FailureStage);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
    }

    [Theory]
    [InlineData(10048, "port-in-use")]
    [InlineData(10013, "access-denied")]
    public async Task SetupBindingRetainsTheRealSocketErrorWithoutUserData(int code, string reason)
    {
        using var observations = new ObservationScope();
        var result = await new SetupTelemetryTest(new Source(new SocketException(code))).RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Passed);
        var terminal = Assert.Single(observations.Values, v => v.Outcome == "failure");
        var incident = Assert.IsType<SupplementaryIncident>(terminal.Incident);
        Assert.Equal((uint)code, incident.Code);
        Assert.Equal(reason, incident.Reason);
        Assert.Equal("setup-data-out", incident.FailureStage);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
    }

    [Theory]
    [InlineData(0, 0, "no-datagrams")]
    [InlineData(0, 4, "packets-rejected")]
    [InlineData(5, 1, "sequence-incomplete")]
    public async Task SetupTimeoutReportsWhatTheReceiverActuallyObserved(long accepted, long rejected, string reason)
    {
        using var observations = new ObservationScope();
        var source = new Source(null, new(AcceptedPackets: accepted, RejectedPackets: rejected, PacketsPerSecond: 0, LastParseError: PacketParseError.None, ListenerError: null));
        var result = await new SetupTelemetryTest(source, timeout: TimeSpan.Zero).RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Passed);
        var terminal = Assert.Single(observations.Values, v => v.Outcome == "timeout");
        Assert.Equal(reason, terminal.Incident?.Reason);
        Assert.True(SupplementaryIncidentSchema.Valid(terminal.Incident!));
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task InvalidSetupPortIsAUserInputRejectionNotAFailure()
    {
        using var observations = new ObservationScope();
        Assert.False((await new SetupTelemetryTest(new Source()).RunAsync("invalid", cancellationToken: TestContext.Current.CancellationToken)).Passed);
        Assert.Equal(new[] { "attempt", "idle" }, observations.Values.Select(v => v.Outcome));
        Assert.All(observations.Values, v => Assert.Null(v.Incident));
    }

    [Theory]
    [InlineData("http", "http-status", 503u)]
    [InlineData("dns", "network-name-resolution", 0x80131500u)]
    [InlineData("verification", "update-verification-failed", 0x80131500u)]
    [InlineData("timeout", "timeout", 0x8013153bu)]
    public async Task UpdaterPublishesClassifiedTerminalEvidence(string kind, string reason, uint expectedCode)
    {
        using var observations = new ObservationScope();
        Exception failure = kind switch
        {
            "http" => new HttpRequestException("PRIVATE_SENTINEL", null, HttpStatusCode.ServiceUnavailable),
            "dns" => new HttpRequestException(HttpRequestError.NameResolutionError, "PRIVATE_SENTINEL"),
            "verification" => new UpdateSecurityException("PRIVATE_SENTINEL"),
            _ => new OperationCanceledException("PRIVATE_SENTINEL")
        };
        await using var controller = new AppController(new AppSettings(), _ => { }, new NoStartup(),
            checkForApplicationUpdate: (_, _) => Task.FromException<UpdateRelease?>(failure));
        Assert.Null(await controller.GetAvailableApplicationUpdateDetailsAsync());
        var terminal = Assert.Single(observations.Values, v => v.Feature == "update" && v.Outcome != "attempt");
        Assert.Equal(kind == "timeout" ? "timeout" : "failure", terminal.Outcome);
        Assert.Equal(reason, terminal.Incident?.Reason);
        Assert.Equal(expectedCode, terminal.Incident?.Code);
        Assert.True(SupplementaryIncidentSchema.Valid(terminal.Incident!));
        Assert.DoesNotContain("PRIVATE_SENTINEL", JsonSerializer.Serialize(observations.Values));
    }

    [Fact]
    public async Task SettingsSaveKeepsItsStorageCodeAndProfileValidationStaysOutOfFailures()
    {
        using var observations = new ObservationScope();
        await using var controller = new AppController(CompletedSettings(),
            _ => throw new IOException("PRIVATE_SENTINEL", unchecked((int)0x80070070)), new NoStartup());
        observations.Values.Clear();
        Assert.False(controller.TrySavePendingSettings());
        var failure = Assert.Single(observations.Values, v => v.Outcome == "failure");
        Assert.Equal("settings-save", failure.Incident?.FailureStage);
        Assert.Equal("disk-full", failure.Incident?.Reason);
        Assert.Equal(0x80070070u, failure.Incident?.Code);
        Assert.True(SupplementaryIncidentSchema.Valid(failure.Incident!));
        observations.Values.Clear();
        Assert.False(controller.TryCreateHudPreset("", out _, out _));
        Assert.False(controller.DeleteHudPreset(Guid.NewGuid()));
        Assert.DoesNotContain(observations.Values, v => v.Outcome == "failure");
        Assert.Equal(2, observations.Values.Count(v => v.Outcome == "idle"));
    }

    [Fact]
    public void ExceptionCaptureRejectsUnknownPhasesAndDoesNotLeakErrorTextOrPaths()
    {
        var error = new FileNotFoundException("PRIVATE_SENTINEL", "PRIVATE_SENTINEL");
        Assert.Null(SupplementaryOperationFailure.Capture("runs", "PRIVATE_SENTINEL", error, DateTimeOffset.UtcNow));
        var incident = SupplementaryOperationFailure.Capture("runs", "load", error, DateTimeOffset.UtcNow)!;
        Assert.Equal("file-unavailable", incident.Reason);
        Assert.Equal("runs-load", incident.FailureStage);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.DoesNotContain("PRIVATE_SENTINEL", JsonSerializer.Serialize(incident));
    }

    private static AppSettings CompletedSettings()
    {
        var settings = new AppSettings { StartWithWindows = false, AutomaticApplicationUpdateChecks = false };
        var now = DateTimeOffset.UtcNow;
        SetupCompletion.Save(settings, SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        }, new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now), _ => { }, now);
        return settings;
    }

    private sealed class ObservationScope : IDisposable
    {
        private readonly Action<SupplementaryObservation>? _previous = SupplementaryObservations.Observer;
        internal List<SupplementaryObservation> Values { get; } = [];
        internal ObservationScope() => SupplementaryObservations.Observer = Values.Add;
        public void Dispose() => SupplementaryObservations.Observer = _previous;
    }
    private sealed class NoStartup : IStartupRegistrationService
    { public void Apply(bool startWithWindows, bool startWithForza) { } }
    private sealed class Source(Exception? error = null, ReceiverStatistics? statistics = null) : ISetupTelemetrySource
    {
        public event EventHandler? PacketAvailable { add { } remove { } }
        public VehicleState? Latest => null;
        public bool IsRunning { get; private set; }
        public int? ListeningPort { get; private set; }
        public Task BindAsync(int port)
        {
            if (error is not null) throw error;
            ListeningPort = port; IsRunning = true;
            return Task.CompletedTask;
        }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public ReceiverStatistics GetStatistics(DateTimeOffset nowUtc) => statistics ?? new(0, 0, 0, PacketParseError.None, null);
    }
}
