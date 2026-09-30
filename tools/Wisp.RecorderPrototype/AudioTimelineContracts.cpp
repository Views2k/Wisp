#include "AudioTimeline.h"

#include <cstring>
#include <limits>

namespace recorder::audio
{
    namespace
    {
        Packet MakePacket(std::uint64_t time, std::uint32_t frames = 480)
        {
            Packet packet;
            packet.frames = frames; packet.qpc100ns = time; packet.timestampValid = true;
            packet.samples.resize(static_cast<std::size_t>(frames) * Channels, std::int16_t{ 17 });
            return packet;
        }
        constexpr std::uint64_t Epoch = 100000000;
        constexpr TimelineOptions FixtureOptions{ Epoch, 10000000 };
    }
    unsigned RunAudioTimelineContracts(unsigned* failedCheck) noexcept
    {
        unsigned checks = 0, failure = 0;
        const auto test = [&](bool result)
        { ++checks; if (!result && failure == 0) failure = checks; };
        if (failedCheck) *failedCheck = 0;
        try
        {
            std::uint64_t allowance = 0, time = 0;
            test(TimelineAllowance(10000000, allowance) && allowance == 212);
            test(TimelineAllowance(3125000, allowance) && allowance == 215);
            test(TimelineAllowance(48000, allowance) && allowance == 420);
            test(!TimelineAllowance(47999, allowance) && allowance == 0);
            test(!TimelineAllowance(0, allowance));
            test(!TimelineAllowance((std::numeric_limits<std::uint64_t>::max)(), allowance));
            test(TimelineFrameTime(Epoch, 0, time) && time == Epoch);
            test(TimelineFrameTime(Epoch, 1, time) && time == Epoch + 208);
            test(TimelineFrameTime(Epoch, 2, time) && time == Epoch + 416);
            test(TimelineFrameTime(Epoch, 3, time) && time == Epoch + 625);
            test(TimelineFrameTime(Epoch, 6ull * 60 * 60 * SampleRate, time) && time == Epoch + 216000000000ull);
            const auto maximum = static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)());
            test(TimelineFrameTime(maximum - 208, 1, time) && time == maximum);
            test(!TimelineFrameTime(maximum - 207, 1, time) && time == 0);
            test(!TimelineFrameTime(maximum + 1, 0, time));
            test(!TimelineFrameTime(Epoch, (std::numeric_limits<std::uint64_t>::max)(), time));
            {
                AudioTimeline timeline;
                FeedSlice slice;
                test(timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Failed);
                test(!timeline.Initialize(FixtureOptions));
            }
            for (const auto& invalid : { TimelineOptions{}, TimelineOptions{ 0, 10000000 },
                TimelineOptions{ maximum + 1, 10000000 }, TimelineOptions{ Epoch, 0 } })
            { AudioTimeline timeline; test(!timeline.Initialize(invalid)); }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                auto packet = MakePacket(Epoch);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.samples == packet.samples.data() &&
                    slice.frames == 480 && slice.firstFrameIndex == 0 && slice.audioEpochTime100ns == 0 && slice.skippedPrefixFrames == 0);
                packet = MakePacket(Epoch + 100000);
                packet.devicePositionFrames = 0; // Zero virtual positions are not gap evidence.
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 480);
                packet = MakePacket(Epoch + 200000); packet.devicePositionFrames = 987654321;
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 960 &&
                    timeline.Result().lastResidual100ns == 0 && timeline.Result().retainedFrames == 1440);
                test(!timeline.Initialize(FixtureOptions));
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                auto packet = MakePacket(Epoch - 100000); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::BeforeVideoEpoch && slice.frames == 0 &&
                    slice.samples == nullptr && slice.skippedPrefixFrames == 480);
                test(!timeline.Result().anchored && timeline.Result().initialDiscontinuityObserved && timeline.Result().discardedFrames == 480);
                packet = MakePacket(Epoch); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 0 && slice.audioEpochTime100ns == 0);
                packet = MakePacket(Epoch + 100000); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::Failed &&
                    std::strcmp(timeline.Result().reason, "audio_timeline_source_discontinuity") == 0);
                packet.discontinuity = false;
                test(timeline.Inspect(packet, slice) == TimelineResult::Failed && slice.samples == nullptr &&
                    std::strcmp(timeline.Result().reason, "audio_timeline_source_discontinuity") == 0);
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize({ Epoch + 1, 10000000 }));
                auto packet = MakePacket(Epoch);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.skippedPrefixFrames == 1 &&
                    slice.frames == 479 && slice.samples == packet.samples.data() + Channels && slice.audioEpochTime100ns == 207);
                test(packet.samples.size() == 960 && packet.samples[0] == 17); // Borrow, do not alter PCM.
                packet = MakePacket(Epoch + 100000, 441);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 479 &&
                    slice.audioEpochTime100ns == 207 && timeline.Result().lastResidual100ns == 0);
                std::uint64_t next = 0;
                test(TimelineFrameTime(Epoch, 480 + 441, next));
                packet = MakePacket(next, 17);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 920 &&
                    timeline.Result().lastResidual100ns == 0 && timeline.Result().discardedFrames == 1);
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize({ Epoch + 625, 10000000 }));
                test(timeline.Inspect(MakePacket(Epoch, 3), slice) == TimelineResult::BeforeVideoEpoch);
                auto packet = MakePacket(Epoch + 625, 1); packet.silent = true; packet.samples.assign(2, 0);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.frames == 1 && slice.audioEpochTime100ns == 0);
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch + 500000), slice) == TimelineResult::Feed && slice.audioEpochTime100ns == 500000);
            }
            for (int sign : { -1, 1 })
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                for (unsigned index = 1; index <= 3; ++index)
                {
                    const auto nominal = Epoch + index * 100000ull;
                    const auto observed = sign > 0 ? nominal + index * 100ull : nominal - index * 100ull;
                    const auto status = timeline.Inspect(MakePacket(observed), slice);
                    test(status == (index < 3 ? TimelineResult::Feed : TimelineResult::Failed));
                }
                test(timeline.Result().maximumAbsoluteResidual100ns == 300 && timeline.Result().residualChecks == 3 &&
                    std::strcmp(timeline.Result().reason, "audio_timeline_clock_outside_policy") == 0);
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                test(timeline.Inspect(MakePacket(Epoch + 100000 + 212), slice) == TimelineResult::Feed);
                test(timeline.Inspect(MakePacket(Epoch + 200000 + 213), slice) == TimelineResult::Failed);
            }
            for (int bad = 0; bad < 6; ++bad)
            {
                AudioTimeline timeline; FeedSlice slice; test(timeline.Initialize(FixtureOptions));
                auto packet = MakePacket(Epoch);
                if (bad == 0) packet.timestampError = true;
                if (bad == 1) packet.timestampValid = false;
                if (bad == 2) packet.qpc100ns = 0;
                if (bad == 3) packet.frames = 0;
                if (bad == 4) packet.samples.pop_back();
                if (bad == 5) packet.qpc100ns = maximum;
                test(timeline.Inspect(packet, slice) == TimelineResult::Failed && !timeline.Result().anchored);
            }
            for (int adjustment : { 0, -1 })
            {
                AudioTimeline timeline; FeedSlice slice; test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                test(timeline.Inspect(MakePacket(adjustment == 0 ? Epoch : Epoch - 1), slice) == TimelineResult::Failed);
            }
        }
        catch (...) { if (failedCheck) *failedCheck = checks + 1; return 0; }
        if (failedCheck) *failedCheck = failure;
        return failure == 0 ? checks : 0;
    }
}
