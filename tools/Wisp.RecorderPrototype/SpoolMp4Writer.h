#pragma once

#include "EncodedSpool.h"
#include "Mp4ClipWriter.h"

namespace recorder::exporting
{
    struct FileExportEvidence
    {
        ExportEvidence media{};
        ULONGLONG fileBytes = 0;
        HRESULT fileCloseHr = S_OK, cursorCloseHr = S_OK;
    };
    // Always adopts and closes the create-new handle from EncodedSpool. Uses
    // immutable pinned cursors with one compressed packet in memory per track.
    // Caller keeps the snapshot alive until completion and owns the save worker.
    // No path lookup, replacement, deletion, capture or re-encoding occurs.
    FileExportEvidence WriteSpoolMp4(HANDLE ownedFile, const spool::Snapshot&,
        const VideoFormat&, const std::atomic<bool>& cancelled) noexcept;
}
