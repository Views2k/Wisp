# Wisp run files and library archives

Wisp exports recorded telemetry, not rendered graphs. Names, tune descriptions, notes, markers, timestamps, recording status, packet counters, calibration values, and every serialized telemetry field stay with the run. Nothing is uploaded.

## Single run: `.wisprun`

The existing single-run format remains unchanged: a gzip-compressed UTF-8 JSON Lines file.

1. The first line is a `RecordedRun` object. `schemaVersion` is currently `1`; `samples` must be an empty array. The header carries `id`, `name`, `tune`, `notes`, `startedAtUtc`, `finishReason`, `isIncomplete`, `rejectedDatagrams`, `droppedDatagrams`, and `markers`.
2. Every subsequent line is one `RunSample`, in recording order. Its fields are `elapsedSeconds`, `segment`, `isDriving`, `state`, `wheelSpeedMetersPerSecond`, `frontRadiusMeters`, and `rearRadiusMeters`. `state` contains the serialized `VehicleState` fields; values are not rounded for export.

JSON uses camel-case property names and string enum names. Unknown properties and unsupported schema versions are rejected rather than silently discarded. Existing Wisp validation also checks finite/bounded numeric data, chronological samples and markers, consistent car/drivetrain identity, plausible calibration, and metadata lengths. Current definitions live in `src/Wisp.Core/Runs/RunModels.cs`, `src/Wisp.Core/VehicleState.cs`, and `src/Wisp.App/Runs/RunStore.cs`.

## Library archive: `.zip`, version 1

This is a standard ZIP containing one `manifest.json` and one existing `.wisprun` file per run. Tools that understand ZIP can extract the individual run files without implementing a new telemetry serialization format.

An illustrative manifest is:

```json
{
  "format": "Wisp Run Archive",
  "version": 1,
  "runs": [
    {
      "id": "00000000-0000-0000-0000-000000000001",
      "path": "runs/00000000000000000000000000000001.wisprun",
      "length": 1024,
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
    }
  ]
}
```

The example size and digest are placeholders. `length` is the exact `.wisprun` byte length, after removing the ZIP container but before decompressing its gzip content. `sha256` hashes those same bytes. Hashes detect corruption; they are not signatures or proof of authorship.

Entry paths must match `runs/{id:N}.wisprun` exactly: lower-case, 32-digit GUID, forward slash, no other folders. The manifest and its run entries must agree on identity, size, and checksum. Duplicate IDs, duplicate paths (including case variants), linked entries, missing entries, unexpected files, invalid data, and unsupported versions fail the entire import. ZIP path names are never used directly as filesystem destinations.

Wisp writes entries in GUID order, sets ZIP timestamps to 1980-01-01, and uses stored ZIP entries because each `.wisprun` is already gzip-compressed. Exporting an unchanged library produces identical archive bytes. Imported standard ZIP entries may be stored or compressed; each extracted run remains subject to the same byte and data limits. Single-disk ZIP and ordinary ZIP64 archives are supported. Split archives and extended ZIP64 directory records are rejected.

## Limits

- At most 2,000 input files selected and 2,000 runs processed in a bulk import. Identical selected paths are opened once.
- At most 2,000 saved runs plus active recording reservations in the library.
- At most 64 MiB per gzip `.wisprun`, 256 MiB decoded JSON, 180,000 samples, and 128 markers per run.
- At most 1 MiB each for the archive manifest and ZIP central directory. Directory structure/counts are checked before constructing ZIP entry objects.
- The archive payload total cannot exceed 2,000 × 64 MiB; archive file size additionally permits 16 MiB of container overhead.
- The existing per-record text and metadata limits still apply. Runs remain limited to ten minutes.

These limits apply before library changes. Files are staged on disk and runs are deserialized individually, rather than holding an entire library's telemetry in memory. Staging requires free disk space for the files being imported or exported.

## Imports and duplicates

The Runs page accepts one or more `.wisprun` and `.zip` files together. Bulk import preserves run IDs. Identical same-ID data is skipped, including a run whose gzip compression or JSON whitespace differs. Equality is determined from the validated, serialized run data, including names and notes.

If an existing or incoming run has the same ID but different content, the entire batch is rejected. Existing data is never overwritten, merged, renamed, or silently preferred. Keep both source files and resolve the conflict deliberately; changing the archive manifest alone cannot change a run's identity.

The internal legacy `ImportAsync(string)` API still creates a new ID for a single imported run, preserving its previous contract. The current Runs-page import flow uses `ImportManyAsync`, including when only one file is selected.

## Export, removal, and interrupted operations

Export all snapshots canonical saved run files under the store's operation lock. It excludes active journals, removed runs, cached summaries, and unrelated files. If any saved run in that snapshot is unreadable, export fails and leaves the originals intact. The archive is fully written to a temporary file beside the selected destination, flushed, and renamed without overwriting an existing file.

Bulk import validates and stages the whole batch before installing its first run. A small local transaction journal supports rollback after an ordinary I/O failure and recovery of an interrupted batch when the library next opens. Substantive transaction entries carry IDs, lengths, and hashes. Recovery checks paths and preserves files when it cannot safely recover; it does not treat ambiguous content as disposable. Committed runs keep their original compressed bytes; summary caches can be rebuilt independently.

Remove all only removes readable canonical library runs. Corrupt/unreadable files and unrelated files stay in place, and skipped entries are reported. Removal is refused while a recording journal is active. Removed runs move into a unique `Deleted/{batch-id}/runs` folder, with transaction metadata. Undo restores the batch using the same validation, capacity, and duplicate rules as import. The batch stays on disk after Undo. This removes entries from the library; it does not permanently erase their backups or reclaim that backup space.

The existing single-run Remove/Undo behavior remains supported. In-process saving, import, export, removal, and recovery use the same store lock. The tests exercise file failures and interrupted transaction fixtures; they do not simulate hardware power loss.
