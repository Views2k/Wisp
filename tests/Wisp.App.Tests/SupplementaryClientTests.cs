using System.Net;
using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryClientTests
{
    [Fact]
    public void InvalidAndMismatchedReportsAreCountedWhileDisabledReportingIsNotLoss()
    {
        using var f = new SupplementaryFixture();
        using var client = new SupplementaryClient(new(new Uri("https://console.example.invalid/")), new(f.Verifier, f.Directory), new FixedClock(f.Now));
        var value = f.Event();
        Assert.False(client.TryEnqueue(value with { Outcome = "PRIVATE_DO_NOT_COPY" }));
        Assert.Equal(1, client.DroppedEvents);
        Assert.True(client.TryEnqueue(value));
        Assert.False(client.TryEnqueue(value with { SessionId = Guid.NewGuid() }));
        Assert.Equal(2, client.DroppedEvents);
        using var disabled = new SupplementaryClient(new(null), new(f.Verifier, f.Directory), new FixedClock(f.Now));
        Assert.False(disabled.TryEnqueue(value));
        Assert.Equal(0, disabled.DroppedEvents);
    }

    [Fact]
    public async Task IngestionBusyRetriesTheExactBatchWithBoundedDelay()
    {
        using var f = new SupplementaryFixture();
        var attempts = 0;
        var bodies = new List<byte[]>();
        var delays = new List<TimeSpan>();
        using var transport = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() => ++attempts < 3
                ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"schemaVersion\":1,\"error\":{\"code\":\"ingestion_busy\"}}", Encoding.UTF8, "application/json") }
                : JsonResponse("{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":0}"),
                request => bodies.Add(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult())));
        using var client = new SupplementaryClient(new(new Uri("https://console.example.invalid/")), new(f.Verifier, f.Directory),
            new FixedClock(f.Now), transport, () => 0, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        Assert.True(client.TryEnqueue(f.Event()));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, attempts);
        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
        Assert.Equal(new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) }, delays);
        Assert.Equal(0, client.DroppedEvents);
    }

    [Theory]
    [InlineData("identity_conflict")]
    [InlineData("sequence_conflict")]
    [InlineData("revision_conflict")]
    public async Task ConflictResponsesRemainTerminal(string code)
    {
        using var f = new SupplementaryFixture();
        var attempts = 0;
        using var transport = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() =>
            {
                attempts++; return new(HttpStatusCode.Conflict)
                { Content = new StringContent($"{{\"schemaVersion\":1,\"error\":{{\"code\":\"{code}\"}}}}", Encoding.UTF8, "application/json") };
            }));
        using var client = new SupplementaryClient(new(new Uri("https://console.example.invalid/")), new(f.Verifier, f.Directory),
            new FixedClock(f.Now), transport, delay: (_, _) => throw new InvalidOperationException("Terminal conflicts must not retry."));
        Assert.True(client.TryEnqueue(f.Event()));
        Assert.False(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
        Assert.Equal(1, client.DroppedEvents);
    }

    [Fact]
    public async Task CumulativeSessionSummarySurvivesQueueLossAndRetriesAnImmutableSnapshot()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { ForcedStatus = SupplementaryRequestStatus.Unavailable };
        using var client = new SupplementaryClient(new(new Uri("https://console.example.invalid/")),
            new(f.Verifier, f.Directory), new FixedClock(f.Now), transport, () => 0,
            (_, _) => Task.CompletedTask);
        var summary = new SupplementarySessionSummary(8_000, 2_000, 4_000, 2_000, 2_000, 10_000);
        var activity = new SupplementaryActivitySummary(4000, 2000, 2000, 2000, 10000, 40000, 20000, 4, "moving");
        var eventValue = f.Event() with { Kind = "heartbeat", SessionSummary = summary, ActivitySummary = activity };
        Assert.True(client.TryEnqueue(eventValue));
        Assert.False(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, transport.Bodies.Count);
        Assert.All(transport.Bodies, bytes => Assert.Equal(transport.Bodies[0], bytes));
        using var failed = JsonDocument.Parse(transport.Bodies[0]);
        Assert.Equal(8_000, failed.RootElement.GetProperty("events")[0].GetProperty("sessionSummary").GetProperty("observedOpenMs").GetInt64());
        Assert.Equal(40000, failed.RootElement.GetProperty("events")[0].GetProperty("activitySummary").GetProperty("distanceMillimeters").GetInt64());
        for (var i = 0; i < SupplementaryClient.QueueCapacity; i++) Assert.True(client.TryEnqueue(f.Event()));
        var next = summary with { ObservedOpenMs = 10_000, ConnectedMs = 6_000, SessionAgeMs = 12_000 };
        Assert.False(client.TryEnqueue(f.Event() with { Kind = "heartbeat", SessionSummary = next }));
        Assert.Equal(10_000, next.ObservedOpenMs);
        transport.ForcedStatus = null;
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.True(client.TryEnqueue(f.Event() with { Kind = "heartbeat", SessionSummary = next }));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        using var delivered = JsonDocument.Parse(transport.Bodies[^1]);
        Assert.Equal(10_000, delivered.RootElement.GetProperty("events")[0].GetProperty("sessionSummary").GetProperty("observedOpenMs").GetInt64());
        Assert.Equal(4, delivered.RootElement.GetProperty("batchSequence").GetInt64());
    }

    [Fact]
    public async Task ExpandedNormalCollectionPlusFeatureIncidentsFitsTwoBoundedDeliveries()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = Client(f, transport);
        var collection = FullCollection(f).ToArray();
        Assert.Equal(58, collection.Length);
        foreach (var value in collection) Assert.True(client.TryEnqueue(value));
        var count = collection.Length;
        // Actual fixed summaries plus a bounded feature/incident burst, not stand-ins for health measurements.
        for (var i = 0; i < 40; i++)
        {
            Assert.True(client.TryEnqueue(f.Event() with
            {
                Kind = "exception",
                Outcome = "failure",
                Stage = "continuing-exception",
                Incident = SupplementaryIncidentSchema.Create("managed-exception")
            }));
            count++;
        }
        Assert.InRange(count, 65, SupplementaryClient.QueueCapacity);
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, client.PendingEvents);
        Assert.Equal(0, client.DroppedEvents);
        Assert.Equal(count, transport.Bodies.Sum(bytes =>
        { using var document = JsonDocument.Parse(bytes); return document.RootElement.GetProperty("events").GetArrayLength(); }));
        Assert.All(transport.Bodies, bytes => Assert.InRange(bytes.Length, 1, SupplementarySchema.MaximumBatchBytes));
    }

    [Fact]
    public async Task PriorityReservationAndEvictionPreserveIncidentAndOperationOrder()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = Client(f, transport);
        var periodic = f.Event() with { Kind = "resource", Feature = "app", Outcome = "none", Stage = "cpu", DurationMs = null, Value = 2 };
        for (var index = 0; index < 112; index++) Assert.True(client.TryEnqueue(periodic with { EventId = Guid.NewGuid() }));
        Assert.False(client.TryEnqueue(periodic with { EventId = Guid.NewGuid() }));
        var priority = Enumerable.Range(0, 40).Select(_ => f.Event()).ToArray();
        foreach (var value in priority) Assert.True(client.TryEnqueue(value));
        Assert.Equal(128, client.PendingEvents);
        Assert.Equal(25, client.DroppedEvents); // One rejected periodic report and 24 displaced periodic reports.
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        var delivered = transport.Bodies.SelectMany(bytes =>
        {
            using var body = JsonDocument.Parse(bytes);
            return body.RootElement.GetProperty("events").EnumerateArray()
                .Where(value => value.GetProperty("kind").GetString() == "connection")
                .Select(value => value.GetProperty("eventId").GetGuid()).ToArray();
        });
        Assert.Equal(priority.Select(value => value.EventId), delivered);
    }

    [Fact]
    public async Task BytePressureKeepsPriorityResidualsAcrossCollectionsAndDoesNotChangeInflightBatch()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = Client(f, transport);
        foreach (var value in FullCollection(f)) Assert.True(client.TryEnqueue(value));
        var incident = LargeIncident();
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        var priority = Enumerable.Range(0, 96).Select(_ => f.Event() with
        { Kind = "exception", Outcome = "failure", Stage = "continuing-exception", Incident = incident }).ToArray();
        foreach (var value in priority) Assert.True(client.TryEnqueue(value));
        Assert.Equal(128, client.PendingEvents);
        var sending = client.FlushBatchAsync(TestContext.Current.CancellationToken);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var inflight = transport.Bodies.Single().ToArray();
        foreach (var value in FullCollection(f)) client.TryEnqueue(value);
        var added = f.Event() with { Kind = "exception", Outcome = "failure", Stage = "continuing-exception", Incident = incident };
        Assert.True(client.TryEnqueue(added));
        Assert.Equal(inflight, transport.Bodies.Single());
        transport.Pause.SetResult();
        Assert.True(await sending);
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.True(client.PendingEvents > 0); // Two byte-bounded sends do not pretend to drain large incidents.
        for (var cycle = 0; cycle < 8 && client.PendingEvents > 0; cycle++)
        {
            foreach (var value in FullCollection(f)) client.TryEnqueue(value);
            Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
            Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        }
        var ids = transport.Bodies.SelectMany(bytes =>
        {
            using var body = JsonDocument.Parse(bytes);
            return body.RootElement.GetProperty("events").EnumerateArray()
                .Where(value => value.GetProperty("kind").GetString() == "exception")
                .Select(value => value.GetProperty("eventId").GetGuid()).ToArray();
        }).ToArray();
        Assert.Equal(priority.Select(value => value.EventId).Append(added.EventId), ids);
        Assert.Equal(0, client.PendingEvents);
        Assert.True(client.DroppedEvents > 0);
        Assert.All(transport.Bodies, bytes => Assert.InRange(bytes.Length, 1, SupplementarySchema.MaximumBatchBytes));
    }

    [Fact]
    public void FullPriorityQueueRejectsNewWorkWithoutEvictingAcceptedPriority()
    {
        using var f = new SupplementaryFixture();
        using var client = Client(f, new StubTransport());
        for (var index = 0; index < 128; index++) Assert.True(client.TryEnqueue(f.Event()));
        Assert.False(client.TryEnqueue(f.Event()));
        Assert.Equal(128, client.PendingEvents);
        Assert.Equal(1, client.DroppedEvents);
    }

    [Fact]
    public void WireTimestampsUseServiceMillisecondPrecisionAndGameVersionsPreserveFourParts()
    {
        using var f = new SupplementaryFixture();
        var value = f.Event() with { ObservedAt = f.Now.AddTicks(1234567), GameBuild = "6.461.0.0" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SupplementarySchema.Json);
        using var body = JsonDocument.Parse(bytes);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", body.RootElement.GetProperty("observedAt").GetString()!);
        Assert.Equal("6.461.0.0", body.RootElement.GetProperty("gameBuild").GetString());
        Assert.True(SupplementarySchema.Valid(value, f.Now));
    }

    [Fact]
    public async Task UnconfiguredRuntimeDoesNoCollectionOrNetworkWork()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = new SupplementaryClient(SupplementaryConfiguration.Unconfigured,
            new SupplementaryContentStore(new(new Dictionary<string, byte[]>()), null), new FixedClock(f.Now), transport);
        client.Start();
        Assert.False(client.IsConfigured);
        Assert.False(client.TryEnqueue(f.Event()));
        await client.Completion;
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task QueueIsBoundedBatchesAreBoundedAndStopDiscardsPendingWork()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = Client(f, transport);
        for (var i = 0; i < 200; i++) client.TryEnqueue(f.Event());
        Assert.Equal(128, client.PendingEvents);
        Assert.Equal(72, client.DroppedEvents);
        await client.FlushBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(64, client.PendingEvents);
        using var body = JsonDocument.Parse(transport.Bodies.Single());
        Assert.Equal(64, body.RootElement.GetProperty("events").GetArrayLength());
        Assert.Equal(1, body.RootElement.GetProperty("batchSequence").GetInt64());
        Assert.True(transport.Bodies[0].Length <= 64 * 1024);
        client.Stop();
        Assert.Equal(0, client.PendingEvents);
        Assert.False(client.TryEnqueue(f.Event()));
    }

    [Fact]
    public async Task LargeValidEventsRespectByteCapAndKeepResidualEventsForTheNextSequence()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = Client(f, transport);
        var bins = Enumerable.Repeat(62500, 16).ToImmutableArray();
        for (var i = 0; i < 64; i++) Assert.True(client.TryEnqueue(f.Event() with
        {
            AppVersion = "65535.65535.65535",
            BuildId = new string('a', 64),
            Kind = "exception",
            Stage = "renderer-queue",
            GameBuild = "999999999.999999999.999999999",
            RenderMode = "cpu",
            DurationMs = 604800000,
            Value = 1000000000000,
            Measurements = new(1000000, 1.234567891, 12345.67891234, 45678.91234567, 56789.12345678, 59999.123456789, 23456.78912345, bins),
            RelatedRun = new(Guid.NewGuid(), "65535.65535.65535", new string('b', 64), "private")
        }));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.InRange(client.PendingEvents, 1, 63);
        Assert.True(transport.Bodies[0].Length <= 64 * 1024);
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, client.PendingEvents);
        Assert.Equal(0, client.DroppedEvents);
        using var next = JsonDocument.Parse(transport.Bodies[1]);
        Assert.Equal(2, next.RootElement.GetProperty("batchSequence").GetInt64());
    }

    [Fact]
    public async Task SignedReportingDisableClearsQueueWithoutChangingAnyCoreSetting()
    {
        using var f = new SupplementaryFixture();
        var p = f.Payload(); p["reportingDisabled"] = true;
        var transport = new StubTransport { Content = f.Envelope(p) };
        using var client = Client(f, transport);
        Assert.True(client.TryEnqueue(f.Event()));
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.False(client.IsReportingEnabled);
        Assert.Equal(0, client.PendingEvents);
        Assert.False(client.TryEnqueue(f.Event()));
        Assert.Single(transport.Routes);
    }

    [Fact]
    public void AutomaticSchemasRejectArbitraryCategoriesAndNonFiniteNumbers()
    {
        using var f = new SupplementaryFixture();
        Assert.True(SupplementarySchema.Valid(f.Event(), f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { Kind = "raw-packet" }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { Stage = "arbitrary user value" }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { Value = double.NaN }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { DurationMs = -1 }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { EventId = Guid.Empty }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { ObservedAt = f.Now.AddDays(-2) }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { BuildId = "user provided data" }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { BuildId = new string('a', 65) }, f.Now));
        Assert.False(SupplementarySchema.Valid(f.Event() with { BuildId = "-test" }, f.Now));
        Assert.True(SupplementarySchema.Valid(f.Event() with { BuildId = new string('a', 64) }, f.Now));
    }

    [Fact]
    public void HistogramRequiresCountAgreementOrderedBoundsAndCorrectUnits()
    {
        using var f = new SupplementaryFixture();
        var bins = new int[16]; bins[2] = 1; bins[3] = 3;
        var measurements = new SupplementaryMeasurements(4, 3, 7, 7, 7, 7, 6, bins.ToImmutableArray());
        Assert.True(SupplementarySchema.Valid(f.Event() with { Stage = "renderer-queue", Measurements = measurements }, f.Now));
        Assert.False((measurements with { Count = 5 }).IsValid);
        Assert.False((measurements with { P95Ms = 8 }).IsValid);
        Assert.False((measurements with { Histogram = ImmutableArray<int>.Empty }).IsValid);
        Assert.False(SupplementarySchema.Valid(f.Event() with { Stage = "working-set", Measurements = measurements }, f.Now));
        Assert.Equal(16, SupplementaryMeasurements.BucketUpperBounds.Length);
        Assert.Equal(60000, SupplementaryMeasurements.BucketUpperBounds[^1]);
    }

    [Fact]
    public async Task RetryLimitDropsBatchWithoutGrowingDiskBacklog()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { ForcedStatus = SupplementaryRequestStatus.Unavailable };
        var delays = new List<TimeSpan>();
        using var client = new SupplementaryClient(new(new Uri("https://console.example.invalid/")),
            new(f.Verifier, f.Directory), new FixedClock(f.Now), transport, () => 0,
            (delay, cancellation) => { cancellation.ThrowIfCancellationRequested(); delays.Add(delay); return Task.CompletedTask; });
        client.TryEnqueue(f.Event());
        await client.FlushBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, transport.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) }, delays);
        Assert.Equal(1, client.DroppedEvents);
        Assert.Equal(0, client.PendingEvents);
        Assert.Equal(transport.Bodies[0], transport.Bodies[1]);
        Assert.Equal(transport.Bodies[0], transport.Bodies[2]);
        transport.ForcedStatus = null;
        Assert.True(client.TryEnqueue(f.Event()));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        using var next = JsonDocument.Parse(transport.Bodies[^1]);
        Assert.Equal(2, next.RootElement.GetProperty("batchSequence").GetInt64());
        Assert.False(Directory.Exists(f.Directory));
    }

    [Fact]
    public async Task OneClientOwnsOneRunAndKeepsOriginalEvidenceIdentitySeparate()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = Client(f, transport);
        var current = f.Event();
        Assert.True(client.TryEnqueue(current));
        Assert.False(client.TryEnqueue(current with { SessionId = Guid.NewGuid() }));
        Assert.False(client.TryEnqueue(current with { InstallationId = Guid.NewGuid() }));
        Assert.False(client.TryEnqueue(current with { AppVersion = "2.6.5" }));
        Assert.False(client.TryEnqueue(current with { BuildId = "other-build" }));
        Assert.False(client.TryEnqueue(current with { Channel = "private" }));
        var prior = new SupplementaryRelatedRun(Guid.NewGuid(), "2.6.5", "previous-build", "private");
        var historical = current with
        {
            EventId = Guid.NewGuid(),
            Kind = "exception",
            Stage = "fatal-exception",
            ObservedAt = f.Now.AddHours(-1),
            RelatedRun = prior
        };
        Assert.True(client.TryEnqueue(historical));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        using var body = JsonDocument.Parse(transport.Bodies.Single());
        var events = body.RootElement.GetProperty("events");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal(current.SessionId, events[1].GetProperty("sessionId").GetGuid());
        Assert.Equal(prior.SessionId, events[1].GetProperty("relatedRun").GetProperty("sessionId").GetGuid());
        Assert.Equal(prior.BuildId, events[1].GetProperty("relatedRun").GetProperty("buildId").GetString());
        Assert.Equal(f.Now.AddHours(-1), events[1].GetProperty("observedAt").GetDateTimeOffset());
        Assert.False(SupplementarySchema.Valid(current with { RelatedRun = prior }, f.Now));
        Assert.False(SupplementarySchema.Valid(historical with { RelatedRun = prior with { SessionId = Guid.Empty } }, f.Now));
        Assert.False(SupplementarySchema.Valid(historical with { RelatedRun = prior with { BuildId = "raw user text" } }, f.Now));
        Assert.False(SupplementarySchema.Valid(historical with { RelatedRun = prior with { AppVersion = "future" } }, f.Now));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":0,\"aggregated\":1,\"retainedDetails\":0,\"sequenceGaps\":0,\"replayed\":true}", true)]
    [InlineData("{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":0,\"futureField\":{\"optional\":true}}", true)]
    [InlineData("{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":0,\"accepted\":1}", false)]
    [InlineData("{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":1}", false)]
    [InlineData("{\"schemaVersion\":1,\"accepted\":0,\"duplicate\":0}", false)]
    [InlineData("{\"schemaVersion\":1,\"accepted\":-1,\"duplicate\":2}", false)]
    [InlineData("{\"schemaVersion\":1,\"accepted\":1}", false)]
    public async Task BatchReceiptAllowsStorageMetadataButRequiresUnambiguousAcknowledgement(string receipt, bool valid)
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { EventReceipt = Encoding.UTF8.GetBytes(receipt) };
        using var client = Client(f, transport);
        client.TryEnqueue(f.Event());
        Assert.Equal(valid, await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(valid ? 0 : 1, client.DroppedEvents);
    }

    [Fact]
    public async Task ConcurrentBatchFlushCannotReorderSequencesOrRemoveAnotherBatch()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = Client(f, transport);
        for (var i = 0; i < 80; i++) Assert.True(client.TryEnqueue(f.Event()));
        var first = client.FlushBatchAsync(TestContext.Current.CancellationToken);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(16, client.PendingEvents);
        transport.Pause.SetResult();
        Assert.True(await first);
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.MaximumConcurrent);
        using var body = JsonDocument.Parse(transport.Bodies[^1]);
        Assert.Equal(2, body.RootElement.GetProperty("batchSequence").GetInt64());
        Assert.Equal(16, body.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task UpdateExitFlushSendsQueuedFunnelEventsInOrderWithServerClock()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { ServerTime = f.Now };
        using var client = Client(f, transport);
        var events = Enumerable.Range(0, SupplementaryClient.QueueCapacity).Select(i => f.Event() with
        {
            Kind = "update",
            Feature = "update",
            Stage = i % 2 == 0 ? "prompt" : "chosen",
            Outcome = "success"
        }).ToArray();
        foreach (var value in events) Assert.True(client.TryEnqueue(value));

        Assert.True(await client.FlushForUpdateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, client.PendingEvents);
        Assert.Equal(0, client.DroppedEvents);
        Assert.Equal(1, transport.MaximumConcurrent);
        Assert.Equal("/api/v1/content", transport.Routes[0]);
        var received = new List<Guid>();
        for (var i = 0; i < transport.Bodies.Count; i++)
        {
            using var body = JsonDocument.Parse(transport.Bodies[i]);
            Assert.Equal(i + 1, body.RootElement.GetProperty("batchSequence").GetInt64());
            received.AddRange(body.RootElement.GetProperty("events").EnumerateArray().Select(value => value.GetProperty("eventId").GetGuid()));
        }
        Assert.Equal(events.Select(value => value.EventId), received);
    }

    [Fact]
    public async Task UpdateExitFlushWaitsForExistingBatchWithoutReordering()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { ServerTime = f.Now };
        using var client = Client(f, transport);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        transport.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < 80; i++) Assert.True(client.TryEnqueue(f.Event()));
        var regular = client.FlushBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(16, client.PendingEvents);
        var exit = client.FlushForUpdateAsync(TestContext.Current.CancellationToken);
        Assert.False(exit.IsCompleted);
        transport.Pause.SetResult();
        Assert.True(await regular);
        Assert.True(await exit);
        Assert.Equal(0, client.PendingEvents);
        Assert.Equal(1, transport.MaximumConcurrent);
        using var last = JsonDocument.Parse(transport.Bodies[^1]);
        Assert.Equal(2, last.RootElement.GetProperty("batchSequence").GetInt64());
        Assert.Equal(16, last.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task UpdateExitFlushDoesNotWaitForRegularRetryBackoff()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { ServerTime = f.Now };
        using var client = new SupplementaryClient(new(new Uri("https://console.example.invalid/")), new(f.Verifier, f.Directory),
            new FixedClock(f.Now), transport, delay: (_, _) => throw new InvalidOperationException("Update exit must not wait for retry backoff."));
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        transport.ForcedStatus = SupplementaryRequestStatus.Unavailable;
        Assert.True(client.TryEnqueue(f.Event()));
        Assert.False(await client.FlushForUpdateAsync(TestContext.Current.CancellationToken));
        Assert.Single(transport.Bodies);
        Assert.Equal(1, client.DroppedEvents);
    }

    [Fact]
    public async Task UpdateExitFlushBoundsUncooperativeTransportAndReleasesTheGate()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { ServerTime = f.Now };
        using var client = Client(f, transport);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        transport.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.IgnoreCancellation = true;
        Assert.True(client.TryEnqueue(f.Event()));
        try
        {
            Assert.False(await client.FlushForUpdateAsync(TestContext.Current.CancellationToken)
                .WaitAsync(SupplementaryClient.UpdateFlushTimeout + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        }
        finally { transport.Pause.SetResult(); }
        Assert.True(await client.FlushForUpdateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task UpdateExitFlushSkipsUncalibratedOrDisabledReporting()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = Client(f, transport);
        Assert.True(client.TryEnqueue(f.Event()));
        Assert.False(await client.FlushForUpdateAsync(TestContext.Current.CancellationToken));
        Assert.Empty(transport.Bodies);
        Assert.Equal(1, client.PendingEvents);
        client.Stop();
        Assert.False(await client.FlushForUpdateAsync(TestContext.Current.CancellationToken));
        Assert.Single(transport.Routes);
    }

    [Fact]
    public async Task StopCancelsInFlightRequestWithoutWaitingForIt()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = Client(f, transport);
        client.TryEnqueue(f.Event());
        var sending = client.FlushBatchAsync(TestContext.Current.CancellationToken);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        client.Stop();
        await sending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(client.IsReportingEnabled);
        Assert.Equal(0, client.PendingEvents);
    }

    [Fact]
    public async Task SupportRequiresRedactedPayloadAndValidDurableReceipt()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport();
        using var client = Client(f, transport);
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), f.Now,
            "2.6.6", "test", "stable", "bug", "Connection failed", "secret=fixture-value at C:\\private\\fixture.txt");
        Assert.Equal(SupplementaryRequestStatus.Invalid, (await client.SubmitSupportAsync(report, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, transport.Calls);
        var preview = SupplementarySchema.RedactForPreview(report);
        Assert.DoesNotContain("fixture-value", preview.Message);
        Assert.DoesNotContain("fixture.txt", preview.Message);
        var result = await client.SubmitSupportAsync(preview, TestContext.Current.CancellationToken);
        Assert.Equal(SupplementaryRequestStatus.Success, result.Status);
        Assert.Equal("WISP-ABCDEF", result.Reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupportPauseRevokesPendingSendWithoutLosingTheReviewedRetryOrAcceptedReceipt(bool alreadyAccepted)
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport
        {
            Pause = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CompleteAcceptedSupport = alreadyAccepted
        };
        using var client = Client(f, transport);
        await using var controller = new AppController(new AppSettings(), _ => { }, new NoSupportStartup());
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), f.Now,
            "2.6.8", "test", "stable", "feedback", "Example message", "Keep this reviewed message.");
        var originalPermission = controller.SupportFeatureToken;
        using var permission = CancellationTokenSource.CreateLinkedTokenSource(originalPermission, TestContext.Current.CancellationToken);
        var pending = client.SubmitSupportAsync(report, permission.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await controller.ApplyFeatureControlsAsync(new(1,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("support", "Messages are paused.")));
        Assert.True(originalPermission.IsCancellationRequested);
        Assert.False(controller.FeatureAllowed("support"));
        transport.Pause.SetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(alreadyAccepted ? SupplementaryRequestStatus.Success : SupplementaryRequestStatus.Cancelled, result.Status);
        Assert.Equal(alreadyAccepted ? "WISP-ABCDEF" : null, result.Reference);
        await controller.ApplyFeatureControlsAsync(new(2, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty));
        Assert.True(originalPermission.IsCancellationRequested);
        Assert.False(controller.SupportFeatureToken.IsCancellationRequested);
        Assert.Equal(1, transport.Calls);
        using var retryPermission = CancellationTokenSource.CreateLinkedTokenSource(controller.SupportFeatureToken, TestContext.Current.CancellationToken);
        var retry = await client.SubmitSupportAsync(report, retryPermission.Token);
        Assert.Equal(SupplementaryRequestStatus.Success, retry.Status);
        Assert.Equal("WISP-ABCDEF", retry.Reference);
        Assert.Equal(transport.Bodies[0], transport.Bodies[1]);
    }

    private sealed class NoSupportStartup : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    [Theory]
    [InlineData("contact@example.invalid")]
    [InlineData("10.1.2.3")]
    [InlineData("AB:CD:EF:12:34:56")]
    [InlineData("token=fixture-secret")]
    [InlineData("/home/fixture/private")]
    [InlineData("https://example.invalid/secret")]
    public void SupportRedactionRemovesKnownSensitivePatterns(string value)
    {
        var output = SupplementarySchema.Redact("Example " + value);
        Assert.DoesNotContain(value, output);
        Assert.Contains("[REDACTED]", output);
    }

    [Theory]
    [InlineData("bug")]
    [InlineData("suggestion")]
    [InlineData("feedback")]
    public async Task SupportAcceptsOnlyInboxCategoriesAndReturnsServerQuota(string category)
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport
        {
            SupportResponse = new(SupplementaryRequestStatus.Success,
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"reference\":\"WSP-12345678901234567890123456789012\",\"nextAllowedAt\":\"2026-10-11T00:00:00.000Z\",\"quota\":{\"policy\":\"installation-utc-day\",\"limit\":1}}"), 201)
        };
        using var client = Client(f, transport);
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), f.Now,
            "2.6.6", "test", "stable", category, "Example message", "A useful observation.");
        Assert.False(SupplementarySchema.Valid(report with { Category = "anything" }, f.Now));
        var result = await client.SubmitSupportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(SupplementaryRequestStatus.Success, result.Status);
        Assert.Equal(f.Now.Date.AddDays(1), result.NextAllowedAt!.Value.UtcDateTime);
        Assert.Equal(36, result.Reference!.Length);
    }

    [Fact]
    public async Task SupportDailyLimitIsDistinctFromCapacityAndUncertainRetryKeepsExactBody()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { ForcedStatus = SupplementaryRequestStatus.TimedOut };
        using var client = Client(f, transport);
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), f.Now,
            "2.6.6", "test", "stable", "feedback", "Example message", "A useful observation.");
        Assert.Equal(SupplementaryRequestStatus.TimedOut, (await client.SubmitSupportAsync(report, TestContext.Current.CancellationToken)).Status);
        transport.ForcedStatus = null;
        var limit = "{\"schemaVersion\":1,\"error\":{\"code\":\"support_daily_limit\",\"message\":\"One message per day.\",\"nextAllowedAt\":\"2026-10-11T00:00:00.000Z\",\"retryAfterSeconds\":43200}}";
        transport.SupportResponse = new(SupplementaryRequestStatus.Unavailable, Encoding.UTF8.GetBytes(limit), 429);
        var result = await client.SubmitSupportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(SupplementaryRequestStatus.DailyLimit, result.Status);
        Assert.NotNull(result.NextAllowedAt);
        Assert.Equal(transport.Bodies[0], transport.Bodies[1]);
        var capacity = limit.Replace("support_daily_limit", "capacity_exhausted", StringComparison.Ordinal);
        Assert.Equal(SupplementaryRequestStatus.Unavailable, SupplementarySupportReceipts.Read(
            new(SupplementaryRequestStatus.Unavailable, Encoding.UTF8.GetBytes(capacity), 429), f.Now).Status);
        Assert.Equal(SupplementaryRequestStatus.Unavailable, SupplementarySupportReceipts.Read(
            new(SupplementaryRequestStatus.Success, Encoding.UTF8.GetBytes("{}"), 201), f.Now).Status);
        Assert.Equal(SupplementaryRequestStatus.Unavailable, SupplementarySupportReceipts.Read(
            new(SupplementaryRequestStatus.Unavailable, Encoding.UTF8.GetBytes(limit.Replace("2026-10-11", "2099-10-11", StringComparison.Ordinal)), 429), f.Now).Status);
    }

    [Fact]
    public async Task SupportHttp429BodyIsBoundedAndAvailableForQuotaParsingOnly()
    {
        const string body = "{\"schemaVersion\":1,\"error\":{\"code\":\"support_daily_limit\",\"message\":\"One message per day.\",\"nextAllowedAt\":\"2026-10-11T00:00:00.000Z\",\"retryAfterSeconds\":43200}}";
        using var transport = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() => new(HttpStatusCode.TooManyRequests) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var response = await transport.SendAsync("/api/v1/support", [1], 2048, TestContext.Current.CancellationToken);
        Assert.Equal(429, response.HttpStatus);
        Assert.Equal(body, Encoding.UTF8.GetString(response.Body));
        Assert.Equal(SupplementaryRequestStatus.DailyLimit, SupplementarySupportReceipts.Read(response, new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)).Status);
        Assert.Empty((await transport.SendAsync("/api/v1/events", [1], 2048, TestContext.Current.CancellationToken)).Body);
        Assert.Empty((await transport.SendAsync("/api/v1/support", [1], 32, TestContext.Current.CancellationToken)).Body);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(100)]
    public void VersionTwoSupportQuotaUsesServerTimeInsteadOfWrongLocalClock(int skewDays)
    {
        var server = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var success = "{\"schemaVersion\":1,\"reference\":\"WSP-12345678901234567890123456789012\",\"nextAllowedAt\":\"2026-10-11T00:00:00.000Z\",\"quota\":{\"policy\":\"installation-utc-day\",\"limit\":1},\"serverTime\":\"2026-10-10T12:00:00.000Z\",\"retryAfterSeconds\":43200}";
        var receipt = SupplementarySupportReceipts.Read(new(SupplementaryRequestStatus.Success, Encoding.UTF8.GetBytes(success), 201), server.AddDays(skewDays));
        Assert.Equal(SupplementaryRequestStatus.Success, receipt.Status);
        Assert.Equal(43200, receipt.RetryAfterSeconds);
        var quota = "{\"schemaVersion\":1,\"error\":{\"code\":\"support_daily_limit\",\"message\":\"One message per day.\",\"nextAllowedAt\":\"2026-10-11T00:00:00.000Z\",\"retryAfterSeconds\":43200,\"serverTime\":\"2026-10-10T12:00:00.000Z\"}}";
        Assert.Equal(SupplementaryRequestStatus.DailyLimit, SupplementarySupportReceipts.Read(new(SupplementaryRequestStatus.Unavailable, Encoding.UTF8.GetBytes(quota), 429), server.AddDays(skewDays)).Status);
        Assert.Equal(SupplementaryRequestStatus.Unavailable, SupplementarySupportReceipts.Read(new(SupplementaryRequestStatus.Success, Encoding.UTF8.GetBytes(success.Replace("43200", "43201", StringComparison.Ordinal)), 201), server).Status);
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), server.AddDays(skewDays), "2.6.6", "test", "stable", "feedback", "Example", "Useful observation.");
        Assert.True(SupplementarySchema.Valid(report, server));
        Assert.False(SupplementarySchema.Valid(report with { ReceiptVersion = 3 }, server));
    }

    [Fact]
    public async Task ClockErrorsAreCategorizedWithoutReturningArbitraryServerMessages()
    {
        using var transport = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() => new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"schemaVersion\":1,\"error\":{\"code\":\"invalid_time\",\"message\":\"untrusted diagnostic text\"}}", Encoding.UTF8, "application/json") }));
        var response = await transport.SendAsync("/api/v1/events", [1], 2048, TestContext.Current.CancellationToken);
        Assert.Equal(SupplementaryRequestStatus.ClockSkew, response.Status);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task HttpTransportRejectsRedirectsOversizedAndUnexpectedResponses()
    {
        using var redirect = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://other.example.invalid/") } }));
        Assert.Equal(SupplementaryRequestStatus.Rejected, (await redirect.SendAsync("/api/v1/content", null, 2048, TestContext.Current.CancellationToken)).Status);
        using var oversized = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() => JsonResponse(new string('x', 4096))));
        Assert.Equal(SupplementaryRequestStatus.Rejected, (await oversized.SendAsync("/api/v1/content", null, 2048, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(SupplementaryRequestStatus.Invalid, (await oversized.SendAsync("/api/v1/admin/settings", [], 2048, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task HttpTransportUsesOnlyFixedOriginAndCancellation()
    {
        Uri? seen = null;
        using var transport = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() => JsonResponse("{}"), request => seen = request.RequestUri));
        await transport.SendAsync("/api/v1/content", null, 2048, TestContext.Current.CancellationToken);
        Assert.Equal("https://console.example.invalid/api/v1/content", seen?.AbsoluteUri);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(SupplementaryRequestStatus.Cancelled, (await transport.SendAsync("/api/v1/content", null, 2048, cancelled.Token)).Status);
    }

    [Fact]
    public async Task UnknownLengthResponseIsBoundedWhileStreaming()
    {
        var content = new UnknownLengthContent(new byte[4096]);
        content.Headers.ContentType = new("application/json");
        using var transport = new SupplementaryHttpTransport(new("https://console.example.invalid/"), TimeProvider.System,
            new ResponseHandler(() => new(HttpStatusCode.OK) { Content = content }));
        Assert.Equal(SupplementaryRequestStatus.Rejected,
            (await transport.SendAsync("/api/v1/content", null, 2048, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(2049, content.ReadBytes);
    }

    [Fact]
    public async Task SupportAndEventsNeverOverlapRequests()
    {
        using var f = new SupplementaryFixture();
        var transport = new StubTransport { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = Client(f, transport);
        client.TryEnqueue(f.Event());
        var first = client.FlushBatchAsync(TestContext.Current.CancellationToken);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var second = client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.Calls);
        transport.Pause.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    private static IEnumerable<SupplementaryEvent> FullCollection(SupplementaryFixture f)
    {
        var histogram = new int[16]; histogram[1] = 100;
        var measurements = new SupplementaryMeasurements(100, 1.5, 2, 2, 2, 2, 1.9, histogram.ToImmutableArray());
        foreach (var metric in Enum.GetValues<Wisp.App.DebugLogging.ToolsPerformanceMetric>())
        {
            var modes = !SupplementaryPerformanceProjection.HasRenderMode(metric) ? new string?[] { null }
                : metric is Wisp.App.DebugLogging.ToolsPerformanceMetric.CompositorMotionWork or Wisp.App.DebugLogging.ToolsPerformanceMetric.CompositorMotionSourceAge
                    ? new string?[] { "gpu" } : new string?[] { "cpu", "gpu" };
            foreach (var mode in modes) yield return f.Event() with
            {
                Kind = "resource",
                Feature = SupplementaryPerformanceProjection.Feature(metric),
                Outcome = "none",
                Stage = SupplementaryPerformanceProjection.Stage(metric),
                DurationMs = null,
                Measurements = measurements,
                RenderMode = mode
            };
        }
        string[] counters = ["accepted-packets", "rejected-packets", "native-attempts", "native-failures", "renderer-submissions",
            "renderer-busy", "renderer-occluded", "renderer-failures", "queue-dropped", "collector-failures", "received-datagrams",
            "drained-datagrams", "processed-packets", "gc-gen2-collections", "health-samples", "dispatcher-pending-samples"];
        foreach (var stage in counters) yield return f.Event() with
        { Kind = "resource", Feature = "app", Outcome = "none", Stage = stage, DurationMs = 60000, Value = 1 };
        foreach (var stage in new[] { "cpu", "working-set", "managed-heap" })
        {
            yield return f.Event() with { Kind = "resource", Feature = "app", Outcome = "none", Stage = stage, DurationMs = null, Value = 2 };
            yield return f.Event() with { Kind = "resource", Feature = "app", Outcome = "none", Stage = stage, DurationMs = null, Value = 2, SessionAgeMs = 60000 };
        }
        foreach (var stage in new[] { "telemetry-age", "native-age", "dispatcher", "ui-heartbeat-age", "composition-maximum-gap", "health-collection-gap", "composition-callback-age" })
            yield return f.Event() with { Kind = "resource", Feature = "app", Outcome = "none", Stage = stage, DurationMs = null, Measurements = measurements };
        yield return f.Event() with
        {
            Kind = "heartbeat",
            Feature = "app",
            Outcome = "fresh",
            Stage = "ready",
            DurationMs = null,
            SessionSummary = new(60000, 10000, 50000, 0, 0, 60000),
            ActivitySummary = new(30000, 20000, 10000, 0, 60000, 300000, 20000, 2, "moving")
        };
        yield return f.Event() with { Outcome = "fresh", Stage = "data-out", DurationMs = null };
    }

    private static SupplementaryIncident LargeIncident()
    {
        var methods = SupplementaryIncidentSchema.Methods.OrderByDescending(value => value.Length).Take(4).ToImmutableArray();
        return SupplementaryIncidentSchema.Create("recorder", "encoder_failed", "video_output", uint.MaxValue,
            Enumerable.Range(0, 3).Select(_ => new SupplementaryIncidentException("ApplicationUpdateHelperUnavailableException", uint.MaxValue, methods)).ToImmutableArray(),
            new[] { 30000, 15000, 0 }.Select(age => new SupplementaryIncidentContext(age, "fresh", 99.123456789, 1_000_000_000_000,
                1_000_000_000_000, 59999.123456789, 59999.123456789, 59999.123456789)).ToImmutableArray(),
            new(1_000_000_000_000, 1_000_000_000_000, 1_000_000_000_000, 59999.123456789, 59999.123456789, 59999.123456789));
    }

    private static SupplementaryClient Client(SupplementaryFixture fixture, StubTransport transport) =>
        new(new(new Uri("https://console.example.invalid/")), new(fixture.Verifier, fixture.Directory), new FixedClock(fixture.Now), transport, () => 0);
    private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class ResponseHandler(Func<HttpResponseMessage> response, Action<HttpRequestMessage>? observe = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); observe?.Invoke(request); return Task.FromResult(response()); }
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        internal int ReadBytes;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CountingStream(bytes, count => ReadBytes += count));
        private sealed class CountingStream(byte[] bytes, Action<int> observe) : MemoryStream(bytes)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            { var count = await base.ReadAsync(buffer, cancellationToken); observe(count); return count; }
        }
    }
    private sealed class StubTransport : ISupplementaryTransport
    {
        internal int Calls, MaximumConcurrent;
        private int _concurrent;
        internal readonly List<byte[]> Bodies = [];
        internal readonly List<string> Routes = [];
        internal byte[]? Content;
        internal byte[]? EventReceipt;
        internal DateTimeOffset? ServerTime;
        internal SupplementaryResponse? SupportResponse;
        internal SupplementaryRequestStatus? ForcedStatus;
        internal TaskCompletionSource? Pause;
        internal bool CompleteAcceptedSupport;
        internal bool IgnoreCancellation;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SupplementaryResponse> SendAsync(string route, byte[]? body, int maximumResponseBytes, CancellationToken cancellation)
        {
            Calls++; Routes.Add(route); MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _concurrent));
            if (body is not null) Bodies.Add(body);
            Entered.TrySetResult();
            try
            {
                if (Pause is not null) await Pause.Task.WaitAsync(IgnoreCancellation || CompleteAcceptedSupport && route.EndsWith("support", StringComparison.Ordinal)
                    ? TestContext.Current.CancellationToken : cancellation);
                if (ForcedStatus is { } forced) return new(forced, []);
                if (route.EndsWith("content", StringComparison.Ordinal)) return new(SupplementaryRequestStatus.Success, Content ?? [], ServerTime: ServerTime, UncertaintyMs: 1);
                if (route.EndsWith("support", StringComparison.Ordinal)) return SupportResponse ?? new(SupplementaryRequestStatus.Success, Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"reference\":\"WISP-ABCDEF\"}"));
                if (EventReceipt is not null) return new(SupplementaryRequestStatus.Success, EventReceipt);
                using var doc = JsonDocument.Parse(body!);
                return new(SupplementaryRequestStatus.Success, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    accepted = doc.RootElement.GetProperty("events").GetArrayLength(),
                    duplicate = 0
                }));
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public void Dispose() { }
    }
}
