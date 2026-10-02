#include "ProcessAudioCapture.h"

#include <mmdeviceapi.h>
#include <propidl.h>
#include <wrl/implements.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <limits>
#include <new>
#include <optional>
#include <stdexcept>
#include <utility>

namespace recorder::audio
{
    using Microsoft::WRL::ComPtr;
    using Clock = std::chrono::steady_clock;

    WAVEFORMATEX PreferredFormat() noexcept
    {
        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_PCM;
        format.nChannels = Channels;
        format.nSamplesPerSec = SampleRate;
        format.wBitsPerSample = BitsPerSample;
        format.nBlockAlign = FrameBytes;
        format.nAvgBytesPerSec = SampleRate * FrameBytes;
        return format;
    }

    bool IsPreferredFormat(const WAVEFORMATEX& format) noexcept
    {
        return format.wFormatTag == WAVE_FORMAT_PCM && format.nChannels == Channels &&
            format.nSamplesPerSec == SampleRate && format.wBitsPerSample == BitsPerSample &&
            format.nBlockAlign == FrameBytes && format.nAvgBytesPerSec == SampleRate * FrameBytes &&
            format.cbSize == 0;
    }

    bool ValidateOptions(const Options& options) noexcept
    {
        if (options.activationTimeoutMs < 1 || options.activationTimeoutMs > 30000) return false;
        if (options.source != LoopbackSource::GameProcess && options.source != LoopbackSource::SystemPlayback) return false;
        switch (options.mode)
        {
        case CaptureMode::TimedFixture:
            return options.maximumCaptureMs >= 1 && options.maximumCaptureMs <= 60000;
        case CaptureMode::UntilStopped:
            return options.maximumCaptureMs == 0;
        default: return false;
        }
    }

    bool detail::ConfigureLoopbackSource(LoopbackSource source, DWORD gameProcessId,
        AUDIOCLIENT_ACTIVATION_PARAMS& parameters) noexcept
    {
        parameters = {};
        if (gameProcessId == 0) return false;
        AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS selection{};
        switch (source)
        {
        case LoopbackSource::GameProcess:
            selection.TargetProcessId = gameProcessId;
            selection.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
            break;
        case LoopbackSource::SystemPlayback:
            selection.TargetProcessId = GetCurrentProcessId();
            selection.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;
            break;
        default: return false;
        }
        parameters.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        parameters.ProcessLoopbackParams = selection;
        return true;
    }

    bool detail::CountersCanAdvance(const QueueSnapshot& stats, std::uint32_t frames,
        DWORD flags, bool discontinuity) noexcept
    {
        constexpr auto maximum = (std::numeric_limits<std::uint64_t>::max)();
        return stats.acceptedPackets < maximum && stats.acceptedFrames <= maximum - frames &&
            (!(flags & AUDCLNT_BUFFERFLAGS_SILENT) || stats.silentPackets < maximum) &&
            (!discontinuity || stats.discontinuityPackets < maximum) &&
            (!(flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) || stats.nativeDiscontinuityPackets < maximum) &&
            (!(flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) || stats.timestampErrorPackets < maximum);
    }

    PacketQueue::PacketQueue(QueueLimits limits) : limits_(limits) {}

    bool PacketQueue::ValidLimits() const noexcept
    {
        return limits_.maximumPacketFrames > 0 && limits_.maximumPacketFrames <= SampleRate &&
            limits_.maximumQueuedPackets > 0 && limits_.maximumQueuedPackets <= 4096 &&
            limits_.maximumQueuedBytes >= FrameBytes && limits_.maximumQueuedBytes <= 16 * 1024 * 1024;
    }

    bool PacketQueue::BeginSource()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!ValidLimits() || sourceClaimed_ || stats_.closed || stats_.acceptedPackets != 0) return false;
        sourceClaimed_ = true;
        return true;
    }

    QueueResult PacketQueue::Push(const BYTE* data, std::uint32_t frames, DWORD flags,
        std::uint64_t devicePositionFrames, std::uint64_t qpc100ns)
    {
        if (!ValidLimits()) return QueueResult::InvalidLimits;
        constexpr DWORD knownFlags = AUDCLNT_BUFFERFLAGS_SILENT | AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY |
            AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR;
        if (frames == 0 || (flags & ~knownFlags) != 0) return QueueResult::InvalidPacket;
        if (frames > limits_.maximumPacketFrames) return QueueResult::PacketLimit;
        const bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
        const bool timestampError = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0;
        if (!silent && !data) return QueueResult::InvalidPacket;
        const auto durationCeiling100ns = (static_cast<std::uint64_t>(frames) * 10000000 + SampleRate - 1) / SampleRate;
        if (!timestampError && (qpc100ns == 0 ||
            qpc100ns > static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)()) - durationCeiling100ns ||
            devicePositionFrames > (std::numeric_limits<std::uint64_t>::max)() - frames))
            return QueueResult::InvalidTimestamp;
        const auto bytes = static_cast<std::size_t>(frames) * FrameBytes;
        std::lock_guard<std::mutex> lock(mutex_);
        if (stats_.closed) return QueueResult::Closed;
        if (queue_.size() >= limits_.maximumQueuedPackets || bytes > limits_.maximumQueuedBytes ||
            stats_.queuedBytes > limits_.maximumQueuedBytes - bytes) return QueueResult::QueueLimit;
        if (!timestampError && haveValidTime_ && qpc100ns <= previousQpc_) return QueueResult::InvalidTimestamp;
        const bool discontinuity = (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0 ||
            timestampError || previousTimestampError_;
        if (!detail::CountersCanAdvance(stats_, frames, flags, discontinuity)) return QueueResult::CounterLimit;
        try
        {
            auto packet = std::make_unique<Packet>();
            packet->frames = frames;
            packet->samples.resize(static_cast<std::size_t>(frames) * Channels, 0);
            if (!silent) std::memcpy(packet->samples.data(), data, bytes);
            packet->silent = silent;
            packet->timestampError = timestampError;
            packet->timestampValid = !timestampError;
            packet->qpc100ns = timestampError ? 0 : qpc100ns;
            packet->devicePositionFrames = timestampError ? 0 : devicePositionFrames;
            packet->discontinuity = discontinuity;
            // Process loopback can return zero device positions for every
            // packet. Preserve them as observations, never infer lost audio
            // from that counter. The media timeline uses original QPC times.
            queue_.push_back(std::move(packet));
            stats_.queuedPackets = queue_.size();
            stats_.queuedBytes += bytes;
            stats_.peakQueuedBytes = (std::max)(stats_.peakQueuedBytes, stats_.queuedBytes);
            ++stats_.acceptedPackets;
            stats_.acceptedFrames += frames;
            stats_.silentPackets += silent ? 1 : 0;
            stats_.discontinuityPackets += discontinuity ? 1 : 0;
            stats_.nativeDiscontinuityPackets += (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0 ? 1 : 0;
            stats_.timestampErrorPackets += timestampError ? 1 : 0;
            previousTimestampError_ = timestampError;
            haveValidTime_ = !timestampError;
            if (!timestampError)
            {
                previousQpc_ = qpc100ns;
            }
            return QueueResult::Accepted;
        }
        catch (const std::bad_alloc&) { return QueueResult::AllocationFailed; }
        catch (const std::length_error&) { return QueueResult::AllocationFailed; }
    }

    bool PacketQueue::TryPop(std::unique_ptr<const Packet>& packet)
    {
        std::unique_ptr<const Packet> next;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (queue_.empty()) return false;
            next = std::move(queue_.front());
            queue_.pop_front();
            stats_.queuedBytes -= next->samples.size() * sizeof(std::int16_t);
            stats_.queuedPackets = queue_.size();
        }
        packet = std::move(next);
        return true;
    }

    void PacketQueue::Close()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        stats_.closed = true;
    }

    QueueSnapshot PacketQueue::Snapshot() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return stats_;
    }

    bool detail::ActivationMailbox::Publish(HRESULT hr, ComPtr<IUnknown> result)
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (completed_) return false;
        completed_ = true;
        if (abandoned_) return false;
        hr_ = hr;
        result_ = std::move(result);
        return true;
    }

    bool detail::ActivationMailbox::Take(HRESULT& hr, ComPtr<IUnknown>& result)
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!completed_ || abandoned_ || taken_) return false;
        taken_ = true;
        hr = hr_;
        result = std::move(result_);
        return true;
    }

    void detail::ActivationMailbox::Abandon()
    {
        ComPtr<IUnknown> discarded;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            abandoned_ = true;
            discarded = std::move(result_);
        }
    }

    bool detail::ActivationMailbox::Completed() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return completed_;
    }

    namespace
    {
        std::atomic<bool> activationPending{ false };
        struct Failure { const char* reason; HRESULT hr; };
        struct Cancelled {};
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        void Require(bool condition, const char* reason) { if (!condition) throw Failure{ reason, E_INVALIDARG }; }

        struct Handle
        {
            HANDLE value = nullptr;
            HRESULT* closeHr = nullptr;
            explicit Handle(HRESULT* result = nullptr) noexcept : closeHr(result) {}
            ~Handle() { Close(); }
            void Close() noexcept
            {
                if (!value) return;
                if (!CloseHandle(value) && closeHr && SUCCEEDED(*closeHr))
                    *closeHr = HRESULT_FROM_WIN32(GetLastError());
                value = nullptr;
            }
            Handle(const Handle&) = delete;
            Handle& operator=(const Handle&) = delete;
        };

        void Duplicate(HANDLE source, Handle& destination)
        {
            Require(source && source != INVALID_HANDLE_VALUE, "invalid_source_handle");
            if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), &destination.value,
                0, FALSE, DUPLICATE_SAME_ACCESS))
                throw Failure{ "handle_duplication_failed", HRESULT_FROM_WIN32(GetLastError()) };
        }

        std::uint64_t FileTimeValue(FILETIME value) noexcept
        {
            return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime;
        }

        void ValidateTarget(HANDLE process, const Target& target)
        {
            Require(target.processId != 0 && target.creationFileTime != 0 &&
                GetProcessId(process) == target.processId, "target_identity_mismatch");
            const DWORD wait = WaitForSingleObject(process, 0);
            if (wait == WAIT_FAILED) throw Failure{ "target_liveness_failed", HRESULT_FROM_WIN32(GetLastError()) };
            Require(wait == WAIT_TIMEOUT, "target_exited");
            FILETIME creation{}, exit{}, kernel{}, user{};
            if (!GetProcessTimes(process, &creation, &exit, &kernel, &user))
                throw Failure{ "target_creation_query_failed", HRESULT_FROM_WIN32(GetLastError()) };
            Require(FileTimeValue(creation) == target.creationFileTime, "target_creation_changed");
        }

        void CheckStop(HANDLE stop)
        {
            const DWORD wait = WaitForSingleObject(stop, 0);
            if (wait == WAIT_OBJECT_0) throw Cancelled{};
            if (wait == WAIT_FAILED) throw Failure{ "stop_event_wait_failed", HRESULT_FROM_WIN32(GetLastError()) };
            Require(wait == WAIT_TIMEOUT, "stop_event_wait_unexpected");
        }

        struct ActivationState
        {
            detail::ActivationMailbox mailbox;
            Handle ready;
            Handle targetProcess; // Pins PID identity until a late callback releases state.
            AUDIOCLIENT_ACTIVATION_PARAMS parameters{};
            PROPVARIANT variant{};
            std::atomic<bool> resultHandled{ false };
            bool ownsPendingSlot = false;
            ~ActivationState() { if (ownsPendingSlot) activationPending.store(false); }
        };

        class ActivationCallback final : public Microsoft::WRL::RuntimeClass<
            Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
            IActivateAudioInterfaceCompletionHandler, Microsoft::WRL::FtmBase>
        {
        public:
            explicit ActivationCallback(std::shared_ptr<ActivationState> state) : state_(std::move(state)) {}
            STDMETHODIMP ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override
            {
                // Windows owns this agile callback through completion. State,
                // activation parameters and event remain alive after timeout;
                // no caller stack, queue or audio-source object is referenced.
                HRESULT activationHr = E_UNEXPECTED;
                ComPtr<IUnknown> activated;
                HRESULT hr = operation ? operation->GetActivateResult(&activationHr, &activated) : E_POINTER;
                if (SUCCEEDED(hr)) hr = activationHr;
                if (SUCCEEDED(hr) && !activated) hr = E_NOINTERFACE;
                try
                {
                    (void)state_->mailbox.Publish(hr, std::move(activated));
                    state_->resultHandled.store(true);
                    if (!SetEvent(state_->ready.value)) return HRESULT_FROM_WIN32(GetLastError());
                    return S_OK;
                }
                catch (...)
                {
                    state_->resultHandled.store(true);
                    (void)SetEvent(state_->ready.value);
                    return E_UNEXPECTED;
                }
            }
        private:
            std::shared_ptr<ActivationState> state_;
        };

        void Activate(HANDLE process, const Target& target, LoopbackSource source, HANDLE stop, DWORD timeoutMs,
            ComPtr<IAudioClient>& client, Evidence& evidence)
        {
            auto state = std::make_shared<ActivationState>();
            bool expected = false;
            Require(activationPending.compare_exchange_strong(expected, true), "prior_activation_still_pending");
            state->ownsPendingSlot = true;
            Duplicate(process, state->targetProcess);
            state->ready.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!state->ready.value) throw Failure{ "activation_event_failed", HRESULT_FROM_WIN32(GetLastError()) };
            Require(detail::ConfigureLoopbackSource(source, target.processId, state->parameters), "audio_source_invalid");
            state->variant.vt = VT_BLOB;
            // The blob is borrowed from callback-owned state, not CoTaskMem.
            // Keep it alive through late completion; do not PropVariantClear it.
            state->variant.blob.cbSize = static_cast<ULONG>(sizeof(state->parameters));
            state->variant.blob.pBlobData = reinterpret_cast<BYTE*>(&state->parameters);
            auto callback = Microsoft::WRL::Make<ActivationCallback>(state);
            Require(callback != nullptr, "activation_callback_allocation_failed");
            ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
            const HRESULT requestHr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                __uuidof(IAudioClient), &state->variant, callback.Get(), &operation);
            if (FAILED(requestHr))
            {
                state->mailbox.Abandon();
                throw Failure{ "process_audio_activation_failed", requestHr };
            }
            try
            {
                const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
                const HANDLE handles[]{ stop, process, state->ready.value };
                for (;;)
                {
                    CheckStop(stop);
                    ValidateTarget(process, target);
                    const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now()).count();
                    if (remaining <= 0) throw Failure{ "audio_activation_timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT) };
                    const DWORD wait = WaitForMultipleObjects(3, handles, FALSE,
                        remaining > 100 ? 100 : static_cast<DWORD>(remaining));
                    if (wait == WAIT_OBJECT_0) throw Cancelled{};
                    if (wait == WAIT_OBJECT_0 + 1) throw Failure{ "target_exited", HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED) };
                    if (wait == WAIT_FAILED) throw Failure{ "activation_wait_failed", HRESULT_FROM_WIN32(GetLastError()) };
                    if (wait != WAIT_OBJECT_0 + 2) continue;
                    HRESULT activationHr = E_PENDING;
                    ComPtr<IUnknown> activated;
                    Require(state->mailbox.Take(activationHr, activated), "activation_result_unavailable");
                    evidence.activationResultReceived = true;
                    Check(activationHr, "process_audio_activation_result_failed");
                    Check(activated.As(&client), "activated_audio_interface_missing");
                    ValidateTarget(process, target);
                    CheckStop(stop);
                    return;
                }
            }
            catch (...)
            {
                state->mailbox.Abandon();
                evidence.activationAbandoned = true;
                evidence.lateActivationCallbackPossible = !state->resultHandled.load();
                throw;
            }
        }

        struct AudioResources
        {
            explicit AudioResources(Evidence& output) : evidence(output), ready(&output.handleCloseHr)
            { evidence.resourcesReleased = false; }
            Evidence& evidence;
            Handle ready;
            ComPtr<IAudioClient> client;
            ComPtr<IAudioCaptureClient> capture;
            bool started = false;
            ~AudioResources()
            {
                if (started)
                {
                    evidence.stopHr = client->Stop();
                    evidence.stopped = SUCCEEDED(evidence.stopHr);
                }
                capture.Reset();
                client.Reset(); // Event stays alive until both interfaces release.
                ready.Close();
                evidence.resourcesReleased = true;
            }
        };

        struct BufferLease
        {
            IAudioCaptureClient* capture;
            UINT32 frames;
            HRESULT& result;
            bool active = true;
            void Release() noexcept
            {
                if (!active) return;
                active = false;
                result = capture->ReleaseBuffer(frames);
            }
            ~BufferLease() { Release(); }
        };

        const char* QueueReason(QueueResult result) noexcept
        {
            switch (result)
            {
            case QueueResult::InvalidLimits: return "audio_queue_limits_invalid";
            case QueueResult::Closed: return "audio_queue_closed";
            case QueueResult::InvalidPacket: return "audio_packet_invalid";
            case QueueResult::PacketLimit: return "audio_packet_limit";
            case QueueResult::QueueLimit: return "audio_queue_limit";
            case QueueResult::InvalidTimestamp: return "audio_timestamp_invalid";
            case QueueResult::AllocationFailed: return "audio_packet_allocation_failed";
            case QueueResult::CounterLimit: return "audio_counter_limit";
            default: return "audio_queue_unexpected_result";
            }
        }

        void CheckForeground(HWND window, DWORD processId)
        {
            if (!window) return;
            DWORD actual = 0;
            Require(GetWindowThreadProcessId(window, &actual) != 0 && actual == processId &&
                GetForegroundWindow() == window && IsIconic(window) == FALSE, "audio_focus_lost");
        }
        void Drain(AudioResources& audio, HANDLE process, const Target& target, HANDLE stop,
            PacketQueue& queue, const std::optional<Clock::time_point>& deadline, HWND foreground)
        {
            // Finite work per notification; cancellation is checked per packet.
            // A pathological continuously replenished backlog fails explicitly.
            for (unsigned packets = 0; packets < 256; ++packets)
            {
                CheckStop(stop);
                ValidateTarget(process, target);
                CheckForeground(foreground, target.processId);
                if (deadline && Clock::now() >= *deadline) return;
                UINT32 pending = 0;
                Check(audio.capture->GetNextPacketSize(&pending), "audio_next_packet_failed");
                if (pending == 0) return;
                Require(pending <= queue.MaximumPacketFrames(), "audio_packet_limit");
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                UINT64 devicePosition = 0, qpc100ns = 0;
                const HRESULT hr = audio.capture->GetBuffer(&data, &frames, &flags, &devicePosition, &qpc100ns);
                if (hr == AUDCLNT_S_BUFFER_EMPTY) return;
                Check(hr, "audio_get_buffer_failed");
                Require(frames != 0, "audio_empty_success_packet");
                BufferLease lease{ audio.capture.Get(), frames, audio.evidence.releaseBufferHr };
                CheckForeground(foreground, target.processId);
                const QueueResult queued = queue.Push(data, frames, flags, devicePosition, qpc100ns);
                lease.Release();
                Check(audio.evidence.releaseBufferHr, "audio_release_buffer_failed");
                if (queued != QueueResult::Accepted) throw Failure{ QueueReason(queued), E_FAIL };
            }
            UINT32 pending = 0;
            Check(audio.capture->GetNextPacketSize(&pending), "audio_next_packet_failed");
            Require(pending == 0, "audio_drain_limit");
        }
    }

    Evidence RunProcessLoopback(const Target& target, const Options& options,
        HANDLE stopEvent, PacketQueue& queue) noexcept
    {
        Evidence evidence;
        const auto began = Clock::now();
        bool initializedCom = false;
        bool claimedQueue = false;
        // Failure before AudioResources exists owns no audio interfaces. Its
        // constructor/destructor bracket the acquired-resource interval.
        evidence.resourcesReleased = true;
        try
        {
            Require(ValidateOptions(options), "audio_options_invalid");
            evidence.processTreeOnly = options.source == LoopbackSource::GameProcess;
            evidence.continuous = options.mode == CaptureMode::UntilStopped;
            Require(queue.BeginSource(), "audio_queue_not_fresh");
            claimedQueue = true;
            Handle process(&evidence.handleCloseHr), stop(&evidence.handleCloseHr);
            Duplicate(target.process, process);
            Duplicate(stopEvent, stop);
            ValidateTarget(process.value, target);
            evidence.targetValidated = true;
            CheckStop(stop.value);
            Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "audio_worker_mta_required");
            initializedCom = true;
            {
                AudioResources audio(evidence);
                Activate(process.value, target, options.source, stop.value, options.activationTimeoutMs, audio.client, evidence);
                audio.ready.value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
                if (!audio.ready.value) throw Failure{ "audio_ready_event_failed", HRESULT_FROM_WIN32(GetLastError()) };
                const WAVEFORMATEX format = PreferredFormat();
                Require(IsPreferredFormat(format), "audio_format_invalid");
                // Explicit requested stream format; Initialize must accept it.
                // AUTOCONVERTPCM does not select another process or endpoint.
                Check(audio.client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                    0, 0, &format, nullptr), "audio_pcm16_48000_initialize_failed");
                evidence.initializedPreferredFormat = true;
                Check(audio.client->SetEventHandle(audio.ready.value), "audio_event_registration_failed");
                UINT32 bufferFrames = 0;
                Check(audio.client->GetBufferSize(&bufferFrames), "audio_buffer_size_failed");
                Require(bufferFrames > 0 && bufferFrames <= SampleRate, "audio_endpoint_buffer_limit");
                Check(audio.client->GetService(IID_PPV_ARGS(&audio.capture)), "audio_capture_service_failed");
                ValidateTarget(process.value, target);
                CheckForeground(options.requiredForegroundWindow, target.processId);
                CheckStop(stop.value);
                Check(audio.client->Start(), "audio_start_failed");
                audio.started = true;
                evidence.started = true;
                std::optional<Clock::time_point> deadline;
                if (!evidence.continuous)
                    deadline = Clock::now() + std::chrono::milliseconds(options.maximumCaptureMs);
                const HANDLE handles[]{ stop.value, process.value, audio.ready.value };
                for (;;)
                {
                    CheckStop(stop.value);
                    ValidateTarget(process.value, target);
                    CheckForeground(options.requiredForegroundWindow, target.processId);
                    DWORD waitMs = 100;
                    if (deadline)
                    {
                        const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(*deadline - Clock::now()).count();
                        if (remaining <= 0)
                        {
                            evidence.reason = "duration_complete";
                            evidence.completed = true;
                            break;
                        }
                        if (remaining < 100) waitMs = static_cast<DWORD>(remaining);
                    }
                    const DWORD wait = WaitForMultipleObjects(3, handles, FALSE, waitMs);
                    if (wait == WAIT_OBJECT_0) throw Cancelled{};
                    if (wait == WAIT_OBJECT_0 + 1) throw Failure{ "target_exited", HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED) };
                    if (wait == WAIT_FAILED) throw Failure{ "audio_wait_failed", HRESULT_FROM_WIN32(GetLastError()) };
                    if (wait == WAIT_OBJECT_0 + 2)
                        Drain(audio, process.value, target, stop.value, queue, deadline, options.requiredForegroundWindow);
                }
            }
        }
        catch (const Cancelled&) { evidence.reason = "cancelled"; evidence.completed = true; }
        catch (const Failure& error) { evidence.reason = error.reason; evidence.hr = error.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "audio_allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "audio_unexpected_failure"; evidence.hr = E_UNEXPECTED; }
        if (initializedCom) CoUninitialize();
        if (claimedQueue) queue.Close();
        evidence.queue = queue.Snapshot();
        if (evidence.completed && (FAILED(evidence.stopHr) || FAILED(evidence.releaseBufferHr) || FAILED(evidence.handleCloseHr)))
        {
            evidence.completed = false;
            evidence.reason = "audio_cleanup_failed";
            evidence.hr = FAILED(evidence.stopHr) ? evidence.stopHr :
                (FAILED(evidence.releaseBufferHr) ? evidence.releaseBufferHr : evidence.handleCloseHr);
        }
        evidence.elapsedMs = static_cast<std::uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - began).count());
        return evidence;
    }
}
