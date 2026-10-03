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
            test(TimelineAllowance(10000000, allowance) && allowance == 213337);
            test(TimelineAllowance(3125000, allowance) && allowance == 213340);
            test(TimelineAllowance(48000, allowance) && allowance == 213545);
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
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 480 &&
                    slice.frames == 480 && slice.audioEpochTime100ns == 0 && !timeline.Result().failed);
                packet = MakePacket(Epoch + 200000);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 960 &&
                    timeline.Result().retainedFrames == 1440 && timeline.Result().generatedSilenceFrames == 0);
            }
            {
                // A flagged half-second capture gap fills through bounded chunks
                // without replacing the AAC epoch or accepting the packet twice.
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                auto packet = MakePacket(Epoch + 5100000); packet.discontinuity = true;
                bool continuous = true;
                std::uint64_t nextIndex = 480;
                unsigned chunks = 0;
                auto result = timeline.Inspect(packet, slice);
                while (result == TimelineResult::NeedsSilence && chunks < 6)
                {
                    const auto needed = slice.silenceFramesNeeded;
                    continuous = continuous && needed > 0 && needed <= 4800 &&
                        timeline.Result().sourcePackets == 1 && timeline.Result().acceptedPackets == 1;
                    continuous = timeline.AdvanceSilence(needed, slice) && continuous &&
                        slice.generatedSilence && slice.firstFrameIndex == nextIndex && slice.audioEpochTime100ns == 0;
                    nextIndex += slice.frames; ++chunks;
                    result = timeline.Inspect(packet, slice);
                }
                test(continuous && chunks == 5 && result == TimelineResult::Feed &&
                    slice.firstFrameIndex == 24480 && slice.frames == 480 && slice.samples == packet.samples.data());
                test(timeline.Result().generatedSilenceFrames == 24000 && timeline.Result().retainedFrames == 24960 &&
                    timeline.Result().sourcePackets == 2 && timeline.Result().acceptedPackets == 2 && !timeline.Result().failed);
                packet = MakePacket(Epoch + 5200000);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 24960 &&
                    slice.audioEpochTime100ns == 0 && timeline.Result().lastResidual100ns == 0);
            }
            {
                // A valid flagged overlap must not replay samples already sent
                // to AAC. A subsequently covered packet is harmless as well.
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch, 4800), slice) == TimelineResult::Feed);
                auto packet = MakePacket(Epoch + 500000, 4800); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 4800 &&
                    slice.skippedPrefixFrames == 2400 && slice.frames == 2400 && slice.samples == packet.samples.data() + 2400 * Channels);
                packet = MakePacket(Epoch + 600000); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::CoveredByTimeline &&
                    slice.skippedPrefixFrames == 480 && slice.samples == nullptr && timeline.Result().retainedFrames == 7200);
                packet = MakePacket(Epoch + 1500000); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 7200 &&
                    slice.audioEpochTime100ns == 0 && timeline.Result().discardedFrames == 2880 && !timeline.Result().failed);
            }
            for (int invalid = 0; invalid < 6; ++invalid)
            {
                // The data-discontinuity flag never overrides an unusable clock.
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions) && timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                auto packet = MakePacket(Epoch + 100000); packet.discontinuity = true;
                if (invalid == 0) packet.timestampError = true;
                if (invalid == 1) packet.timestampValid = false;
                if (invalid == 2) packet.qpc100ns = 0;
                if (invalid == 3) packet.qpc100ns = Epoch;
                if (invalid == 4) packet.qpc100ns = Epoch - 1;
                if (invalid == 5) packet.qpc100ns = maximum;
                test(timeline.Inspect(packet, slice) == TimelineResult::Failed && slice.samples == nullptr &&
                    timeline.Result().retainedFrames == 480 && timeline.Result().sourcePackets == 1);
                const auto* expected = invalid <= 2 ? "audio_timeline_timestamp_unavailable" :
                    invalid <= 4 ? "audio_timeline_timestamp_not_increasing" : "audio_timeline_clock_overflow";
                test(std::strcmp(timeline.Result().reason, expected) == 0);
                test(timeline.Inspect(MakePacket(Epoch + 200000), slice) == TimelineResult::Failed &&
                    std::strcmp(timeline.Result().reason, expected) == 0 && slice.samples == nullptr);
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
                    test(status == TimelineResult::Feed);
                }
                test(timeline.Result().maximumAbsoluteResidual100ns == 300 && timeline.Result().residualChecks == 3 &&
                    timeline.Result().retainedFrames == 1920 && !timeline.Result().failed);
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                test(timeline.Inspect(MakePacket(Epoch + 100000 + 212), slice) == TimelineResult::Feed);
                test(timeline.Inspect(MakePacket(Epoch + 200000 + 213), slice) == TimelineResult::Feed &&
                    slice.firstFrameIndex == 960 && slice.audioEpochTime100ns == 0);
                test(timeline.Inspect(MakePacket(Epoch + 300000), slice) == TimelineResult::Feed &&
                    slice.firstFrameIndex == 1440 && timeline.Result().generatedSilenceFrames == 0);
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                const auto packet = MakePacket(Epoch + 100000 + 213338);
                test(timeline.Inspect(packet, slice) == TimelineResult::NeedsSilence && slice.silenceFramesNeeded == 1025 &&
                    timeline.Result().retainedFrames == 480 && !timeline.Result().failed);
                test(timeline.AdvanceSilence(slice.silenceFramesNeeded, slice) && slice.generatedSilence &&
                    slice.firstFrameIndex == 480 && slice.frames == 1025 && slice.samples == nullptr);
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 1505 &&
                    slice.skippedPrefixFrames == 1 && slice.frames == 479 && slice.samples == packet.samples.data() + Channels);
            }
            for (int sign : { -1, 1 })
            {
                // Ten seconds with 1% source drift: corrections bound endpoint
                // error instead of allowing residual to grow with recording length.
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                std::uint64_t nextIndex = 0, lastSourceEnd = 0;
                bool bounded = true;
                for (unsigned index = 0; index <= 1000 && bounded; ++index)
                {
                    const auto nominal = Epoch + index * 100000ull;
                    auto packet = MakePacket(sign > 0 ? nominal + index * 1000ull : nominal - index * 1000ull);
                    auto status = timeline.Inspect(packet, slice);
                    unsigned corrections = 0;
                    while (status == TimelineResult::NeedsSilence && corrections++ < 2)
                    {
                        bounded = timeline.AdvanceSilence(slice.silenceFramesNeeded, slice) &&
                            slice.firstFrameIndex == nextIndex && slice.frames <= 4800;
                        nextIndex += slice.frames;
                        if (!bounded) break;
                        status = timeline.Inspect(packet, slice);
                    }
                    if (status == TimelineResult::Feed)
                    {
                        bounded = bounded && slice.firstFrameIndex == nextIndex && !slice.generatedSilence;
                        nextIndex += slice.frames;
                    }
                    else bounded = bounded && status == TimelineResult::CoveredByTimeline;
                    std::uint64_t endpoint = 0;
                    bounded = bounded && TimelineFrameTime(0, nextIndex, endpoint) && TimelineFrameTime(packet.qpc100ns, packet.frames, lastSourceEnd);
                    const auto sourceEnd = lastSourceEnd - Epoch;
                    const auto error = endpoint > sourceEnd ? endpoint - sourceEnd : sourceEnd - endpoint;
                    bounded = bounded && error <= timeline.Result().policyAllowance100ns + 209 && !timeline.Result().failed;
                }
                test(bounded && nextIndex == timeline.Result().retainedFrames && timeline.Result().sourcePackets == 1001);
                test(sign < 0 ? timeline.Result().discardedFrames > 0 : timeline.Result().generatedSilenceFrames > 0);
            }
            {
                // An established track keeps its AAC index through an arbitrary
                // paused wall-clock gap, silence fallback, and a late source return.
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch, 4800), slice) == TimelineResult::Feed);
                constexpr auto resumed = Epoch + 100000000;
                test(timeline.Pause() && timeline.Resume(resumed));
                test(timeline.AnchorResumedSilence(resumed, 1000000));
                test(timeline.AdvanceSilence(4800, slice) && slice.firstFrameIndex == 4800 &&
                    slice.audioEpochTime100ns == 0 && slice.sourceTime100ns == resumed);
                auto covered = MakePacket(resumed); covered.discontinuity = true;
                test(timeline.Inspect(covered, slice) == TimelineResult::CoveredByTimeline &&
                    slice.skippedPrefixFrames == 480 && timeline.Result().retainedFrames == 9600);
                auto returning = MakePacket(resumed + 500000, 4800);
                test(timeline.Inspect(returning, slice) == TimelineResult::Feed && slice.firstFrameIndex == 9600 &&
                    slice.skippedPrefixFrames == 2400 && slice.frames == 2400 && slice.audioEpochTime100ns == 0 &&
                    slice.samples == returning.samples.data() + 2400 * Channels);
                test(timeline.Result().retainedFrames == 12000 && timeline.Result().generatedSilenceFrames == 4800 &&
                    !timeline.Result().failed);
            }
            for (const std::int64_t videoTime : { 900000ll, 1100000ll })
            {
                // Preserve a pre-pause 10ms AAC lead/lag while rebasing to video;
                // neither paused wall time nor a second video rebase enters AAC.
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions) && timeline.Inspect(MakePacket(Epoch, 4800), slice) == TimelineResult::Feed);
                constexpr auto resumed = Epoch + 100000000;
                test(timeline.Pause() && timeline.Resume(resumed));
                test(!timeline.AnchorResumedSilence(resumed - 1, videoTime));
                test(timeline.AnchorResumedSilence(resumed, videoTime));
                test(timeline.AdvanceSilence(480, slice) && slice.firstFrameIndex == 4800 &&
                    slice.audioEpochTime100ns == 0 && slice.sourceTime100ns == resumed + 1000000 - videoTime);
                std::uint64_t end = 0;
                test(TimelineFrameTime(0, timeline.Result().retainedFrames, end) && end == 1100000);
                const auto packetTime = resumed + end - videoTime;
                test(timeline.Inspect(MakePacket(packetTime), slice) == TimelineResult::Feed &&
                    slice.firstFrameIndex == 5280 && slice.skippedPrefixFrames == 0);
            }
            {
                std::uint32_t frames = 0;
                test(TimelineSilenceBudget(0, 0, 0, 4800, frames) && frames == 1024);
                test(TimelineSilenceBudget(0, 1024, 0, 4800, frames) && frames == 0);
                test(TimelineSilenceBudget(0, 0, 1000000000, 99999, frames) && frames == 4800);
                test(TimelineSilenceBudget(0, 0, 1000000000, 17, frames) && frames == 17);
                test(!TimelineSilenceBudget(-1, 0, 0, 4800, frames));
                test(!TimelineSilenceBudget(0, 0, -1, 4800, frames));
                test(!TimelineSilenceBudget(0, 0, static_cast<std::int64_t>(maximum), 4800, frames));
                test(!TimelineSilenceBudget(0, (std::numeric_limits<std::uint64_t>::max)(), 0, 4800, frames));
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions) && timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                test(!timeline.AdvanceSilence(4801, slice) && timeline.Result().failed);
            }
            {
                // The budget bounds every generated endpoint, including rounds
                // which need several AAC input chunks to catch up with video.
                std::uint64_t retained = 0, end = 0;
                bool bounded = true;
                for (std::int64_t video = 0; video <= 100000000 && bounded; video += 166666)
                {
                    std::uint32_t frames = 0;
                    bounded = TimelineSilenceBudget(0, retained, video, 4800, frames);
                    retained += frames;
                    bounded = bounded && frames <= 4800 && TimelineFrameTime(0, retained, end) &&
                        end <= static_cast<std::uint64_t>(video) + 213334 &&
                        end + 209 >= static_cast<std::uint64_t>(video) + 213334;
                }
                test(bounded);
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions) && timeline.Inspect(MakePacket(Epoch), slice) == TimelineResult::Feed);
                test(timeline.Pause() && timeline.Resume(maximum - 100000));
                test(!timeline.AnchorResumedSilence(maximum, 0));
                test(timeline.AnchorResumedSilence(maximum - 100000, 100000));
                test(!timeline.AdvanceSilence(481, slice) && timeline.Result().failed);
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
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions));
                test(timeline.Inspect(MakePacket(Epoch + 208), slice) == TimelineResult::Feed);
                test(slice.firstFrameIndex == 0 && slice.audioEpochTime100ns == 208 && slice.sourceTime100ns == Epoch + 208);
                test(!timeline.Resume(Epoch + 10000000)); // Re-anchoring needs an explicit pause.
                test(timeline.Pause() && timeline.Result().paused);
                test(!timeline.Pause());
                constexpr auto resumed = Epoch + 3000000000ull;
                test(timeline.Resume(resumed + 1) && !timeline.Result().paused && !timeline.Result().anchored);
                auto packet = MakePacket(resumed); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed);
                test(slice.firstFrameIndex == 480 && slice.frames == 479 && slice.skippedPrefixFrames == 1 &&
                    slice.audioEpochTime100ns == 208 && slice.sourceTime100ns == resumed + 208);
                test(timeline.Inspect(MakePacket(resumed + 100000), slice) == TimelineResult::Feed &&
                    slice.firstFrameIndex == 959 && timeline.Result().lastResidual100ns == 0);
                // A second wall-clock gap keeps every previous AAC frame index.
                test(timeline.Pause() && timeline.Resume(resumed + 10000000));
                test(timeline.Inspect(MakePacket(resumed + 10000000), slice) == TimelineResult::Feed &&
                    slice.firstFrameIndex == 1439 && slice.audioEpochTime100ns == 208);
                packet = MakePacket(resumed + 10100000); packet.discontinuity = true;
                test(timeline.Inspect(packet, slice) == TimelineResult::Feed && slice.firstFrameIndex == 1919 &&
                    slice.audioEpochTime100ns == 208 && !timeline.Result().failed);
                test(timeline.Pause() && timeline.Resume(resumed + 20000000));
            }
            {
                AudioTimeline timeline; FeedSlice slice;
                test(timeline.Initialize(FixtureOptions) && timeline.Pause());
                test(!timeline.Resume(0) && !timeline.Resume(maximum + 1) && !timeline.Resume(Epoch + 1, -1));
                test(timeline.Result().paused && timeline.Result().retainedFrames == 0);
                test(timeline.Resume(Epoch + 10000000, 333333));
                test(timeline.Inspect(MakePacket(Epoch + 10000000), slice) == TimelineResult::Feed &&
                    slice.firstFrameIndex == 0 && slice.audioEpochTime100ns == 333333);
                test(timeline.Pause());
                test(!timeline.Resume(Epoch + 10000000));
                test(timeline.Inspect(MakePacket(Epoch + 10100000), slice) == TimelineResult::Failed);
            }
            {
                // Startup can hold an accepted slice until video bootstrap.
                // A resume preview must not accept a second slice ahead of it.
                AudioTimeline timeline; FeedSlice held, previewSlice, actual;
                const auto first = MakePacket(Epoch + 208);
                test(timeline.Initialize(FixtureOptions) && timeline.Inspect(first, held) == TimelineResult::Feed);
                constexpr auto resumed = Epoch + 10000000;
                test(timeline.Pause() && timeline.Resume(resumed + 1));
                auto packet = MakePacket(resumed);
                auto preview = timeline;
                test(preview.Inspect(packet, previewSlice) == TimelineResult::Feed);
                test(timeline.Result().retainedFrames == 480 && timeline.Result().acceptedPackets == 1 &&
                    !timeline.Result().anchored && held.firstFrameIndex == 0 && held.frames == 480);
                test(previewSlice.firstFrameIndex == 480 && previewSlice.skippedPrefixFrames == 1 &&
                    previewSlice.audioEpochTime100ns == 208 && previewSlice.sourceTime100ns == resumed + 208);
                test(timeline.Inspect(packet, actual) == TimelineResult::Feed &&
                    actual.firstFrameIndex == previewSlice.firstFrameIndex && actual.frames == previewSlice.frames &&
                    actual.audioEpochTime100ns == previewSlice.audioEpochTime100ns && actual.sourceTime100ns == previewSlice.sourceTime100ns);
                test(timeline.Result().retainedFrames == 959 && timeline.Result().acceptedPackets == 2);

                // A second pause abandons only the unaccepted preview packet.
                test(timeline.Pause() && timeline.Resume(resumed + 10000000));
                preview = timeline;
                packet = MakePacket(resumed + 10000000);
                test(preview.Inspect(packet, previewSlice) == TimelineResult::Feed && previewSlice.firstFrameIndex == 959);
                test(timeline.Pause() && timeline.Resume(resumed + 20000000));
                packet = MakePacket(resumed + 20000000);
                preview = timeline;
                test(preview.Inspect(packet, previewSlice) == TimelineResult::Feed && previewSlice.firstFrameIndex == 959);
                test(timeline.Inspect(packet, actual) == TimelineResult::Feed && actual.firstFrameIndex == 959 &&
                    actual.sourceTime100ns == previewSlice.sourceTime100ns && timeline.Result().retainedFrames == 1439);
            }
        }
        catch (...) { if (failedCheck) *failedCheck = checks + 1; return 0; }
        if (failedCheck) *failedCheck = failure;
        return failure == 0 ? checks : 0;
    }
}
