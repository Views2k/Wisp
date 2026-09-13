using System.IO;
using System.Text.Json;

namespace Wisp.App.Runs;

internal sealed record RunMetadataDraft(Guid Id, string Name, string Tune, string Notes);
internal sealed record RunMetadataSaveResult(RunSummary? Summary, bool DraftPreserved, bool NeedsName);

public sealed partial class RunStore
{
    internal Task<RunMetadataSaveResult> SaveMetadataDraftAsync(RunMetadataDraft draft) => InBackground<RunMetadataSaveResult>(async () =>
    {
        if (!ValidMetadataDraft(draft)) throw new InvalidDataException("The run details are invalid.");
        if (!File.Exists(RunPath(draft.Id))) throw new InvalidDataException("This run is no longer saved in the library.");
        var destination = MetadataDraftPath(draft.Id);
        var temporary = Path.Combine(_directory, $".{draft.Id:N}-{Guid.NewGuid():N}.metadata.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(file, draft, JsonOptions).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            CheckPath(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        if (string.IsNullOrWhiteSpace(draft.Name)) return new(null, true, true);
        try
        {
            var run = await ReadAsync(RunPath(draft.Id), draft.Id).ConfigureAwait(false);
            var updated = run with { Name = draft.Name.Trim(), Tune = draft.Tune.Trim(), Notes = draft.Notes.Trim() };
            await WriteAsync(updated, overwrite: true).ConfigureAwait(false);
            // A leftover recovery draft is harmless: it contains exactly the metadata
            // committed above and will be checked against the run when reopened.
            try { File.Delete(destination); }
            catch (Exception error) when (IsDataError(error)) { }
            return new(Summarize(updated), true, false);
        }
        catch (Exception error) when (IsDataError(error)) { return new(null, true, false); }
    });

    internal Task<RunMetadataDraft?> ReadMetadataDraftAsync(Guid id) => InBackground<RunMetadataDraft?>(async () =>
        await ReadMetadataDraftCoreAsync(id).ConfigureAwait(false));

    internal Task<IReadOnlyList<RunMetadataDraft>> ReadMetadataDraftsAsync(IEnumerable<Guid> ids)
    {
        var knownIds = ids.Distinct().ToArray();
        return InBackground<IReadOnlyList<RunMetadataDraft>>(async () =>
        {
            var drafts = new List<RunMetadataDraft>();
            foreach (var id in knownIds)
                if (await ReadMetadataDraftCoreAsync(id).ConfigureAwait(false) is { } draft) drafts.Add(draft);
            return drafts;
        });
    }

    private async Task<RunMetadataDraft?> ReadMetadataDraftCoreAsync(Guid id)
    {
        var path = MetadataDraftPath(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is <= 0 or > 32_768) throw new InvalidDataException("The saved details draft is invalid.");
        var draft = JsonSerializer.Deserialize<RunMetadataDraft>(await File.ReadAllTextAsync(path).ConfigureAwait(false), JsonOptions);
        return draft is not null && draft.Id == id && ValidMetadataDraft(draft) ? draft : throw new InvalidDataException("The saved details draft is invalid.");
    }

    private string MetadataDraftPath(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Choose a saved run.", nameof(id));
        var path = Path.Combine(_directory, $"{id:N}.metadata-draft.json");
        CheckPath(path);
        return path;
    }

    private static bool ValidMetadataDraft(RunMetadataDraft draft) => draft.Id != Guid.Empty &&
        ValidText(draft.Name, 100, false) && ValidText(draft.Tune, 150, false) && ValidText(draft.Notes, 4000, true);
}
