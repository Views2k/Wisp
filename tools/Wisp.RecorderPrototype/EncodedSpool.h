#pragma once

#include <windows.h>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace recorder::spool
{
    using MediaTime = std::int64_t;
    constexpr MediaTime MaximumDuration = 300ll * 10000000;
    constexpr std::uint32_t StandardMaximumPacketBytes = 16u * 1024 * 1024;
    constexpr std::uint32_t MaximumPacketBytes = 64u * 1024 * 1024;
    constexpr std::uint32_t OwnershipRecordBytes = 128;
    enum class Track { Video, Audio };
    enum class ReadResult { Packet, End, Failed };

    struct Limits
    {
        MediaTime maximumDuration100ns = MaximumDuration;
        std::uint64_t maximumFileBytes = 0; // Required; includes configuration/record headers and pinned files.
        std::uint32_t maximumRecords = 100000; // Rolling records plus the one snapshot's metadata copy.
        std::uint32_t maximumFiles = 2048; // Physical files: each configuration/chunk and its ownership companion.
        std::uint32_t maximumPacketBytes = StandardMaximumPacketBytes; // Video only; audio remains bounded to64KiB.
    };
    struct Configuration
    {
        std::uint64_t epoch = 0;
        std::vector<std::uint8_t> h264SequenceHeader;
        std::vector<std::uint8_t> aacUserData; // Empty means video-only. Opaque negotiated MF codec blob.
    };
    struct Packet
    {
        MediaTime timestamp100ns = 0, duration100ns = 0;
        bool cleanPoint = false;
        std::vector<std::uint8_t> bytes; // A cursor reads one bounded compressed packet, never an entire clip.
    };
    struct Evidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK, cleanupHr = S_OK;
        bool initialized = false, poisoned = false, closed = false;
        std::uint64_t accountedFileBytes = 0, committedPackets = 0;
        std::uint32_t ownedFiles = 0, rollingRecords = 0, snapshotRecords = 0;
    };
    struct Bounds
    {
        MediaTime start100ns = 0, end100ns = 0; // Video bounds; actual span never exceeds requested maximum.
        MediaTime audioStart100ns = 0, audioEnd100ns = 0;
        std::uint32_t videoPackets = 0, audioPackets = 0;
    };
    constexpr std::uint64_t MaximumLosslessVideoBytes = 12ull * 1024 * 1024 * 1024;
    constexpr std::uint64_t MaximumRetainedAudioBytes = 16ull * 1024 * 1024;
    struct RetentionBudget
    {
        std::uint64_t maximumVideoBytes = MaximumLosslessVideoBytes;
        std::uint64_t maximumAudioBytes = MaximumRetainedAudioBytes;
    };
    struct AvailablePlan
    {
        Bounds bounds{};
        std::uint64_t videoPayloadBytes = 0, audioPayloadBytes = 0;
        bool sizeLimited = false; // Capacity eviction/byte trimming, not startup or IDR alignment.
    };
    const char* ValidateRetentionBudget(const RetentionBudget&) noexcept;
    const char* ValidateLimits(const Limits&) noexcept;
    // Syntax-only. Existing local drive parent plus exactly .wisp-recorder-<32 lowercase hex>.
    // Runtime opens the entire parent with OBJ_DONT_REPARSE and creates the new child relative to it.
    bool ValidateSessionPath(const std::wstring&) noexcept;
    bool ValidateExportName(const std::wstring&) noexcept;
    // Opens/closes only the existing parent, without creating any file. Runtime
    // Initialize repeats these checks on the handle retained for actual writes.
    HRESULT PreflightSessionParent(const std::wstring& sessionPath) noexcept;

    class PacketCursor;
    class Snapshot final
    {
    public:
        ~Snapshot();
        const Configuration& Format() const noexcept;
        const Bounds& Range() const noexcept;
        // At most one cursor per track at once. A cursor keeps the snapshot/pins alive.
        HRESULT OpenCursor(Track, std::unique_ptr<PacketCursor>&) const noexcept;
        Snapshot(const Snapshot&) = delete;
        Snapshot& operator=(const Snapshot&) = delete;
    private:
        friend class EncodedSpool;
        friend class PacketCursor;
        struct Impl;
        explicit Snapshot(std::shared_ptr<Impl>);
        std::shared_ptr<Impl> impl_;
    };
    class PacketCursor final
    {
    public:
        ~PacketCursor();
        ReadResult Next(Packet&) noexcept;
        HRESULT Close() noexcept; // Checked on normal paths; destructor is only a fallback.
        const char* Reason() const noexcept;
        HRESULT Error() const noexcept;
        PacketCursor(const PacketCursor&) = delete;
        PacketCursor& operator=(const PacketCursor&) = delete;
    private:
        friend class Snapshot;
        struct Impl;
        explicit PacketCursor(std::unique_ptr<Impl>);
        std::unique_ptr<Impl> impl_;
    };

    // Single ordinary writer worker; no capture, encode, threads, elevation or UI calls.
    // Each Initialize is one immutable codec/timestamp epoch. Disk I/O is synchronous:
    // the host owns cancellation/watchdog around blocking filesystem APIs.
    // The byte cap counts logical file bytes (not filesystem allocation/metadata),
    // including failed reserved tails. File and metadata counts have separate caps.
    // Before any media bytes, each generated file gets a flushed128-byte .owner
    // companion containing its session/name/FileIdInfo. Companions remain pinned
    // and retire with the data file; they allow bounded orphan cleanup to verify
    // exact ownership after a crash without trusting a filename pattern.
    // Only complete oldest GOPs/obsolete audio chunks can be retired. Pinned files
    // are never deleted. A cap refusal does not consume the caller's packet.
    // All I/O errors are terminal for appends and preserve remaining files.
    class EncodedSpool final
    {
    public:
        EncodedSpool() noexcept;
        ~EncodedSpool();
        EncodedSpool(const EncodedSpool&) = delete;
        EncodedSpool& operator=(const EncodedSpool&) = delete;
        bool Initialize(const std::wstring& newSessionDirectory, const Limits&, const Configuration&) noexcept;
        bool AppendVideo(MediaTime timestamp, MediaTime duration, bool cleanPoint,
            const std::uint8_t*, std::size_t) noexcept;
        bool AppendAudio(MediaTime timestamp, MediaTime duration,
            const std::uint8_t*, std::size_t) noexcept;
        // Startup may yield less than requested. A capacity-evicted required GOP
        // refuses the save explicitly rather than silently shortening it.
        // Audio-enabled snapshots end at
        // a committed video boundary covered by audio. The first AAC packet
        // starts at/after the video start, within one1024-frame48kHz packet;
        // negative rebased sample PTS and guessed priming adjustments are avoided.
        // Audio packet boundaries must be exactly contiguous. Actual bounds are explicit.
        // Saving is allowed after an append failure using previously committed bytes.
        bool Retain(MediaTime maximumSpan, std::shared_ptr<const Snapshot>&) noexcept;
        // Explicit byte-limited lossless policy. Planning reads only writer-owned
        // metadata: no files are read/flushed/pinned and an active save is allowed.
        // Retaining recomputes the same plan and pins it before returning. The host
        // must report actual bounds and sizeLimited rather than promise maximumSpan.
        // A snapshot starts at an IDR; its final complete frame may be in an open GOP.
        bool PlanAvailable(MediaTime maximumSpan, const RetentionBudget&, AvailablePlan&) noexcept;
        bool RetainAvailable(MediaTime maximumSpan, const RetentionBudget&,
            std::shared_ptr<const Snapshot>&, AvailablePlan&) noexcept;
        // Writer-worker call. Creates <32 lowercase hex>.mp4 in the held library
        // parent, never overwrites/deletes. Transfer the returned handle to mux;
        // caller owns checked close, finalization and library registration.
        HRESULT CreateNewExport(const std::wstring& clipName, HANDLE& ownedHandle) noexcept;
        // Explicit clean-stop disposal of expendable rolling data. Refuses any
        // retained reader/snapshot or failed I/O state. Only tracked owned file
        // handles and this newly created session directory are removed. Saved
        // MP4s are outside the directory and are never part of this operation.
        HRESULT DiscardOwnedBuffer() noexcept;
        // Releases handles but deliberately preserves remaining session files.
        // Returns ERROR_BUSY while a snapshot/cursor exists; call again after release.
        HRESULT Close() noexcept;
        Evidence Result() const noexcept;
    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
        Evidence evidence_{};
        bool Append(Track, MediaTime, MediaTime, bool, const std::uint8_t*, std::size_t) noexcept;
        bool RetainInternal(MediaTime, std::shared_ptr<const Snapshot>&, const RetentionBudget*, AvailablePlan*) noexcept;
    };

    // Root invokes these explicitly. File contracts create only their new named
    // child directories, leave evidence in place, and never scan/delete a parent.
    struct ContractFailure { int index; };
    int RunSpoolCpuContracts();
    int RunSpoolFileContracts(const std::wstring& existingLocalParent);
}
