using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.App;
using Wisp.Telemetry;

namespace Wisp.UiReview;

// Offline review of a retained recording; never starts WPF or any live service.
internal static class ShiftSessionReview
{
    internal static int Run(string input, string output)
    {
        using var archive = ZipFile.OpenRead(input);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var root = manifest.RootElement;
        var frequency = root.GetProperty("stopwatchFrequency").GetInt64();
        var payload = archive.GetEntry("events.jsonl")!;
        string hash;
        using (var stream = payload.Open()) hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, root.GetProperty("validation").GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recorded payload hash does not match its manifest.");
        var audit = new ShiftCaptureSessionAudit(frequency);
        var parser = new Fh6PacketParser();
        long records = 0, packets = 0, projected = 0;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        using (var stream = payload.Open())
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                using var row = JsonDocument.Parse(line);
                var entry = row.RootElement;
                audit.Observe(entry);
                records++;
                if (entry.GetProperty("kind").GetString() != "telemetry") continue;
                var data = entry.GetProperty("payload");
                var bytes = Convert.FromBase64String(data.GetProperty("rawBase64").GetString()!);
                if (!parser.TryParse(bytes, DateTimeOffset.UnixEpoch, out _, out _, entry.GetProperty("timestamp").GetInt64()))
                    throw new InvalidDataException("A retained packet failed the current parser.");
                packets++;
                if (ShiftCapturePacketEvidence.Read(bytes) is not null) projected++;
            }
        }
        if (records != root.GetProperty("validation").GetProperty("recordCount").GetInt64())
            throw new InvalidDataException("Retained record count changed.");
        var result = new
        {
            verifiedPayload = true,
            records,
            packets,
            projected,
            currentRecorderSelfCheck = ShiftCaptureSelfCheck.Run(),
            audit = audit.Snapshot()
        };
        File.WriteAllText(Path.Combine(output, "recorded-session-review.json"), JsonSerializer.Serialize(result, json));
        File.WriteAllText(Path.Combine(output, "recorded-session-review.txt"), audit.ToPlainText());
        Console.WriteLine($"Verified {records:N0} recorded events; parsed/projected {packets:N0}/{projected:N0} packets.");
        return result.currentRecorderSelfCheck.Passed && packets == projected && result.audit.MalformedAuditRecords == 0 ? 0 : 1;
    }
}
