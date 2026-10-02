#pragma once

#include <cstddef>
#include <cstdint>
#include <deque>
#include <memory>
#include <vector>

namespace recorder::buffer
{
    namespace detail { struct ByteBudget; }

    using MediaTime = std::int64_t; // Media Foundation sample time/duration: 100 ns.

    struct Limits
    {
        MediaTime maximumDuration100ns = 0;
        std::uint64_t maximumCompressedBytes = 0;
    };

    enum class Result
    {
        Accepted, InvalidLimits, InvalidConfiguration, EpochNotIncreasing,
        NoConfiguration, EpochMismatch, InvalidTimestamp, InvalidPayload,
        KeyframeRequired, Discontinuity, DurationLimit, ByteLimit,
        RetainedClipExists, NoFrames, AllocationFailed
    };

    class EncodedClipBuffer;

    // Created by the buffer, copied from caller bytes, and never mutable. Each
    // distinct payload stays charged until its last shared owner releases it.
    class Payload final
    {
    public:
        ~Payload();
        const std::vector<std::uint8_t>& Bytes() const noexcept { return bytes_; }
        Payload(const Payload&) = delete;
        Payload& operator=(const Payload&) = delete;
    private:
        friend class EncodedClipBuffer;
        Payload(const std::uint8_t* data, std::size_t size,
            std::shared_ptr<detail::ByteBudget> budget);
        std::vector<std::uint8_t> bytes_;
        std::shared_ptr<detail::ByteBudget> budget_;
    };

    struct Configuration
    {
        std::uint64_t epoch = 0;
        std::shared_ptr<const Payload> h264SequenceHeader;
    };

    struct Packet
    {
        MediaTime timestamp100ns = 0;
        MediaTime duration100ns = 0;
        bool cleanPoint = false;
        std::shared_ptr<const Payload> payload;
    };

    struct Clip
    {
        Configuration configuration;
        std::vector<Packet> packets;
        MediaTime start100ns = 0;
        MediaTime end100ns = 0; // Exclusive: last timestamp plus its duration.
    };

    // No capture, graphics, encoder, audio, disk or app dependency. Serialize
    // calls to this object. Immutable clips/payloads may be read and released
    // on other threads; byte accounting follows their actual ownership.
    // The cap includes unique compressed packets AND configuration bytes, not
    // container/allocator overhead or the caller's original encoded sample.
    class EncodedClipBuffer final
    {
    public:
        explicit EncodedClipBuffer(Limits limits);
        EncodedClipBuffer(const EncodedClipBuffer&) = delete;
        EncodedClipBuffer& operator=(const EncodedClipBuffer&) = delete;

        // A new encoder/configuration or timestamp origin needs a new epoch.
        // Clears rolling data; never changes a retained clip. If allocation or
        // the retained-byte budget prevents the new header, no epoch is active.
        Result BeginEpoch(std::uint64_t epoch, const std::uint8_t* header, std::size_t size);
        void MarkDiscontinuity() noexcept;

        // Requires positive duration and contiguous, non-overlapping times.
        // After a rejected packet, a clean point is needed. A timestamp reset
        // requires BeginEpoch; MarkDiscontinuity permits a forward time gap.
        // The caller supplies trusted clean-point/configuration metadata; this
        // core does not parse or claim to validate the compressed bitstream.
        Result Append(std::uint64_t epoch, MediaTime timestamp100ns, MediaTime duration100ns,
            bool cleanPoint, const std::uint8_t* data, std::size_t size);

        Result Retain();
        std::shared_ptr<const Clip> Retained() const noexcept { return retained_; }
        void ReleaseRetained() noexcept { retained_.reset(); }

        std::uint64_t AccountedBytes() const noexcept;
        std::size_t RollingPacketCount() const noexcept { return rolling_.size(); }
        MediaTime RollingDuration100ns() const noexcept;
        bool AwaitingKeyframe() const noexcept { return rolling_.empty(); }

    private:
        Result CopyPayload(const std::uint8_t* data, std::size_t size,
            std::shared_ptr<const Payload>& result);
        bool RemoveOldestGop(bool incomingCleanPoint);
        bool ValidLimits() const noexcept;

        Limits limits_;
        std::shared_ptr<detail::ByteBudget> budget_;
        Configuration configuration_;
        std::uint64_t lastEpoch_ = 0;
        bool hasAcceptedTime_ = false;
        MediaTime lastEnd100ns_ = 0;
        std::deque<Packet> rolling_;
        std::shared_ptr<const Clip> retained_;
        std::weak_ptr<const Clip> outstandingClip_;
    };
}
