#include "AudioTimeline.h"

#include <limits>

namespace recorder::audio
{
    namespace
    {
        constexpr std::uint64_t TicksPerSecond = 10000000;
        constexpr std::uint64_t MaximumTime = static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)());
        constexpr std::uint32_t MaximumFeedFrames = 4800; // Reviewed AAC Feed/production queue bound.
        bool Sum(std::uint64_t value, std::uint64_t increment) noexcept
        { return increment <= (std::numeric_limits<std::uint64_t>::max)() - value; }
        std::uint64_t CeilQuotient(std::uint64_t numerator, std::uint64_t denominator) noexcept
        { return numerator / denominator + (numerator % denominator != 0 ? 1 : 0); }
        // Difference is bounded to signed QPC time, so dividing first prevents
        // overflow while finding the first nominal sample at/after the epoch.
        std::uint64_t FramesToEpoch(std::uint64_t difference) noexcept
        {
            return (difference / TicksPerSecond) * SampleRate +
                CeilQuotient((difference % TicksPerSecond) * SampleRate, TicksPerSecond);
        }
    }
    bool TimelineFrameTime(std::uint64_t origin, std::uint64_t frames, std::uint64_t& time) noexcept
    {
        time = 0;
        if (origin > MaximumTime || frames / SampleRate > MaximumTime / TicksPerSecond) return false;
        const auto whole = (frames / SampleRate) * TicksPerSecond;
        const auto fraction = (frames % SampleRate) * TicksPerSecond / SampleRate;
        if (whole > MaximumTime - fraction || whole + fraction > MaximumTime - origin) return false;
        time = origin + whole + fraction;
        return true;
    }
    bool TimelineAllowance(std::uint64_t frequency, std::uint64_t& allowance) noexcept
    {
        allowance = 0;
        // Do not allow a coarse counter to silently expand a sample-scale budget.
        if (frequency < SampleRate || frequency > MaximumTime) return false;
        allowance = CeilQuotient(TicksPerSecond, SampleRate) + CeilQuotient(TicksPerSecond, frequency) + 2;
        return true;
    }
    TimelineResult AudioTimeline::Fail(const char* reason) noexcept
    {
        evidence_.reason = reason; evidence_.failed = true;
        return TimelineResult::Failed;
    }
    bool AudioTimeline::Initialize(const TimelineOptions& options) noexcept
    {
        if (evidence_.initialized || evidence_.failed)
        { (void)Fail("audio_timeline_not_fresh"); return false; }
        std::uint64_t allowance = 0;
        if (options.videoEpochQpc100ns == 0 || options.videoEpochQpc100ns > MaximumTime ||
            !TimelineAllowance(options.qpcFrequency, allowance))
        { (void)Fail("audio_timeline_options_invalid"); return false; }
        options_ = options;
        evidence_.policyAllowance100ns = allowance;
        evidence_.initialized = true; evidence_.reason = "audio_timeline_initialized";
        return true;
    }
    TimelineResult AudioTimeline::Inspect(const Packet& packet, FeedSlice& slice) noexcept
    {
        slice = {};
        if (evidence_.failed) return TimelineResult::Failed;
        if (!evidence_.initialized) return Fail("audio_timeline_not_usable");
        if (packet.frames == 0 || packet.frames > MaximumFeedFrames ||
            packet.samples.size() != static_cast<std::size_t>(packet.frames) * Channels)
            return Fail("audio_timeline_packet_shape_invalid");
        if (!packet.timestampValid || packet.timestampError || packet.qpc100ns == 0 || packet.qpc100ns > MaximumTime)
            return Fail("audio_timeline_timestamp_unavailable");
        if (havePreviousPacket_ && packet.qpc100ns <= previousQpc100ns_)
            return Fail("audio_timeline_timestamp_not_increasing");
        if (evidence_.anchored && packet.discontinuity) return Fail("audio_timeline_source_discontinuity");
        std::uint64_t packetEnd = 0;
        if (!TimelineFrameTime(packet.qpc100ns, packet.frames, packetEnd)) return Fail("audio_timeline_clock_overflow");
        if (!Sum(evidence_.sourcePackets, 1)) return Fail("audio_timeline_counter_limit");

        std::uint32_t skip = 0;
        std::int64_t residual = 0;
        std::uint64_t absoluteResidual = 0;
        if (evidence_.anchored)
        {
            std::uint64_t expected = 0, expectedEnd = 0;
            if (!Sum(sourceFramesSinceAnchor_, packet.frames) ||
                !TimelineFrameTime(sourceAnchorQpc100ns_, sourceFramesSinceAnchor_, expected) ||
                !TimelineFrameTime(sourceAnchorQpc100ns_, sourceFramesSinceAnchor_ + packet.frames, expectedEnd))
                return Fail("audio_timeline_clock_overflow");
            residual = static_cast<std::int64_t>(packet.qpc100ns) - static_cast<std::int64_t>(expected);
            absoluteResidual = residual < 0 ? static_cast<std::uint64_t>(-residual) : static_cast<std::uint64_t>(residual);
            if (!Sum(evidence_.residualChecks, 1)) return Fail("audio_timeline_counter_limit");
            ++evidence_.residualChecks;
            evidence_.lastResidual100ns = residual;
            if (absoluteResidual > evidence_.maximumAbsoluteResidual100ns) evidence_.maximumAbsoluteResidual100ns = absoluteResidual;
            if (absoluteResidual > evidence_.policyAllowance100ns) return Fail("audio_timeline_clock_outside_policy");
        }
        else if (packet.qpc100ns < options_.videoEpochQpc100ns)
        {
            const auto toEpoch = FramesToEpoch(options_.videoEpochQpc100ns - packet.qpc100ns);
            skip = toEpoch < packet.frames ? static_cast<std::uint32_t>(toEpoch) : packet.frames;
        }
        if (!Sum(evidence_.discardedFrames, skip)) return Fail("audio_timeline_counter_limit");
        const std::uint32_t retained = packet.frames - skip;
        if (retained == 0)
        {
            if (!Sum(evidence_.beforeEpochPackets, 1)) return Fail("audio_timeline_counter_limit");
            ++evidence_.sourcePackets; ++evidence_.beforeEpochPackets;
            evidence_.discardedFrames += skip;
            evidence_.initialDiscontinuityObserved |= packet.discontinuity;
            previousQpc100ns_ = packet.qpc100ns; havePreviousPacket_ = true;
            evidence_.reason = "audio_before_video_epoch";
            slice.skippedPrefixFrames = skip;
            return TimelineResult::BeforeVideoEpoch;
        }
        if (!Sum(evidence_.acceptedPackets, 1) || !Sum(evidence_.retainedFrames, retained)) return Fail("audio_timeline_counter_limit");
        std::int64_t audioEpoch = evidence_.audioEpochTime100ns;
        if (!evidence_.anchored)
        {
            std::uint64_t firstRetainedTime = 0;
            if (!TimelineFrameTime(packet.qpc100ns, skip, firstRetainedTime) || firstRetainedTime < options_.videoEpochQpc100ns)
                return Fail("audio_timeline_initial_trim_invalid");
            audioEpoch = static_cast<std::int64_t>(firstRetainedTime - options_.videoEpochQpc100ns);
        }
        std::uint64_t encodedEnd = 0;
        if (!TimelineFrameTime(static_cast<std::uint64_t>(audioEpoch), evidence_.retainedFrames + retained, encodedEnd))
            return Fail("audio_timeline_clock_overflow");
        // All validations precede acceptance. No frame is dropped or duplicated
        // after the first retained sample; source residual never adjusts this clock.
        slice.samples = packet.samples.data() + static_cast<std::size_t>(skip) * Channels;
        slice.frames = retained; slice.skippedPrefixFrames = skip;
        slice.firstFrameIndex = evidence_.retainedFrames; slice.audioEpochTime100ns = audioEpoch;
        if (!evidence_.anchored)
        {
            sourceAnchorQpc100ns_ = packet.qpc100ns; sourceFramesSinceAnchor_ = 0;
            evidence_.audioEpochTime100ns = audioEpoch;
            evidence_.initialDiscontinuityObserved |= packet.discontinuity;
            evidence_.anchored = true;
        }
        sourceFramesSinceAnchor_ += packet.frames;
        ++evidence_.sourcePackets; ++evidence_.acceptedPackets;
        evidence_.discardedFrames += skip; evidence_.retainedFrames += retained;
        previousQpc100ns_ = packet.qpc100ns; havePreviousPacket_ = true;
        evidence_.reason = "audio_slice_ready";
        return TimelineResult::Feed;
    }
}
