using System.IO;
using Wisp.App.Supplementary;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

[CollectionDefinition("Supplementary instrumentation", DisableParallelization = true)]
public sealed class SupplementaryInstrumentationCollection { }

[Collection("Supplementary instrumentation")]
public sealed class SupplementaryInstrumentationTests
{
    [Theory]
    [InlineData(false, "failure")]
    [InlineData(true, "cancelled")]
    public async Task SetupObservesRealFailureAndCancellationWithoutLeakingSourceError(bool cancel, string outcome)
    {
        var observed = new List<SupplementaryObservation>();
        var previous = SupplementaryObservations.Observer;
        SupplementaryObservations.Observer = observed.Add;
        try
        {
            using var cancellation = new CancellationTokenSource();
            if (cancel) cancellation.Cancel();
            var test = new SetupTelemetryTest(new FailingSource());
            var result = await test.RunAsync("5300", cancellationToken: cancellation.Token);
            Assert.False(result.Passed);
            Assert.Equal(new[] { "attempt", outcome }, observed.Select(v => v.Outcome));
            Assert.All(observed, v => { Assert.Equal("setup", v.Kind); Assert.Equal("data-out", v.Stage); });
            Assert.DoesNotContain("fixture-source-error", System.Text.Json.JsonSerializer.Serialize(observed));
        }
        finally { SupplementaryObservations.Observer = previous; }
    }

    private sealed class FailingSource : ISetupTelemetrySource
    {
        public event EventHandler? PacketAvailable { add { } remove { } }
        public VehicleState? Latest => null;
        public bool IsRunning => false;
        public int? ListeningPort => null;
        public Task BindAsync(int port) => throw new IOException("fixture-source-error");
        public Task StopAsync() => Task.CompletedTask;
        public ReceiverStatistics GetStatistics(DateTimeOffset nowUtc) => new(0, 0, 0, PacketParseError.None, null);
    }
}
