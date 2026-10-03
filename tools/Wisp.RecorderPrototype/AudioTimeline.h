#pragma once

#include "ProcessAudioCapture.h"
#include <cstdint>

namespace recorder::audio
{
    enum class TimelineResult { Feed, BeforeVideoEpoch, CoveredByTimeline, NeedsSilence, Failed };
    struct TimelineOptions
    {
        std::uint64_t videoEpochQpc100ns = 0;
        std::uint64_t qpcFrequency = 0; // Actual QueryPerformanceFrequency, not assumed10MHz.
    };
    struct FeedSlice
    {
        const std::int16_t* samples = nullptr; // Borrowed from the inspected packet.
        std::uint32_t frames = 0, skippedPrefixFrames = 0;
        std::uint64_t firstFrameIndex = 0; // Continuous AAC input index, starting0.
        std::int64_t audioEpochTime100ns = 0; // Nonnegative offset from the common video epoch.
        std::uint64_t sourceTime100ns = 0; // QPC of the first retained sample in this slice.
        std::uint32_t silenceFramesNeeded = 0;
        bool generatedSilence = false;
    };
    struct TimelineEvidence
    {
        const char* reason = "not_started";
        bool initialized = false, anchored = false, failed = false, paused = false;
        bool initialDiscontinuityObserved = false;
        std::uint64_t policyAllowance100ns = 0;
        std::uint64_t sourcePackets = 0, acceptedPackets = 0, beforeEpochPackets = 0;
        std::uint64_t discardedFrames = 0, retainedFrames = 0, residualChecks = 0;
        std::uint64_t maximumAbsoluteResidual100ns = 0;
        std::uint64_t generatedSilenceFrames = 0, coveredPackets = 0;
        std::int64_t lastResidual100ns = 0, audioEpochTime100ns = 0;
    };

    // Integer clocks only, no OS calls/audio activation. The allowance is an
    // APPLICATION ACCEPTANCE POLICY, not a Microsoft timestamp-accuracy claim:
    // one AAC packet (1024 PCM frames), plus source-clock rounding allowance.
    // Microsoft documents QPC units, not a universal audio packet jitter bound.
    // Larger deviations request bounded silence/overlap trimming.
    bool TimelineAllowance(std::uint64_t actualQpcFrequency, std::uint64_t& allowance100ns) noexcept;
    bool TimelineFrameTime(std::uint64_t anchorQpc100ns, std::uint64_t nominalFrames, std::uint64_t& time100ns) noexcept;
    bool TimelineSilenceBudget(std::int64_t audioEpoch100ns, std::uint64_t retainedFrames,
        std::int64_t videoEnd100ns, std::uint32_t maximumFrames, std::uint32_t& frames) noexcept;

    // One caller-owned worker. No PCM storage, mutation or resampling.
    // An explicit stopped-source pause/resume may
    // replace the source QPC anchor while preserving the continuous AAC index;
    // flagged source discontinuities with valid increasing timestamps use the
    // same bounded silence/overlap recovery. Unusable timestamps remain errors.
    // Saving does not reset this timeline.
    // Valid samples before the common video epoch are explicitly discarded;
    // an overlapping packet is trimmed at the first nominal PCM boundary on or
    // after it. The first retained sample sets AAC's epoch, with index0. Once
    // Inspect returns Feed, AAC must consume that slice once or end the session.
    class AudioTimeline final
    {
    public:
        bool Initialize(const TimelineOptions&) noexcept;
        bool Pause() noexcept;
        // Call only after stopping/joining the old producer. Consume any last
        // accepted slice before inspecting the new source. Pre-resume source
        // samples are trimmed; the old AAC index and media origin are retained.
        bool Resume(std::uint64_t minimumSourceTime100ns, std::int64_t initialMediaTime100ns = 0) noexcept;
        // Keep the AAC origin/index while mapping resumed silence to video time.
        // The caller owns zero PCM storage and limits work to video progress.
        bool AnchorResumedSilence(std::uint64_t sourceTime100ns, std::int64_t mediaTime100ns) noexcept;
        bool AdvanceSilence(std::uint32_t frames, FeedSlice&) noexcept;
        TimelineResult Inspect(const Packet&, FeedSlice&) noexcept;
        const TimelineEvidence& Result() const noexcept { return evidence_; }
    private:
        TimelineResult Fail(const char*) noexcept;
        TimelineOptions options_{};
        TimelineEvidence evidence_{};
        bool havePreviousPacket_ = false;
        bool resumeAnchor_ = false;
        std::uint64_t previousQpc100ns_ = 0;
        // Original first retained PACKET timestamp plus its source-frame count.
        // Keep the trim phase separate from AAC's rounded epoch/index clock.
        std::uint64_t sourceAnchorQpc100ns_ = 0, sourceFramesSinceAnchor_ = 0;
        std::uint64_t silenceCoveredUntil100ns_ = 0;
    };
    unsigned RunAudioTimelineContracts(unsigned* failedCheck = nullptr) noexcept;
}
