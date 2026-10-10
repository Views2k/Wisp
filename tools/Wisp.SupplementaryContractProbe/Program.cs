using System.Collections.Immutable;
using System.Text.Json;
using Wisp.App.Supplementary;

if (args.Length != 2 || args[0] is not ("generate" or "verify"))
    throw new ArgumentException("Use generate <fixture.json> or verify <signed-fixture.json>.");

var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
if (args[0] == "generate")
{
    var session = Guid.Parse("00000000-0000-4000-8000-000000000001");
    var installation = Guid.Parse("00000000-0000-4000-8000-000000000002");
    var histogram = Enumerable.Repeat(0, 16).ToArray(); histogram[0] = 1; histogram[1] = 2; histogram[2] = 1;
    var measurements = new SupplementaryMeasurements(4, 1, 1.5, 3, 3, 3, 1.875, histogram.ToImmutableArray());
    var events = Enumerable.Range(1, SupplementarySchema.MaximumBatchEvents).Select(index =>
        new SupplementaryEvent(1, Guid.Parse($"10000000-0000-4000-8000-{index:D12}"), session, installation, now,
            "2.6.6", "contract-fixture", "stable", "resource", "hud", "none", "steam", GameBuild: "6.461.691.0",
            Stage: "render-work", Measurements: measurements, RenderMode: "gpu")).ToArray();
    events[1] = events[1] with { Kind = "exception", Stage = "unexpected-exit", Measurements = null,
        RelatedRun = new(Guid.Parse("00000000-0000-4000-8000-000000000003"), "2.6.5", "prior-fixture", "stable") };
    events[2] = events[2] with { Kind = "udp-summary", Stage = "accepted-packets", Measurements = null, Value = 123 };
    events[3] = events[3] with { Kind = "startup", Stage = "first-telemetry", Measurements = null, DurationMs = 123.456 };
    events[4] = events[4] with { Kind = "heartbeat", Feature = "app", Stage = "ready", Measurements = null, RenderMode = null,
        SessionSummary = new(60000, 20000, 30000, 10000, 2000, 62000),
        ActivitySummary = new(30000, 20000, 10000, 2000, 62000, 300000, 15000, 20, "moving") };
    events[5] = events[5] with { Feature = "app", Stage = "working-set", Measurements = null, RenderMode = null, Value = 123456789, SessionAgeMs = 62000 };
    events[6] = events[6] with { Feature = "app", Stage = "managed-heap", Measurements = null, RenderMode = null, Value = 1234567, SessionAgeMs = 62000 };
    events[7] = events[7] with { Feature = "app", Stage = "cpu", Measurements = null, RenderMode = null, Value = 2.5, SessionAgeMs = 62000 };
    events[1] = events[1] with { Incident = SupplementaryIncidentSchema.Create("unexpected-exit") };
    events[8] = events[8] with { Kind = "exception", Feature = "app", Stage = "continuing-exception", Outcome = "failure", Measurements = null,
        Incident = SupplementaryIncidentSchema.Create("managed-exception", code: 0x80004005,
            exceptions: [new("InvalidOperationException", 0x80004005, ["Wisp.App.Clips.CompatibleClipExporter.Inspect"])],
            context: [new(2000, "fresh", 2.5, 123456789, 1234567, 20, 12, 15), new(0, "idle")]) };
    events[9] = events[9] with { Kind = "clips", Feature = "clips", Stage = "audio", Outcome = "failure", Measurements = null,
        Incident = SupplementaryIncidentSchema.Create("recorder", "audio_failed", "audio_encode", 5,
            recorder: new(10, 20, 30, 1.25, 2.5, 3.75)) };
    string[] diagnosticStages = ["compositor-motion-work", "compositor-motion-source-age", "telemetry-parse-work", "parse-to-ui-adoption",
        "receive-to-ui-adoption", "publish-to-ui-adoption", "ui-update-work", "native-ui-update-work", "native-observation-to-ui-adoption",
        "received-datagrams", "drained-datagrams", "processed-packets", "gc-gen2-collections", "health-samples", "dispatcher-pending-samples",
        "health-collection-gap", "composition-callback-age"];
    for (var i = 0; i < diagnosticStages.Length; i++)
    {
        var stage = diagnosticStages[i];
        var count = SupplementarySchema.CountStages.Contains(stage);
        events[10 + i] = events[10 + i] with { Stage = stage, Feature = "app", RenderMode = null,
            Measurements = count ? null : measurements, Value = count ? 2 : null };
    }
    var support = SupplementarySchema.RedactForPreview(new(1, Guid.Parse("20000000-0000-4000-8000-000000000001"),
        session, installation, now, "2.6.6", "contract-fixture", "stable", "feedback", "Contract fixture",
        "A synthetic report.\nNo personal information is included.", new("steam", "fresh", "none", "6.461.691.0")));
    if (!events.All(value => SupplementarySchema.Valid(value, now)) || !SupplementarySchema.Valid(support, now))
        throw new InvalidOperationException("The real client validator rejected its fixture.");
    var batch = new { schemaVersion = 1, batchSequence = 1, events };
    var batchBytes = JsonSerializer.SerializeToUtf8Bytes(batch, SupplementarySchema.Json);
    if (batchBytes.Length > SupplementarySchema.MaximumBatchBytes) throw new InvalidOperationException("Fixture exceeds batch bound.");
    var fixture = new { now, batch, support, batchBytes = batchBytes.Length,
        categories = new { kinds = SupplementarySchema.Kinds.Order(), features = SupplementarySchema.Features.Order(),
            outcomes = SupplementarySchema.Outcomes.Order(), stages = SupplementarySchema.Stages.Order(),
            platforms = SupplementarySchema.Platforms.Order(), channels = SupplementarySchema.Channels.Order() },
        histogramEdges = SupplementaryMeasurements.BucketUpperBounds,
        incidentCatalog = new { categories = SupplementaryIncidentSchema.Categories.Order(), reasons = SupplementaryIncidentSchema.Reasons.Order(),
            failureStages = SupplementaryIncidentSchema.FailureStages.Order(), exceptionTypes = SupplementaryIncidentSchema.ExceptionTypes.Order(),
            methods = SupplementaryIncidentSchema.Methods.Order() } };
    File.WriteAllBytes(args[1], JsonSerializer.SerializeToUtf8Bytes(fixture, SupplementarySchema.Json));
    Console.WriteLine($"Generated real-client fixture: {events.Length} events, {batchBytes.Length} batch bytes; no network requests.");
}
else
{
    var bytes = File.ReadAllBytes(args[1]);
    if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Fixture exceeds bound.");
    using var document = JsonDocument.Parse(bytes);
    var root = document.RootElement;
    var verificationTime = root.GetProperty("now").GetDateTimeOffset();
    var verifier = new SupplementaryContentVerifier(new Dictionary<string, byte[]> {
        [root.GetProperty("keyId").GetString()!] = Convert.FromBase64String(root.GetProperty("publicKey").GetString()!) });
    var audience = new SupplementaryAudienceContext("2.6.6", "stable", "steam", "6.461.691.0");
    var checkedCases = 0;
    foreach (var test in root.GetProperty("cases").EnumerateArray())
    {
        SupplementaryVerifiedContent? result = null;
        try { result = verifier.Verify(JsonSerializer.SerializeToUtf8Bytes(test.GetProperty("envelope")), verificationTime); }
        catch (SupplementaryValidationException) { }
        var expected = test.GetProperty("accepted").GetBoolean();
        if ((result is not null) != expected) throw new InvalidOperationException($"Signature contract case failed: {test.GetProperty("name").GetString()}");
        if (result is not null)
        {
            var snapshot = result.Snapshot;
            if (snapshot.ReportingDisabled != test.GetProperty("reportingDisabled").GetBoolean() ||
                snapshot.ForAudience(audience, verificationTime).Length != test.GetProperty("visibleItems").GetInt32())
                throw new InvalidOperationException("Signed pause or audience contract differs.");
            if (snapshot.ForAudience(audience with { GameBuild = "6.461.692.0" }, verificationTime).Length != 0 ||
                snapshot.ForAudience(audience, snapshot.ExpiresAt).Length != 0)
                throw new InvalidOperationException("Audience or expiry contract differs.");
            if (snapshot.Items.Length > 0 && snapshot.Items[0].StartsAt >= snapshot.IssuedAt)
                throw new InvalidOperationException("Republished content did not preserve its earlier start.");
        }
        checkedCases++;
    }
    if (checkedCases < 7) throw new InvalidOperationException("Expected the complete fixture set.");
    Console.WriteLine($"Verified {checkedCases} actual service-signed cases with the real client verifier; no network requests.");
}
