using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.App;

// Explicit private-session preflight. Uses the same writer/packager as the real
// capture, before any game or input recording starts. Evidence is never deleted.
internal static class ShiftCaptureStorageCheck
{
    internal static Task<ShiftCaptureStorageCheckResult> RunAsync(string parentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        return Task.Run(async () =>
        {
            string? directory = null;
            ShiftCaptureJournal? journal = null;
            try
            {
                directory = Path.Combine(Path.GetFullPath(parentDirectory), "storage-check-" + Guid.NewGuid().ToString("N"));
                journal = new ShiftCaptureJournal(directory, new
                {
                    schema = "shift-storage-selfcheck-v1",
                    purpose = "Synthetic storage roundtrip only; no game, controller or screen data"
                }, capacity: 16, maxBytes: 64 * 1024, maxDuration: TimeSpan.FromSeconds(15));
                if (!journal.TryRecord("storage_probe", new { value = 42 }) ||
                    !journal.TryRecord("storage_probe", new { value = 43 }))
                    throw new IOException("The storage probe could not retain its two records.");
                var archivePath = await journal.StopAndPackageAsync("storage-selfcheck").ConfigureAwait(false);
                using var archive = ZipFile.OpenRead(archivePath);
                if (archive.Entries.Count != 3) throw new InvalidDataException("Unexpected storage-probe archive contents.");
                var payload = archive.GetEntry("events.jsonl") ?? throw new InvalidDataException("Missing probe payload.");
                var manifest = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing probe manifest.");
                var report = archive.GetEntry("capture-report.txt") ?? throw new InvalidDataException("Missing probe report.");
                using var manifestStream = manifest.Open();
                using var document = await JsonDocument.ParseAsync(manifestStream).ConfigureAwait(false);
                var root = document.RootElement;
                if (!root.GetProperty("captureComplete").GetBoolean() ||
                    root.GetProperty("droppedRecords").GetInt64() != 0 ||
                    root.GetProperty("writtenRecords").GetInt64() != 2 ||
                    !root.GetProperty("storageIntegrity").GetProperty("journalEventsLossless").GetBoolean() ||
                    root.GetProperty("audit").GetProperty("records").GetInt64() != 2)
                    throw new InvalidDataException("Storage probe accounting failed.");
                var validation = root.GetProperty("validation");
                if (payload.Length != validation.GetProperty("byteLength").GetInt64() || payload.Length > 64 * 1024)
                    throw new InvalidDataException("Storage probe size failed.");
                using (var stream = payload.Open())
                    if (Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)) != validation.GetProperty("sha256").GetString())
                        throw new InvalidDataException("Storage probe payload hash failed.");
                using (var stream = report.Open())
                    if (report.Length != root.GetProperty("report").GetProperty("byteLength").GetInt64() ||
                        Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)) != root.GetProperty("report").GetProperty("sha256").GetString())
                        throw new InvalidDataException("Storage probe report hash failed.");
                using (var stream = payload.Open())
                using (var reader = new StreamReader(stream))
                {
                    for (var index = 0; index < 2; index++)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false) ?? throw new InvalidDataException("Missing storage probe record.");
                        using var row = JsonDocument.Parse(line);
                        if (row.RootElement.GetProperty("sequence").GetInt64() != index + 1 ||
                            row.RootElement.GetProperty("kind").GetString() != "storage_probe" ||
                            row.RootElement.GetProperty("payload").GetProperty("value").GetInt32() != index + 42)
                            throw new InvalidDataException("Storage probe content failed.");
                    }
                    if (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
                        throw new InvalidDataException("Unexpected storage probe record.");
                }
                return new ShiftCaptureStorageCheckResult(true, "roundtrip-verified", null, directory);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                if (journal is not null)
                {
                    try { await journal.StopAndPackageAsync("storage-selfcheck-failed").ConfigureAwait(false); }
                    catch (Exception stopError) when (stopError is not OutOfMemoryException and not StackOverflowException) { }
                }
                return new ShiftCaptureStorageCheckResult(false, "storage-check-failed", error.GetType().Name, directory);
            }
        });
    }
}

internal sealed record ShiftCaptureStorageCheckResult(bool Passed, string Status, string? ErrorType,
    [property: JsonIgnore] string? EvidenceDirectory)
{
    public string Scope => "Synthetic bounded file/journal/ZIP/hash roundtrip on this account, before arming. No game data, input, renderer or car-physics validation.";
}
