#pragma once

#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <wrl/client.h>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <vector>

namespace recorder::audio
{
    constexpr std::uint32_t SampleRate = 48000;
    constexpr std::uint16_t Channels = 2;
    constexpr std::uint16_t BitsPerSample = 16;
    constexpr std::uint16_t FrameBytes = 4;

    WAVEFORMATEX PreferredFormat() noexcept;
    bool IsPreferredFormat(const WAVEFORMATEX& format) noexcept;

    struct QueueLimits
    {
        std::uint32_t maximumPacketFrames = 4800;
        std::size_t maximumQueuedPackets = 128;
        std::size_t maximumQueuedBytes = 384000;
    };

    enum class QueueResult
    {
        Accepted, InvalidLimits, Closed, InvalidPacket, PacketLimit,
        QueueLimit, InvalidTimestamp, AllocationFailed, CounterLimit
    };

    struct Packet
    {
        std::vector<std::int16_t> samples;
        std::uint32_t frames = 0;
        std::uint64_t devicePositionFrames = 0;
        std::uint64_t qpc100ns = 0;
        bool timestampValid = false;
        bool timestampError = false;
        bool silent = false;
        bool discontinuity = false;
    };

    struct QueueSnapshot
    {
        std::size_t queuedPackets = 0;
        std::size_t queuedBytes = 0;
        std::size_t peakQueuedBytes = 0;
        std::uint64_t acceptedPackets = 0;
        std::uint64_t acceptedFrames = 0;
        std::uint64_t silentPackets = 0;
        std::uint64_t discontinuityPackets = 0;
        std::uint64_t nativeDiscontinuityPackets = 0;
        std::uint64_t timestampErrorPackets = 0;
        bool closed = false;
    };

    // One producer/session, concurrent consumer. Rejects instead of replacing
    // queued audio. Bounds cover PCM bytes still IN THE QUEUE, not popped
    // consumer packets or allocator/container overhead. Consumers must bound
    // their own retention. Samples are copied and immutable after publication.
    class PacketQueue final
    {
    public:
        explicit PacketQueue(QueueLimits limits = {});
        bool ValidLimits() const noexcept;
        bool BeginSource();
        QueueResult Push(const BYTE* data, std::uint32_t frames, DWORD flags,
            std::uint64_t devicePositionFrames, std::uint64_t qpc100ns);
        bool TryPop(std::unique_ptr<const Packet>& packet);
        void Close();
        QueueSnapshot Snapshot() const;
        std::uint32_t MaximumPacketFrames() const noexcept { return limits_.maximumPacketFrames; }
    private:
        QueueLimits limits_;
        mutable std::mutex mutex_;
        std::deque<std::unique_ptr<const Packet>> queue_;
        QueueSnapshot stats_;
        bool sourceClaimed_ = false;
        bool haveValidTime_ = false;
        bool previousTimestampError_ = false;
        std::uint64_t previousQpc_ = 0;
    };

    struct Target
    {
        // Caller verifies this is the intended game before entering the source.
        // Requires QUERY_LIMITED_INFORMATION and SYNCHRONIZE access. This source
        // duplicates the handle and rechecks PID/creation time in both modes.
        // System playback still stops when this game process exits.
        HANDLE process = nullptr;
        DWORD processId = 0;
        std::uint64_t creationFileTime = 0;
    };

    enum class CaptureMode { TimedFixture, UntilStopped };
    enum class LoopbackSource { GameProcess, SystemPlayback };

    struct Options
    {
        DWORD activationTimeoutMs = 3000;
        DWORD maximumCaptureMs = 10000; // Timed fixture: 1..60000; UntilStopped requires 0.
        CaptureMode mode = CaptureMode::TimedFixture;
        LoopbackSource source = LoopbackSource::GameProcess;
        HWND requiredForegroundWindow = nullptr; // Production monitor capture only; fixtures omit it.
    };
    bool ValidateOptions(const Options& options) noexcept;

    struct Evidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK;
        HRESULT stopHr = S_OK;
        HRESULT releaseBufferHr = S_OK;
        HRESULT handleCloseHr = S_OK;
        bool resourcesReleased = false;
        bool completed = false;
        bool targetValidated = false;
        bool activationResultReceived = false;
        bool activationAbandoned = false;
        bool lateActivationCallbackPossible = false;
        bool initializedPreferredFormat = false;
        bool started = false;
        bool stopped = false;
        bool processTreeOnly = true;
        bool playbackUsed = false;
        bool continuous = false;
        std::uint64_t elapsedMs = 0;
        QueueSnapshot queue;
    };

    // Blocking source for a caller-owned ORDINARY worker. Initializes its MTA;
    // no MMCSS, priority, microphone, render/playback or disk path. The default
    // captures only the game process tree; SystemPlayback captures rendered
    // audio from all processes except this recorder and its children.
    // Caller keeps inputs valid until entry duplicates handles. Stop event must
    // be a manual-reset event. A fresh queue is required for each invocation.
    // UntilStopped retains the same queue and per-notification bounds. The
    // caller must consume queued PCM and signal stop; saving a clip is not stop.
    // Timestamp-error packets have timestampValid=false, qpc100ns=0 and an
    // explicit discontinuity: consumers must not mux them as synchronized audio.
    Evidence RunProcessLoopback(const Target& target, const Options& options,
        HANDLE stopEvent, PacketQueue& queue) noexcept;

    namespace detail
    {
        // Production selection shared with CPU contracts. The system mode
        // excludes this silent recorder, never the validated game process.
        bool ConfigureLoopbackSource(LoopbackSource source, DWORD gameProcessId,
            AUDIOCLIENT_ACTIVATION_PARAMS& parameters) noexcept;

        bool CountersCanAdvance(const QueueSnapshot& stats, std::uint32_t frames,
            DWORD flags, bool discontinuity) noexcept;

        // The same handoff is exercised by CPU contracts using a fake IUnknown.
        // No COM activation or audio device is needed to test abandonment.
        class ActivationMailbox final
        {
        public:
            bool Publish(HRESULT hr, Microsoft::WRL::ComPtr<IUnknown> result);
            bool Take(HRESULT& hr, Microsoft::WRL::ComPtr<IUnknown>& result);
            void Abandon();
            bool Completed() const;
        private:
            mutable std::mutex mutex_;
            bool completed_ = false;
            bool abandoned_ = false;
            bool taken_ = false;
            HRESULT hr_ = E_PENDING;
            Microsoft::WRL::ComPtr<IUnknown> result_;
        };
    }
}
