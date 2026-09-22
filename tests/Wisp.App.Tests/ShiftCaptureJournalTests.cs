using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureJournalTests
{
    [Theory]
    [InlineData("telemetry")]
    [InlineData("native_configuration")]
    [InlineData("controller_button")]
    [InlineData("cue_evaluation")]
    [InlineData("cue_submission")]
    [InlineData("cue_pixels")]
    public async Task ShiftSchemaRequiresActualUsableChannelsEvenWithoutCallerError(string missingKind)
    {
        await using var files = new CaptureFiles();
        var journal = files.Create(provenance: new { schema = "shift-capture-v2" });
        RecordRequiredChannels(journal, missingKind);
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.True(root.GetProperty("requiredChannelsCheckedFromRecordedEvidence").GetBoolean());
        Assert.False(root.GetProperty("requiredChannelsComplete").GetBoolean());
        Assert.False(root.GetProperty("captureComplete").GetBoolean());
        Assert.Contains(missingKind, root.GetProperty("missingRequiredChannels").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("missing-required-capture-channels", journal.Error);
        Assert.True(root.GetProperty("storageIntegrity").GetProperty("journalEventsLossless").GetBoolean());
    }

    [Fact]
    public async Task ShiftSchemaRejectsPresentButUnusableChannelRecords()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create(provenance: new { schema = "shift-capture-v2" });
        RecordRequiredChannels(journal, "cue_pixels");
        Assert.True(journal.TryRecord("cue_pixels", new { status = 5, qualified = false }));
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        Assert.False(manifest.RootElement.GetProperty("requiredChannelsComplete").GetBoolean());
        Assert.Equal("cue_pixels", Assert.Single(manifest.RootElement.GetProperty("missingRequiredChannels").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task ShiftSchemaRecognizesObservedChannelsWithoutCertifyingDrivingProtocol()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create(provenance: new { schema = "shift-capture-v2" });
        RecordRequiredChannels(journal);
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.True(root.GetProperty("requiredChannelsComplete").GetBoolean());
        Assert.False(root.GetProperty("basicDrivingChecklistRecorded").GetBoolean());
        Assert.False(root.GetProperty("basicDrivingChecklistSatisfied").GetBoolean());
        Assert.Contains("does not certify", root.GetProperty("captureCompleteMeaning").GetString(), StringComparison.Ordinal);
        Assert.Null(journal.Error);
    }

    [Fact]
    public async Task ReceiverLossDoesNotBecomeFalseJournalLossOrOverallCompleteness()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create(provenance: new { schema = "shift-capture-v2" });
        RecordRequiredChannels(journal);
        journal.MarkIncomplete("telemetry-drops");
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.True(root.GetProperty("storageIntegrity").GetProperty("journalEventsLossless").GetBoolean());
        Assert.Contains("Receiver datagram loss", root.GetProperty("storageIntegrity").GetProperty("scope").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("requiredChannelsComplete").GetBoolean());
        Assert.False(root.GetProperty("captureComplete").GetBoolean());
    }

    [Fact]
    public async Task MissingDrivingScenarioDoesNotMarkIntactRecordsAsChannelFailure()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.True(journal.TryRecord("test", new { value = 1 }));
        journal.MarkScenarioIncomplete("missing-driving-evidence");
        Assert.Null(journal.Error);
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.True(root.GetProperty("storageIntegrity").GetProperty("verified").GetBoolean());
        Assert.True(root.GetProperty("requiredChannelsComplete").GetBoolean());
        Assert.False(root.GetProperty("requiredChannelsCheckedFromRecordedEvidence").GetBoolean());
        Assert.False(root.GetProperty("basicDrivingChecklistSatisfied").GetBoolean());
        Assert.False(root.GetProperty("captureComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        Assert.Equal(1, root.GetProperty("audit").GetProperty("records").GetInt64());
        var report = archive.GetEntry("capture-report.txt");
        Assert.NotNull(report);
        using var stream = report.Open();
        Assert.Equal(root.GetProperty("report").GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(stream)));
        Assert.Throws<InvalidOperationException>(() => journal.MarkScenarioIncomplete("late"));
    }

    [Fact]
    public async Task RoundTripPreservesChannelsClockPayloadAndVerifiedHash()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.True(journal.TryRecord("telemetry", new { rpm = 7104.25, gear = 3, gameMilliseconds = 125 }, 500));
        Assert.True(journal.TryRecord("input-edge", new { button = "upshift", pressed = true }, 499));

        var archivePath = await journal.StopAndPackageAsync("user-stop");
        using var archive = ZipFile.OpenRead(archivePath);
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.True(root.GetProperty("captureComplete").GetBoolean());
        Assert.True(root.GetProperty("validation").GetProperty("jsonlVerified").GetBoolean());
        Assert.Equal(journal.SessionId, root.GetProperty("sessionId").GetString());
        Assert.Equal(Stopwatch.Frequency, root.GetProperty("stopwatchFrequency").GetInt64());
        Assert.Equal(journal.StartedTimestamp, root.GetProperty("startedTimestamp").GetInt64());
        Assert.Equal(2, root.GetProperty("writtenRecords").GetInt64());
        Assert.Equal(0, root.GetProperty("droppedRecords").GetInt64());
        Assert.DoesNotContain(journal.DirectoryPath, root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var lines = ReadLines(archive);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(1, first.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(2, second.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(500, first.RootElement.GetProperty("timestamp").GetInt64());
        Assert.Equal(499, second.RootElement.GetProperty("timestamp").GetInt64());
        Assert.Equal(7104.25, first.RootElement.GetProperty("payload").GetProperty("rpm").GetDouble());
        using var payload = archive.GetEntry("events.jsonl")!.Open();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), root.GetProperty("validation").GetProperty("sha256").GetString());
        Assert.Equal(1, journal.RecordedCounts["telemetry"]);
        Assert.Equal(1, journal.RecordedCounts["input-edge"]);
        Assert.Null(journal.Error);
        Assert.False(journal.IsAccepting);
    }

    [Fact]
    public async Task UnsupportedFloatingPointValuesRemainExplicitInTheEvidence()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.True(journal.TryRecord("native", new { unavailable = double.NaN, invalid = double.PositiveInfinity }));
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var document = JsonDocument.Parse(Assert.Single(ReadLines(archive)));
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("NaN", payload.GetProperty("unavailable").GetString());
        Assert.Equal("Infinity", payload.GetProperty("invalid").GetString());
        Assert.Null(journal.Error);
    }

    [Fact]
    public async Task SerializationRunsOffProducerAndOverflowDoesNotOverwriteAcceptedRecords()
    {
        await using var files = new CaptureFiles();
        using var payload = new BlockingPayload();
        var journal = files.Create(capacity: 1);
        var producerThread = Environment.CurrentManagedThreadId;
        Assert.True(journal.TryRecord("telemetry", payload));
        try
        {
            Assert.True(payload.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.NotEqual(producerThread, payload.SerializationThread);
            Assert.True(journal.TryRecord("telemetry", new { value = 2 }));
            Assert.False(journal.TryRecord("telemetry", new { value = 3 }));
            Assert.Equal(1, journal.DroppedRecords);
        }
        finally { payload.Release.Set(); }

        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.False(root.GetProperty("captureComplete").GetBoolean());
        Assert.Equal("queue-overflow", root.GetProperty("error").GetString());
        Assert.Equal(3, root.GetProperty("attemptedRecords").GetInt64());
        Assert.Equal(2, root.GetProperty("acceptedRecords").GetInt64());
        Assert.Equal(2, root.GetProperty("writtenRecords").GetInt64());
        Assert.Equal(1, root.GetProperty("queueDroppedRecords").GetInt64());
        var lines = ReadLines(archive);
        Assert.Equal(2, lines.Length);
        using var last = JsonDocument.Parse(lines[1]);
        Assert.Equal(2, last.RootElement.GetProperty("payload").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task ConcurrentProducersProduceOneStrictSequenceWithExactCounts()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create(capacity: 4096);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(producer => Task.Run(() =>
        {
            for (var sample = 0; sample < 200; sample++)
                Assert.True(journal.TryRecord("telemetry", new { producer, sample }));
        })));
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        var lines = ReadLines(archive);
        Assert.Equal(1600, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            Assert.Equal(index + 1, document.RootElement.GetProperty("sequence").GetInt64());
        }
        Assert.Equal(1600, journal.RecordedCounts["telemetry"]);
        Assert.Equal(0, journal.DroppedRecords);
    }

    [Fact]
    public async Task StopIsIdempotentAndLaterProducersCannotChangeFrozenEvidence()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.True(journal.TryRecord("telemetry", new { value = 1 }));
        var first = journal.StopAndPackageAsync("first-stop");
        var second = journal.StopAndPackageAsync("second-stop");
        Assert.Same(first, second);
        Assert.False(journal.TryRecord("telemetry", new { value = 2 }));
        using var archive = ZipFile.OpenRead(await first);
        using var manifest = ReadManifest(archive);
        Assert.Equal("first-stop", manifest.RootElement.GetProperty("finishReason").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("attemptedRecords").GetInt64());
        Assert.Single(ReadLines(archive));
    }

    [Fact]
    public async Task ByteCapPreservesCompleteEarlierLinesAndReportsAbandonedRecord()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create(maxBytes: 1024);
        Assert.True(journal.TryRecord("telemetry", new { value = 1 }));
        Assert.True(journal.TryRecord("telemetry", new { value = new string('x', 4096) }));
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.False(root.GetProperty("captureComplete").GetBoolean());
        Assert.Equal("maximum-bytes", root.GetProperty("error").GetString());
        Assert.Equal(1, root.GetProperty("writtenRecords").GetInt64());
        Assert.Equal(1, root.GetProperty("abandonedRecords").GetInt64());
        Assert.Equal(1, journal.DroppedRecords);
        Assert.True(archive.GetEntry("events.jsonl")!.Length <= 1024);
        Assert.Single(ReadLines(archive));
    }

    [Fact]
    public async Task DeadlineStopsAcceptanceAndEmptyCaptureIsExplicitlyIncomplete()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create(maxDuration: TimeSpan.FromMilliseconds(10));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(journal.TryRecord("telemetry", new { value = 1 }));
        Assert.False(journal.IsAccepting);
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        Assert.Equal("maximum-duration", manifest.RootElement.GetProperty("finishReason").GetString());
        Assert.Equal("maximum-duration", manifest.RootElement.GetProperty("error").GetString());
        Assert.False(manifest.RootElement.GetProperty("captureComplete").GetBoolean());
        Assert.Equal(0, manifest.RootElement.GetProperty("writtenRecords").GetInt64());
    }

    [Fact]
    public async Task MissingProtocolEvidenceCannotBeReportedAsCompleteDespiteValidFiles()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.True(journal.TryRecord("telemetry", new { value = 1 }));
        journal.MarkIncomplete("missing-upshift-input");
        journal.MarkIncomplete("missing-native-configuration");
        journal.MarkIncomplete("missing-upshift-input");
        Assert.True(journal.IsAccepting);
        Assert.Equal("missing-upshift-input", journal.Error);
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        Assert.False(root.GetProperty("captureComplete").GetBoolean());
        Assert.True(root.GetProperty("validation").GetProperty("jsonlVerified").GetBoolean());
        Assert.Equal("missing-upshift-input", root.GetProperty("error").GetString());
        Assert.Equal(new[] { "missing-native-configuration", "missing-upshift-input" },
            root.GetProperty("incompleteErrors").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Throws<InvalidOperationException>(() => journal.MarkIncomplete("late-reason"));
    }

    [Fact]
    public async Task EmptyManualCaptureCanBeInspectedButIsNeverCalledComplete()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("user-stop"));
        using var manifest = ReadManifest(archive);
        Assert.Equal("no-records", journal.Error);
        Assert.False(manifest.RootElement.GetProperty("captureComplete").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("validation").GetProperty("jsonlVerified").GetBoolean());
        Assert.Empty(ReadLines(archive));
    }

    [Fact]
    public async Task SerializationFailureStopsAndCannotReturnSuccessfulPackage()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.True(journal.TryRecord("telemetry", new ThrowingPayload()));
        await Assert.ThrowsAsync<IOException>(() => journal.StopAndPackageAsync("user-stop"));
        Assert.Equal("journal-write-failed", journal.Error);
        Assert.False(journal.IsAccepting);
        Assert.False(File.Exists(Path.Combine(journal.DirectoryPath, "capture.zip")));
    }

    [Fact]
    public async Task PackagingIoFailureIsSanitizedAndPreservesTheLocalJournal()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.True(journal.TryRecord("telemetry", new { value = 1 }));
        Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "manifest.json"));
        var failure = await Assert.ThrowsAsync<IOException>(() => journal.StopAndPackageAsync("user-stop"));
        Assert.DoesNotContain(journal.DirectoryPath, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("capture-package-validation-failed", journal.Error);
        Assert.True(File.Exists(Path.Combine(journal.DirectoryPath, "events.jsonl")));
        Assert.False(File.Exists(Path.Combine(journal.DirectoryPath, "capture.zip")));
    }

    [Fact]
    public async Task ExistingSessionDirectoryCannotBeOverwritten()
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.Throws<IOException>(() => new ShiftCaptureJournal(journal.DirectoryPath, new { build = "fixture" }));
    }

    [Theory]
    [InlineData("event with spaces")]
    [InlineData("../event")]
    [InlineData("drive:event")]
    public async Task IdentifiersCannotContainFreeTextOrPaths(string kind)
    {
        await using var files = new CaptureFiles();
        var journal = files.Create();
        Assert.Throws<ArgumentException>(() => journal.TryRecord(kind, new { value = 1 }));
    }

    private static JsonDocument ReadManifest(ZipArchive archive)
    {
        using var input = archive.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(input);
    }

    private static void RecordRequiredChannels(ShiftCaptureJournal journal, string? omit = null)
    {
        void Record(string kind, object payload)
        {
            if (kind != omit) Assert.True(journal.TryRecord(kind, payload));
        }
        Record("telemetry", new
        {
            parsed = true,
            rawBase64 = "AA==",
            state = new
            { carOrdinal = 1, gear = 2, gameTimestampMilliseconds = 10, engineRpm = 6000 }
        });
        Record("native_configuration", new { fingerprint = "fixture" });
        Record("controller_button", new
        {
            edge = "pressed",
            earliestObservedEdgeQpc = 100,
            latestObservedEdgeQpc = 120,
            pollStartedQpc = 118,
            pollCompletedQpc = 120
        });
        Record("cue_evaluation", new
        {
            carOrdinal = 1,
            gear = 2,
            cue = new { enabled = true, stage = 2, targetRpm = 9000 }
        });
        Record("cue_submission", new
        {
            carOrdinal = 1,
            gear = 2,
            submitted = true,
            canary = false,
            shiftCue = new { enabled = true, stage = 2, targetRpm = 9000, flashOn = true, isVisible = true }
        });
        Record("cue_pixels", new { status = 0, qualified = true, readStartedQpc = 100, readFinishedQpc = 108 });
    }

    private static string[] ReadLines(ZipArchive archive)
    {
        using var input = archive.GetEntry("events.jsonl")!.Open();
        using var reader = new StreamReader(input);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class BlockingPayload : IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal int SerializationThread { get; private set; }
        public int Value
        {
            get
            {
                SerializationThread = Environment.CurrentManagedThreadId;
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return 1;
            }
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class ThrowingPayload { public int Value => throw new InvalidOperationException("fixture"); }

    private sealed class CaptureFiles : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Wisp-ShiftCaptureJournalTests-" + Guid.NewGuid().ToString("N"));
        private readonly List<ShiftCaptureJournal> _journals = new();
        internal ShiftCaptureJournal Create(int capacity = 8192, long maxBytes = 512 * 1024 * 1024,
            TimeSpan? maxDuration = null, object? provenance = null)
        {
            var journal = new ShiftCaptureJournal(Path.Combine(_root, Guid.NewGuid().ToString("N")),
                provenance ?? new { build = "fixture", input = "approved-button-only" }, capacity, maxBytes, maxDuration);
            _journals.Add(journal);
            return journal;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var journal in _journals)
            {
                try { await journal.StopAndPackageAsync("test-cleanup"); }
                catch (IOException) { }
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
