using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureReplayTests
{
    private const int ReplayRows = 18_050;
    private const string SyntheticEvidence = "Synthetic storage fixture; not recorded driving, model validation or displayed frames";

    [Fact]
    public async Task SyntheticFullLengthReplayPreservesEveryImmutableRowAndFixtureChannel()
    {
        using var timeout = Deadline();
        await using var files = new CaptureFiles();
        var journal = files.Create(32_768);
        var fixtureEventsPerChannel = (ReplayRows + 49) / 50;
        var start = Stopwatch.GetTimestamp();
        for (var row = 0; row < ReplayRows; row++)
        {
            var stamp = start + row * Math.Max(1, Stopwatch.Frequency / 120);
            var telemetry = SyntheticRow(row);
            Assert.True(journal.TryRecord("telemetry", telemetry, stamp));
            if (row % 50 != 0) continue;
            Assert.True(journal.TryRecord("controller_button",
                new InputFixture(row, "B", "upshift", "pressed", SyntheticEvidence), stamp));
            Assert.True(journal.TryRecord("cue_evaluation",
                new ModelFixture(row, 7_800, row % 4, SyntheticEvidence), stamp));
            Assert.True(journal.TryRecord("cue_submission",
                new SubmissionFixture(row, row / 50, true, SyntheticEvidence), stamp));
        }

        var path = await journal.StopAndPackageAsync("synthetic-replay").WaitAsync(timeout.Token);
        using var archive = ZipFile.OpenRead(path);
        using var manifest = ReadManifest(archive);
        var total = ReplayRows + 3 * fixtureEventsPerChannel;
        VerifyCompleteManifest(manifest.RootElement, total);
        VerifyKind(manifest.RootElement, "telemetry", ReplayRows);
        foreach (var kind in new[] { "controller_button", "cue_evaluation", "cue_submission" })
            VerifyKind(manifest.RootElement, kind, fixtureEventsPerChannel);
        Assert.Contains("Synthetic", manifest.RootElement.GetProperty("provenance").GetRawText());

        long sequence = 0;
        var nextTelemetry = 0;
        var nextFixture = new Dictionary<string, int>
        {
            ["controller_button"] = 0,
            ["cue_evaluation"] = 0,
            ["cue_submission"] = 0
        };
        using (var stream = archive.GetEntry("events.jsonl")!.Open())
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                Assert.Equal(++sequence, entry.GetProperty("sequence").GetInt64());
                var kind = entry.GetProperty("kind").GetString()!;
                var payload = entry.GetProperty("payload");
                var row = payload.GetProperty("row").GetInt32();
                Assert.Equal(start + row * Math.Max(1, Stopwatch.Frequency / 120),
                    entry.GetProperty("timestamp").GetInt64());
                Assert.Equal(SyntheticEvidence, payload.GetProperty("evidence").GetString());
                if (kind == "telemetry")
                {
                    Assert.Equal(nextTelemetry++, row);
                    var expected = SyntheticRow(row);
                    Assert.Equal(expected.Gear, payload.GetProperty("gear").GetInt32());
                    Assert.Equal(expected.Rpm, payload.GetProperty("rpm").GetSingle());
                    Assert.Equal(expected.TorqueNm, payload.GetProperty("torqueNm").GetSingle());
                    Assert.Equal(expected.PowerWatts, payload.GetProperty("powerWatts").GetSingle());
                }
                else
                {
                    Assert.True(nextFixture.ContainsKey(kind));
                    Assert.Equal(nextFixture[kind], row);
                    nextFixture[kind] += 50;
                    if (kind == "controller_button") Assert.Equal("pressed", payload.GetProperty("edge").GetString());
                    if (kind == "cue_evaluation") Assert.Equal(7_800, payload.GetProperty("targetRpm").GetDouble());
                    if (kind == "cue_submission") Assert.Equal(row / 50, payload.GetProperty("renderSequence").GetInt32());
                }
            }
        }
        Assert.Equal(total, sequence);
        Assert.Equal(ReplayRows, nextTelemetry);
        Assert.All(nextFixture.Values, next => Assert.Equal(fixtureEventsPerChannel * 50, next));
        VerifyPayloadHash(archive, manifest.RootElement);
        Assert.Equal(0, journal.DroppedRecords);
        Assert.Null(journal.Error);
    }

    [Fact]
    public async Task RealUdpReceiverCapturePreservesEveryFixtureSequenceAndParseFailureInJournal()
    {
        const int packetCount = 72;
        using var timeout = Deadline();
        await using var files = new CaptureFiles();
        var journal = files.Create(256);
        await using var receiver = new TelemetryUdpReceiver();
        var port = AvailablePort();
        await receiver.StartAsync(port, timeout.Token);
        var capture = receiver.BeginRunCapture(128);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var written = 0;
        var consumer = Consume();
        var checkpoint = 0;
        Exception? failure = null;
        try
        {
            // Exercise bounded bursts and wait for consumption. UDP does not promise
            // lossless delivery when a producer outruns the receiver's socket buffer.
            for (var first = 0; first < packetCount; first += 8)
            {
                checkpoint = Math.Min(first + 8, packetCount);
                for (var index = first; index < checkpoint; index++)
                    await sender.SendAsync(Packet(index), new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
                while (Volatile.Read(ref written) < checkpoint)
                {
                    if (consumer.IsCompleted)
                    {
                        await consumer;
                        Assert.Fail("Capture consumer ended before the burst was retained: " + Progress());
                    }
                    await Task.Delay(5, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            failure = new TimeoutException("Loopback fixture checkpoint timed out: " + Progress(), ex);
            throw failure;
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            receiver.EndRunCapture(capture, "fixture-complete");
            try { await consumer.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); }
            catch when (failure is not null) { /* Preserve the original assertion/timeout and its counts. */ }
        }
        Assert.Equal(packetCount, capture.ObservedDatagrams);
        Assert.Equal(0, capture.DroppedDatagrams);
        Assert.Equal(packetCount, written);
        using var archive = ZipFile.OpenRead(await journal.StopAndPackageAsync("udp-fixture").WaitAsync(timeout.Token));
        using var manifest = ReadManifest(archive);
        VerifyCompleteManifest(manifest.RootElement, packetCount);
        VerifyKind(manifest.RootElement, "telemetry", packetCount);
        using var stream = archive.GetEntry("events.jsonl")!.Open();
        using var reader = new StreamReader(stream);
        var row = 0;
        var rejected = 0;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            var payload = entry.GetProperty("payload");
            Assert.Equal(row + 1, entry.GetProperty("sequence").GetInt64());
            Assert.Equal(row + 1, payload.GetProperty("rawSequence").GetInt64());
            Assert.Equal(entry.GetProperty("timestamp").GetInt64(), payload.GetProperty("receivedTimestamp").GetInt64());
            Assert.True(entry.GetProperty("timestamp").GetInt64() > 0);
            var expectedError = row switch
            {
                17 => PacketParseError.IncorrectLength,
                33 => PacketParseError.InvalidRaceFlag,
                49 => PacketParseError.NonFiniteValue,
                _ => PacketParseError.None
            };
            Assert.Equal(expectedError.ToString(), payload.GetProperty("error").GetString());
            Assert.Equal(expectedError == PacketParseError.None, payload.GetProperty("parsed").GetBoolean());
            // The bounded raw capture represents wrong-length traffic as an empty payload.
            // Its position and error remain present; this is not a claim to retain arbitrary UDP bytes.
            var expectedBytes = row == 17 ? Array.Empty<byte>() : Packet(row);
            Assert.Equal(expectedBytes.Length, payload.GetProperty("recordedLength").GetInt32());
            Assert.Equal(Convert.ToHexString(expectedBytes), payload.GetProperty("rawHex").GetString());
            if (expectedError == PacketParseError.None)
            {
                var state = payload.GetProperty("state");
                Assert.Equal(4_000 + row, state.GetProperty("carOrdinal").GetInt32());
                Assert.Equal(5_000f + row, state.GetProperty("engineRpm").GetSingle());
                Assert.Equal((uint)(row * 8), state.GetProperty("gameTimestampMilliseconds").GetUInt32());
            }
            else
            {
                rejected++;
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("state").ValueKind);
            }
            row++;
        }
        Assert.Equal(packetCount, row);
        Assert.Equal(3, rejected);
        VerifyPayloadHash(archive, manifest.RootElement);

        string Progress() => $"checkpoint={checkpoint}, received={receiver.ReceivedDatagrams}, " +
            $"captured={capture.ObservedDatagrams}, written={Volatile.Read(ref written)}, dropped={capture.DroppedDatagrams}";

        async Task Consume()
        {
            var parser = new Fh6PacketParser();
            await foreach (var packet in capture.ReadAllAsync(timeout.Token))
            {
                var parsed = parser.TryParse(packet.Bytes.Span, DateTimeOffset.UtcNow, out var state, out var error, packet.Timestamp);
                // Materialize before advancing the enumerator: its underlying datagram buffer is pooled.
                var immutable = new ReceivedFixture(packet.Sequence, packet.Timestamp, packet.Bytes.Length,
                    Convert.ToHexString(packet.Bytes.Span), parsed, error.ToString(), state, SyntheticEvidence);
                Assert.True(journal.TryRecord("telemetry", immutable, packet.Timestamp));
                Interlocked.Increment(ref written);
            }
        }
    }

    private static TelemetryFixture SyntheticRow(int row) => new(row, 1 + row / 401 % 7,
        1_000 + row % 8_500 + .25f, row % 97 == 0 ? -125.5f : 100 + row % 900 + .5f,
        row % 97 == 0 ? -52_000.25f : 80_000 + row % 750_000 + .25f, SyntheticEvidence);

    private static byte[] Packet(int index)
    {
        if (index == 17) return new byte[8];
        var bytes = new byte[324];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), index == 33 ? 2 : 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(index * 8));
        WriteFloat(8, 9_000);
        WriteFloat(16, index == 49 ? float.NaN : 5_000 + index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(212, 4), 4_000 + index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(224, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(228, 4), 8);
        WriteFloat(256, 40 + index * .25f);
        WriteFloat(260, 250_000 + index);
        WriteFloat(264, 500 + index);
        bytes[315] = 255;
        bytes[319] = 3;
        return bytes;

        void WriteFloat(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private static CancellationTokenSource Deadline()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private static int AvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static JsonDocument ReadManifest(ZipArchive archive)
    {
        using var stream = archive.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream);
    }

    private static void VerifyCompleteManifest(JsonElement root, long total)
    {
        Assert.True(root.GetProperty("captureComplete").GetBoolean());
        Assert.Equal(total, root.GetProperty("attemptedRecords").GetInt64());
        Assert.Equal(total, root.GetProperty("acceptedRecords").GetInt64());
        Assert.Equal(total, root.GetProperty("writtenRecords").GetInt64());
        Assert.Equal(0, root.GetProperty("droppedRecords").GetInt64());
        Assert.Equal(0, root.GetProperty("abandonedRecords").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        Assert.True(root.GetProperty("validation").GetProperty("jsonlVerified").GetBoolean());
        Assert.Equal(total, root.GetProperty("validation").GetProperty("recordCount").GetInt64());
        Assert.Equal(total, root.GetProperty("validation").GetProperty("lastSequence").GetInt64());
    }

    private static void VerifyKind(JsonElement root, string kind, long count)
    {
        var counts = root.GetProperty("countsByKind").GetProperty(kind);
        foreach (var property in new[] { "attempted", "accepted", "written" })
            Assert.Equal(count, counts.GetProperty(property).GetInt64());
        Assert.Equal(0, counts.GetProperty("queueDropped").GetInt64());
        Assert.Equal(0, counts.GetProperty("abandoned").GetInt64());
    }

    private static void VerifyPayloadHash(ZipArchive archive, JsonElement root)
    {
        var entry = archive.GetEntry("events.jsonl")!;
        using var stream = entry.Open();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(stream)), root.GetProperty("validation").GetProperty("sha256").GetString());
        Assert.Equal(entry.Length, root.GetProperty("validation").GetProperty("byteLength").GetInt64());
    }

    private sealed record TelemetryFixture(int Row, int Gear, float Rpm, float TorqueNm, float PowerWatts, string Evidence);
    private sealed record InputFixture(int Row, string Button, string Action, string Edge, string Evidence);
    private sealed record ModelFixture(int Row, double TargetRpm, int Stage, string Evidence);
    private sealed record SubmissionFixture(int Row, int RenderSequence, bool Submitted, string Evidence);
    private sealed record ReceivedFixture(long RawSequence, long ReceivedTimestamp, int RecordedLength,
        string RawHex, bool Parsed, string Error, VehicleState? State, string Evidence);

    private sealed class CaptureFiles : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Wisp-ShiftCaptureReplayTests-" + Guid.NewGuid().ToString("N"));
        private readonly List<ShiftCaptureJournal> _journals = [];
        internal ShiftCaptureJournal Create(int capacity)
        {
            var journal = new ShiftCaptureJournal(Path.Combine(_root, Guid.NewGuid().ToString("N")),
                new { evidence = SyntheticEvidence, scope = "Offline storage and loopback receiver only" }, capacity);
            _journals.Add(journal);
            return journal;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var journal in _journals)
            {
                try { await journal.StopAndPackageAsync("test-cleanup").WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (IOException) { }
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
