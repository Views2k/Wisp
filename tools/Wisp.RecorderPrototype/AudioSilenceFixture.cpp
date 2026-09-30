#include "ProcessAudioCapture.h"

#include <tlhelp32.h>
#include <atomic>
#include <cstdio>
#include <cwchar>
#include <thread>

namespace
{
    struct Handle final
    {
        HANDLE value = nullptr;
        ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
        Handle() = default;
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
    };

    enum class GameState { Absent, Running, Unknown };

    GameState CheckGame() noexcept
    {
        Handle snapshot;
        snapshot.value = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.value == INVALID_HANDLE_VALUE) return GameState::Unknown;
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        if (!Process32FirstW(snapshot.value, &entry)) return GameState::Unknown;
        do
        {
            if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0)
                return GameState::Running;
        } while (Process32NextW(snapshot.value, &entry));
        return GetLastError() == ERROR_NO_MORE_FILES ? GameState::Absent : GameState::Unknown;
    }

    struct GameGuard final
    {
        HANDLE stop;
        Handle done;
        std::atomic<GameState> state{ GameState::Absent };
        std::thread worker;

        explicit GameGuard(HANDLE stopEvent) : stop(stopEvent)
        {
            done.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!done.value) throw 1;
            worker = std::thread([this]
            {
                for (;;)
                {
                    const DWORD wait = WaitForSingleObject(done.value, 100);
                    if (wait == WAIT_OBJECT_0) return;
                    const GameState observed = wait == WAIT_TIMEOUT ? CheckGame() : GameState::Unknown;
                    if (observed != GameState::Absent)
                    {
                        state.store(observed);
                        SetEvent(stop);
                        return;
                    }
                }
            });
        }

        ~GameGuard() { Finish(); }
        void Finish() noexcept
        {
            if (worker.joinable())
            {
                SetEvent(done.value);
                worker.join();
            }
        }
    };

    struct PacketEvidence
    {
        std::uint64_t packets = 0;
        std::uint64_t frames = 0;
        std::uint64_t validTimestampPackets = 0;
        std::uint64_t firstTimestamp = 0;
        std::uint64_t lastTimestamp = 0;
        std::uint64_t nonzeroSamples = 0;
        std::uint64_t nonzeroPackets = 0;
        std::uint64_t absoluteSampleSum = 0;
        std::uint32_t maximumAbsoluteSample = 0;
        std::uint64_t zeroDevicePositions = 0;
        std::uint64_t previousDevicePosition = 0;
        std::uint64_t equalDevicePositions = 0;
        std::uint64_t devicePositionsGoingBackwards = 0;
        std::uint64_t minimumPositiveDeviceStep = UINT64_MAX;
        std::uint64_t maximumPositiveDeviceStep = 0;
        bool allSamplesZero = true;
        bool packetSizesValid = true;
        bool increasingTimestamps = true;
    };

    const char* Json(bool value) noexcept { return value ? "true" : "false"; }

    int Refuse(const char* reason) noexcept
    {
        std::printf("{\"fixture\":\"self_silence\",\"completed\":false,\"reason\":\"%s\","
            "\"audioActivated\":false,\"playbackUsed\":false,\"filesWritten\":false}\n", reason);
        return 2;
    }

    int RunFixture()
    {
        const GameState before = CheckGame();
        if (before != GameState::Absent)
            return Refuse(before == GameState::Running ? "close_game_before_fixture" : "game_state_unavailable");

        Handle process, stop;
        if (!DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(),
            &process.value, PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, 0))
            return Refuse("self_process_handle_failed");
        FILETIME created{}, exited{}, kernel{}, user{};
        if (!GetProcessTimes(process.value, &created, &exited, &kernel, &user))
            return Refuse("self_process_identity_failed");
        stop.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!stop.value) return Refuse("stop_event_failed");

        recorder::audio::Target target;
        target.process = process.value;
        target.processId = GetCurrentProcessId();
        target.creationFileTime = (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
        recorder::audio::Options options;
        options.activationTimeoutMs = 3000;
        options.maximumCaptureMs = 1500;
        recorder::audio::QueueLimits limits;
        limits.maximumQueuedPackets = 512;
        recorder::audio::PacketQueue queue(limits);

        GameGuard guard(stop.value);
        const auto source = recorder::audio::RunProcessLoopback(target, options, stop.value, queue);
        guard.Finish();
        const GameState after = CheckGame();
        const bool gameAbsent = guard.state.load() == GameState::Absent && after == GameState::Absent;

        PacketEvidence samples;
        std::unique_ptr<const recorder::audio::Packet> packet;
        while (queue.TryPop(packet))
        {
            ++samples.packets;
            samples.frames += packet->frames;
            samples.packetSizesValid &= packet->frames > 0 &&
                packet->samples.size() == static_cast<std::size_t>(packet->frames) * recorder::audio::Channels;
            bool nonzeroPacket = false;
            for (const auto sample : packet->samples)
            {
                samples.allSamplesZero &= sample == 0;
                nonzeroPacket |= sample != 0;
                samples.nonzeroSamples += sample != 0 ? 1 : 0;
                const auto magnitude = static_cast<std::uint32_t>(sample < 0 ? -static_cast<int>(sample) : sample);
                samples.absoluteSampleSum += magnitude;
                if (magnitude > samples.maximumAbsoluteSample) samples.maximumAbsoluteSample = magnitude;
            }
            samples.nonzeroPackets += nonzeroPacket ? 1 : 0;
            if (packet->timestampValid)
            {
                samples.zeroDevicePositions += packet->devicePositionFrames == 0 ? 1 : 0;
                if (samples.validTimestampPackets > 0)
                {
                    if (packet->devicePositionFrames == samples.previousDevicePosition) ++samples.equalDevicePositions;
                    else if (packet->devicePositionFrames < samples.previousDevicePosition) ++samples.devicePositionsGoingBackwards;
                    else
                    {
                        const auto step = packet->devicePositionFrames - samples.previousDevicePosition;
                        if (step < samples.minimumPositiveDeviceStep) samples.minimumPositiveDeviceStep = step;
                        if (step > samples.maximumPositiveDeviceStep) samples.maximumPositiveDeviceStep = step;
                    }
                }
                samples.previousDevicePosition = packet->devicePositionFrames;
            }
            if (packet->timestampValid)
            {
                if (samples.validTimestampPackets == 0) samples.firstTimestamp = packet->qpc100ns;
                else samples.increasingTimestamps &= packet->qpc100ns > samples.lastTimestamp;
                samples.lastTimestamp = packet->qpc100ns;
                ++samples.validTimestampPackets;
            }
            packet.reset();
        }
        const auto drained = queue.Snapshot();
        const bool accountingValid = samples.packets == source.queue.acceptedPackets &&
            samples.frames == source.queue.acceptedFrames && drained.queuedPackets == 0 &&
            drained.queuedBytes == 0 && drained.closed;
        const bool activationAndCleanup = source.completed && source.targetValidated &&
            source.activationResultReceived && !source.activationAbandoned &&
            !source.lateActivationCallbackPossible && source.initializedPreferredFormat &&
            source.started && source.stopped && SUCCEEDED(source.hr) &&
            SUCCEEDED(source.stopHr) && SUCCEEDED(source.releaseBufferHr);
        const bool silentDelivery = samples.packets > 0 && samples.allSamplesZero &&
            samples.packetSizesValid && accountingValid;
        const bool timestampSequence = samples.validTimestampPackets > 1 &&
            samples.validTimestampPackets == samples.packets && samples.increasingTimestamps;
        const auto timestampSpan = timestampSequence ? samples.lastTimestamp - samples.firstTimestamp : 0;
        const bool complete = gameAbsent && activationAndCleanup && samples.allSamplesZero &&
            samples.packetSizesValid && accountingValid;
        const char* reason = !gameAbsent ? "game_state_changed" :
            !samples.allSamplesZero ? "unexpected_nonzero_audio" :
            !samples.packetSizesValid || !accountingValid ? "packet_contract_failed" : source.reason;

        // Scalar evidence only: no process identifiers, raw samples or absolute QPC times.
        std::printf("{\"fixture\":\"self_silence\",\"completed\":%s,\"reason\":\"%s\","
            "\"processTreeOnly\":true,\"targetIsSelf\":true,\"gameAbsent\":%s,"
            "\"playbackUsed\":false,\"filesWritten\":false,\"captureLimitMs\":1500,"
            "\"activationResultReceived\":%s,\"activationAbandoned\":%s,"
            "\"lateActivationCallbackPossible\":%s,\"initializedPreferredFormat\":%s,"
            "\"started\":%s,\"stopped\":%s,\"activationAndCleanupVerified\":%s,"
            "\"packetDeliveryObserved\":%s,\"silentPacketDeliveryVerified\":%s,"
            "\"allReturnedSamplesZero\":%s,\"timestampSequenceVerified\":%s,"
            "\"packets\":%llu,\"frames\":%llu,\"silentFlagPackets\":%llu,"
            "\"timestampErrorPackets\":%llu,\"discontinuityPackets\":%llu,"
            "\"nativeDiscontinuityPackets\":%llu,\"nonzeroSamples\":%llu,"
            "\"totalInterleavedSamples\":%llu,\"nonzeroPackets\":%llu,"
            "\"absoluteSampleSum\":%llu,\"maximumAbsoluteSample\":%u,"
            "\"zeroDevicePositions\":%llu,\"equalDevicePositions\":%llu,"
            "\"devicePositionsGoingBackwards\":%llu,\"minimumPositiveDeviceStep\":%llu,"
            "\"maximumPositiveDeviceStep\":%llu,"
            "\"validTimestampPackets\":%llu,\"timestampSpan100ns\":%llu,"
            "\"peakQueuedPcmBytes\":%llu,\"queueDrained\":%s,\"elapsedMs\":%llu,"
            "\"hr\":%ld,\"stopHr\":%ld,\"releaseBufferHr\":%ld}\n",
            Json(complete), reason, Json(gameAbsent), Json(source.activationResultReceived),
            Json(source.activationAbandoned), Json(source.lateActivationCallbackPossible),
            Json(source.initializedPreferredFormat), Json(source.started), Json(source.stopped),
            Json(activationAndCleanup), Json(samples.packets > 0), Json(silentDelivery),
            Json(samples.allSamplesZero), Json(timestampSequence), samples.packets, samples.frames,
            source.queue.silentPackets, source.queue.timestampErrorPackets, source.queue.discontinuityPackets,
            source.queue.nativeDiscontinuityPackets, samples.nonzeroSamples,
            samples.frames * recorder::audio::Channels, samples.nonzeroPackets, samples.absoluteSampleSum,
            samples.maximumAbsoluteSample, samples.zeroDevicePositions, samples.equalDevicePositions,
            samples.devicePositionsGoingBackwards,
            samples.minimumPositiveDeviceStep == UINT64_MAX ? 0ULL : samples.minimumPositiveDeviceStep,
            samples.maximumPositiveDeviceStep,
            samples.validTimestampPackets, timestampSpan, static_cast<unsigned long long>(source.queue.peakQueuedBytes),
            Json(accountingValid), source.elapsedMs, source.hr, source.stopHr, source.releaseBufferHr);
        return complete ? 0 : 1;
    }
}

int wmain(int argc, wchar_t** argv)
{
    // Parsing/help never creates handles, threads, COM or an audio session.
    if (argc == 1 || (argc == 2 && std::wcscmp(argv[1], L"--help") == 0))
    {
        std::puts("Usage: AudioSilenceFixture --self-silence-fixture\n"
            "Checks only its own non-rendering process for 1500ms; refuses while Forza runs.\n"
            "No playback or file output. This does not validate game audio or active-audio exclusion.");
        return 0;
    }
    if (argc != 2 || std::wcscmp(argv[1], L"--self-silence-fixture") != 0)
        return Refuse("invalid_arguments");
    try { return RunFixture(); }
    catch (...)
    {
        std::puts("{\"fixture\":\"self_silence\",\"completed\":false,"
            "\"reason\":\"fixture_unexpected_failure\",\"activationStatusKnown\":false,"
            "\"playbackUsed\":false,\"filesWritten\":false}");
        return 2;
    }
}
