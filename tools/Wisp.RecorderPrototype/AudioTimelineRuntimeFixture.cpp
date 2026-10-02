#include "AudioTimeline.h"

#include <tlhelp32.h>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <limits>
#include <thread>

namespace
{
    constexpr DWORD CaptureMs = 15000, ObservationDeadlineMs = 19000;
    constexpr std::uint64_t MaximumPackets = 10000, MaximumFrames = 30ull * recorder::audio::SampleRate;
    struct Failure {};
    void Require(bool condition) { if (!condition) throw Failure{}; }
    const char* Json(bool value) noexcept { return value ? "true" : "false"; }
    [[noreturn]] void TerminateSelf() noexcept
    {
        (void)TerminateProcess(GetCurrentProcess(), 30);
        std::terminate();
    }
    struct Handle final
    {
        HANDLE value = nullptr;
        ~Handle() { (void)Close(); }
        Handle() = default;
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        bool Close() noexcept
        {
            if (!value || value == INVALID_HANDLE_VALUE) return true;
            if (!CloseHandle(value)) return false;
            value = nullptr; return true;
        }
    };
    enum class GameState { Absent, Running, Unknown };
    GameState CheckGame() noexcept
    {
        Handle snapshot;
        snapshot.value = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.value == INVALID_HANDLE_VALUE) return GameState::Unknown;
        PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
        if (!Process32FirstW(snapshot.value, &entry)) return GameState::Unknown;
        GameState result = GameState::Absent;
        do
        {
            if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0)
            { result = GameState::Running; break; }
        } while (Process32NextW(snapshot.value, &entry));
        if (result == GameState::Absent && GetLastError() != ERROR_NO_MORE_FILES) result = GameState::Unknown;
        return snapshot.Close() ? result : GameState::Unknown;
    }
    void Signal(HANDLE event) noexcept { if (!SetEvent(event)) TerminateSelf(); }

    struct SourceWorker final
    {
        HANDLE stop;
        Handle done;
        recorder::audio::Evidence evidence{};
        std::thread thread;
        SourceWorker(const recorder::audio::Target& target, HANDLE stopEvent, recorder::audio::PacketQueue& queue) : stop(stopEvent)
        {
            done.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            Require(done.value != nullptr);
            thread = std::thread([this, target, &queue]
            {
                recorder::audio::Options options;
                options.activationTimeoutMs = 3000; options.maximumCaptureMs = CaptureMs;
                evidence = recorder::audio::RunProcessLoopback(target, options, stop, queue);
                Signal(done.value);
            });
        }
        ~SourceWorker() { if (thread.joinable()) { Signal(stop); Finish(); } }
        void Finish() noexcept
        {
            if (!thread.joinable()) return;
            if (WaitForSingleObject(thread.native_handle(), 2000) != WAIT_OBJECT_0) TerminateSelf();
            try { thread.join(); } catch (...) { TerminateSelf(); }
        }
    };

    struct Observation
    {
        recorder::audio::AudioTimeline timeline;
        std::uint64_t packets = 0, frames = 0, validTimestamps = 0, timestampErrors = 0, discontinuities = 0;
        std::uint64_t nonzeroSamples = 0, aboveOneLsbSamples = 0;
        std::uint32_t maximumAbsoluteSample = 0;
        std::uint64_t anchor = 0, anchorFrame = 0, previousTimestamp = 0;
        std::uint64_t residualChecks = 0, maximumAbsoluteResidual = 0;
        std::int64_t minimumResidual = 0, maximumResidual = 0, lastResidual = 0;
        std::uint64_t feedPackets = 0, beforeEpochPackets = 0, firstFailurePacket = 0, afterFailurePackets = 0;
        bool increasingTimestamps = true, timelineInitialized = false;
        const char* timelineReason = "no_packets";

        void Consume(const recorder::audio::Packet& packet, std::uint64_t frequency)
        {
            Require(packets < MaximumPackets && packet.frames > 0 && packet.frames <= 4800 &&
                packet.samples.size() == static_cast<std::size_t>(packet.frames) * recorder::audio::Channels &&
                frames <= MaximumFrames - packet.frames);
            ++packets;
            timestampErrors += packet.timestampError ? 1 : 0;
            discontinuities += packet.discontinuity ? 1 : 0;
            for (const auto sample : packet.samples)
            {
                const auto magnitude = static_cast<std::uint32_t>(sample < 0 ? -static_cast<int>(sample) : sample);
                maximumAbsoluteSample = (std::max)(maximumAbsoluteSample, magnitude);
                nonzeroSamples += magnitude != 0 ? 1 : 0;
                aboveOneLsbSamples += magnitude > 1 ? 1 : 0;
            }
            constexpr auto maximumTime = static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)());
            const bool valid = packet.timestampValid && !packet.timestampError && packet.qpc100ns > 0 && packet.qpc100ns <= maximumTime;
            if (valid)
            {
                if (validTimestamps) increasingTimestamps &= packet.qpc100ns > previousTimestamp;
                previousTimestamp = packet.qpc100ns;
                ++validTimestamps;
                if (!anchor) { anchor = packet.qpc100ns; anchorFrame = frames; }
                else
                {
                    // Independent rational sample clock. Preserve this original
                    // anchor even after the production policy rejects a packet.
                    const auto sinceAnchor = frames - anchorFrame;
                    const auto nominal = (sinceAnchor / recorder::audio::SampleRate) * 10000000 +
                        (sinceAnchor % recorder::audio::SampleRate) * 10000000 / recorder::audio::SampleRate;
                    Require(anchor <= maximumTime - nominal);
                    lastResidual = static_cast<std::int64_t>(packet.qpc100ns) - static_cast<std::int64_t>(anchor + nominal);
                    const auto magnitude = static_cast<std::uint64_t>(lastResidual < 0 ? -lastResidual : lastResidual);
                    maximumAbsoluteResidual = (std::max)(maximumAbsoluteResidual, magnitude);
                    if (!residualChecks) minimumResidual = maximumResidual = lastResidual;
                    else { minimumResidual = (std::min)(minimumResidual, lastResidual); maximumResidual = (std::max)(maximumResidual, lastResidual); }
                    ++residualChecks;
                }
            }
            if (packets == 1)
            {
                timelineInitialized = valid && timeline.Initialize({ packet.qpc100ns, frequency });
                if (!timelineInitialized) { firstFailurePacket = packets; timelineReason = "first_timestamp_unavailable"; }
            }
            if (timelineInitialized && !firstFailurePacket)
            {
                recorder::audio::FeedSlice slice;
                const auto decision = timeline.Inspect(packet, slice);
                timelineReason = timeline.Result().reason;
                if (decision == recorder::audio::TimelineResult::Feed) ++feedPackets;
                else if (decision == recorder::audio::TimelineResult::BeforeVideoEpoch) ++beforeEpochPackets;
                else firstFailurePacket = packets;
            }
            else if (firstFailurePacket && packets > firstFailurePacket) ++afterFailurePackets;
            frames += packet.frames;
        }
    };

    int Refuse(const char* reason) noexcept
    {
        std::printf("{\"fixture\":\"self_audio_timeline\",\"completed\":false,\"reason\":\"%s\","
            "\"audioActivated\":false,\"playbackUsed\":false,\"filesWritten\":false}\n", reason);
        return 2;
    }
    int Run()
    {
        const auto before = CheckGame();
        if (before != GameState::Absent)
            return Refuse(before == GameState::Running ? "close_game_before_fixture" : "game_state_unavailable");
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart < recorder::audio::SampleRate)
            return Refuse("qpc_frequency_unavailable");
        Handle process, stop;
        if (!DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(), &process.value,
            PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, 0)) return Refuse("self_process_handle_failed");
        FILETIME created{}, exited{}, kernel{}, user{};
        if (!GetProcessTimes(process.value, &created, &exited, &kernel, &user)) return Refuse("self_process_identity_failed");
        stop.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!stop.value) return Refuse("stop_event_failed");
        const recorder::audio::Target target{ process.value, GetCurrentProcessId(),
            (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime };
        recorder::audio::PacketQueue queue; // Exact production queue limits, concurrently drained.
        Observation observation;
        SourceWorker worker(target, stop.value, queue);
        const auto began = GetTickCount64();
        auto nextGameCheck = began;
        bool gameAbsent = true, deadlineReached = false, consumerFailed = false;
        for (;;)
        {
            const auto now = GetTickCount64();
            if (now >= nextGameCheck)
            {
                gameAbsent = CheckGame() == GameState::Absent;
                nextGameCheck = now + 100;
                if (!gameAbsent) { Signal(stop.value); break; }
            }
            if (now - began >= ObservationDeadlineMs) { deadlineReached = true; Signal(stop.value); break; }
            try
            {
                std::unique_ptr<const recorder::audio::Packet> packet;
                for (unsigned batch = 0; batch < 128 && queue.TryPop(packet); ++batch)
                { observation.Consume(*packet, static_cast<std::uint64_t>(frequency.QuadPart)); packet.reset(); }
            }
            catch (...) { consumerFailed = true; Signal(stop.value); break; }
            const DWORD done = WaitForSingleObject(worker.done.value, 5);
            Require(done == WAIT_TIMEOUT || done == WAIT_OBJECT_0);
            if (done == WAIT_OBJECT_0 && queue.Snapshot().queuedPackets == 0) break;
        }
        worker.Finish();
        const auto& source = worker.evidence;
        const auto drained = queue.Snapshot();
        gameAbsent = gameAbsent && CheckGame() == GameState::Absent;
        const bool accounting = observation.packets == source.queue.acceptedPackets && observation.frames == source.queue.acceptedFrames &&
            drained.closed && drained.queuedPackets == 0 && drained.queuedBytes == 0;
        const bool sourceComplete = source.completed && source.targetValidated && source.activationResultReceived &&
            !source.activationAbandoned && !source.lateActivationCallbackPossible && source.initializedPreferredFormat &&
            source.started && source.stopped && SUCCEEDED(source.hr) && SUCCEEDED(source.stopHr) && SUCCEEDED(source.releaseBufferHr) &&
            std::strcmp(source.reason, "duration_complete") == 0;
        const bool doneClosed = worker.done.Close(), processClosed = process.Close(), stopClosed = stop.Close();
        const bool handlesClosed = doneClosed && processClosed && stopClosed;
        const bool complete = gameAbsent && !deadlineReached && !consumerFailed && sourceComplete && accounting && handlesClosed;
        const bool clockComplete = complete && observation.packets > 1 && observation.validTimestamps == observation.packets && observation.increasingTimestamps;
        const auto& timeline = observation.timeline.Result();
        const bool acceptedAll = clockComplete && observation.timelineInitialized && !observation.firstFailurePacket && observation.feedPackets == observation.packets;
        const char* reason = !gameAbsent ? "game_state_changed" : deadlineReached ? "observation_deadline" : consumerFailed ? "consumer_failed" :
            !sourceComplete ? "source_failed" : !accounting ? "packet_accounting_failed" : !handlesClosed ? "handle_close_failed" : "observation_complete";
        // No raw PCM, paths, identities, device positions or absolute QPC times.
        // A completed observation may demonstrate that the strict policy FAILS.
        std::printf("{\"fixture\":\"self_audio_timeline\",\"completed\":%s,\"reason\":\"%s\","
            "\"targetIsSelf\":true,\"processTreeOnly\":true,\"gameAbsent\":%s,\"playbackUsed\":false,\"filesWritten\":false,"
            "\"format\":\"pcm16_48000_stereo\",\"captureLimitMs\":%lu,\"sourceElapsedMs\":%llu,\"sourceReason\":\"%s\","
            "\"sourceHr\":%lu,\"stopHr\":%lu,\"releaseBufferHr\":%lu,\"qpcFrequency\":%llu,\"packets\":%llu,\"frames\":%llu,"
            "\"peakQueuedBytes\":%llu,\"accountingValid\":%s,\"validTimestampPackets\":%llu,\"timestampErrorPackets\":%llu,"
            "\"discontinuityPackets\":%llu,\"increasingTimestamps\":%s,\"clockObservationComplete\":%s,\"residualChecks\":%llu,"
            "\"maxAbsoluteResidual100ns\":%llu,\"minimumResidual100ns\":%lld,\"maximumResidual100ns\":%lld,\"lastResidual100ns\":%lld,"
            "\"timelineAcceptedAll\":%s,\"timelineReason\":\"%s\",\"policyAllowance100ns\":%llu,\"feedPackets\":%llu,"
            "\"beforeEpochPackets\":%llu,\"firstFailurePacket\":%llu,\"observedPacketsAfterFailure\":%llu,"
            "\"maxAbsolutePcmSample\":%u,\"nonzeroSamples\":%llu,\"aboveOneLsbSamples\":%llu,"
            "\"gameAvSyncVerified\":false,\"policyChanged\":false}\n",
            Json(complete), reason, Json(gameAbsent), CaptureMs, source.elapsedMs, source.reason,
            static_cast<DWORD>(source.hr), static_cast<DWORD>(source.stopHr), static_cast<DWORD>(source.releaseBufferHr),
            static_cast<unsigned long long>(frequency.QuadPart), observation.packets, observation.frames,
            static_cast<unsigned long long>(drained.peakQueuedBytes), Json(accounting), observation.validTimestamps, observation.timestampErrors,
            observation.discontinuities, Json(observation.increasingTimestamps), Json(clockComplete), observation.residualChecks,
            observation.maximumAbsoluteResidual, observation.minimumResidual, observation.maximumResidual, observation.lastResidual,
            Json(acceptedAll), observation.timelineReason, timeline.policyAllowance100ns, observation.feedPackets, observation.beforeEpochPackets,
            observation.firstFailurePacket, observation.afterFailurePackets, observation.maximumAbsoluteSample, observation.nonzeroSamples, observation.aboveOneLsbSamples);
        return complete ? 0 : 3;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && std::wcscmp(argv[1], L"--help") == 0))
    {
        std::puts("Wisp audio timeline probe. --self-silence:15-second self-process PCM16 cadence observation; no playback or files.");
        return 0;
    }
    if (argc != 2 || std::wcscmp(argv[1], L"--self-silence") != 0) return Refuse("invalid_arguments");
    try { return Run(); }
    catch (...)
    {
        std::puts("{\"fixture\":\"self_audio_timeline\",\"completed\":false,\"reason\":\"unexpected_fixture_failure\","
            "\"audioActivationState\":\"not_reported\",\"playbackUsed\":false,\"filesWritten\":false}");
        return 3;
    }
}
