#include "HardwareVideoSession.h"
#include "HardwareEncoderInternal.h"

#include <mfapi.h>
#include <mferror.h>
#include <chrono>
#include <limits>
#include <new>

namespace recorder::encoder
{
    using Microsoft::WRL::ComPtr;
    using Clock = std::chrono::steady_clock;
    using namespace detail;

    struct HardwareVideoSession::Impl
    {
        HardwareSession session;
        EncodeConfig configuration{};
        LiveOptions options{};
        DWORD ownerThread = 0;
        const std::atomic<bool>* cancelled = nullptr;
        PacketObserver* observer = nullptr;
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D10Multithread> multithread;
        ComPtr<IMFMediaEventGenerator> generator;
        ComPtr<Callback> eventCallback;
        std::shared_ptr<SharedState> state;
        UINT inputCredits = 0;
        GopTracker gop;
        bool draining = false;
        Clock::time_point started = Clock::now();
    };

    bool ValidateCfrTime(const EncodeConfig& config, LONGLONG epoch, UINT index,
        LONGLONG time, LONGLONG& duration) noexcept
    {
        if (epoch < 0 || (config.frameRate != 30 && config.frameRate != 60) ||
            index == (std::numeric_limits<UINT>::max)()) return false;
        const LONGLONG relative = FrameTime(index, config.frameRate);
        const LONGLONG end = FrameTime(index + 1, config.frameRate);
        if (epoch > (std::numeric_limits<LONGLONG>::max)() - end || time != epoch + relative) return false;
        duration = end - relative;
        return duration > 0;
    }

    UINT RunLiveContractTests() noexcept
    {
        UINT count = 0;
        bool passed = true;
        const auto test = [&](bool value) { ++count; passed = passed && value; };
        EncodeConfig config;
        LONGLONG duration = 0;
        test(ValidateCfrTime(config, 0, 0, 0, duration) && duration == 333333);
        test(ValidateCfrTime(config, 9000000, 2, 9666666, duration) && duration == 333334);
        test(!ValidateCfrTime(config, 0, 1, 333334, duration));
        test(!ValidateCfrTime(config, -1, 0, -1, duration));
        test(!ValidateCfrTime(config, (std::numeric_limits<LONGLONG>::max)(), 0,
            (std::numeric_limits<LONGLONG>::max)(), duration));
        test(!ValidateCfrTime(config, 0, (std::numeric_limits<UINT>::max)(), 0, duration));
        config.frameRate = 60;
        test(ValidateCfrTime(config, 12000000, 1, 12166666, duration) && duration == 166667);
        test(ValidateCfrTime(config, 0, 36000, 6000000000ll, duration));
        config.frameRate = 59;
        test(!ValidateCfrTime(config, 0, 0, 0, duration));
        config.frameRate = 0;
        test(!ValidateCfrTime(config, 0, 0, 0, duration));
        HardwareVideoSession inactive;
        test(!inactive.CanAcceptInput() && !inactive.Result().activatedHardwareAttribute);
        test(SUCCEEDED(inactive.Close()) && SUCCEEDED(inactive.Close()));
        HardwareVideoSession uninitialized;
        test(!uninitialized.Pump(0) && !uninitialized.Result().activatedHardwareAttribute);
        Evidence fixtureDefaults;
        test(!fixtureDefaults.gopControlSupported && !fixtureDefaults.gopSizeReadback &&
            fixtureDefaults.requestedGopFrames == 0 && fixtureDefaults.negotiatedGopFrames == 0);
        struct CodecResult { HRESULT observed, expectedFailure; };
        for (const auto item : { CodecResult{S_OK, S_OK}, CodecResult{S_FALSE, E_FAIL},
            CodecResult{E_NOTIMPL, E_NOTIMPL}, CodecResult{E_ACCESSDENIED, E_ACCESSDENIED} })
        {
            HRESULT failure = S_OK;
            try { CheckCodecSuccess(item.observed, "contract_codec_result"); }
            catch (const Failure& error) { failure = error.hr; }
            test(failure == item.expectedFailure);
        }
        VARIANT observed{};
        for (const auto item : { CodecResult{S_OK, S_OK}, CodecResult{E_NOTIMPL, S_OK},
            CodecResult{S_FALSE, E_FAIL}, CodecResult{E_ACCESSDENIED, E_ACCESSDENIED} })
        {
            HRESULT failure = S_OK;
            try { CheckGopModifiability(item.observed); }
            catch (const Failure& error) { failure = error.hr; }
            test(failure == item.expectedFailure);
        }
        observed.vt = VT_UI4; observed.ulVal = 60;
        test(GopReadbackMatches(60, observed) && !GopReadbackMatches(120, observed));
        observed.ulVal = 120;
        test(GopReadbackMatches(120, observed));
        observed.ulVal = 0;
        test(!GopReadbackMatches(120, observed) && !GopReadbackMatches(0, observed));
        observed.vt = VT_I4; observed.lVal = 120;
        test(!GopReadbackMatches(120, observed));
        observed.vt = VT_EMPTY;
        test(!GopReadbackMatches(120, observed));
        for (const UINT maximum : { 60u, 120u })
        {
            GopTracker tracker;
            tracker.maximumFrames = maximum;
            Evidence measured;
            bool sequenceOkay = true;
            for (UINT i = 0; i <= 2 * maximum; ++i)
                if (tracker.Observe(i, i % maximum == 0, measured)) sequenceOkay = false;
            test(sequenceOkay && measured.observedGopIntervals == 2 &&
                measured.maximumObservedGopFrames == maximum && measured.trailingGopFrames == 1);
        }
        {
            GopTracker tracker;
            tracker.maximumFrames = 60;
            Evidence measured;
            test(tracker.Observe(0, false, measured) != nullptr && tracker.nextIndex == 0);
            test(!tracker.Observe(0, true, measured));
            test(tracker.Observe(0, true, measured) != nullptr && tracker.nextIndex == 1);
            test(tracker.Observe(2, false, measured) != nullptr && tracker.nextIndex == 1);
            bool sequenceOkay = true;
            for (UINT i = 1; i < 60; ++i)
                if (tracker.Observe(i, false, measured)) sequenceOkay = false;
            test(sequenceOkay && measured.trailingGopFrames == 60);
            test(tracker.Observe(60, false, measured) != nullptr && tracker.nextIndex == 60);
            test(!tracker.Observe(60, true, measured) && measured.observedGopIntervals == 1);
        }
        {
            GopTracker tracker;
            tracker.maximumFrames = 120;
            Evidence measured;
            bool sequenceOkay = true;
            for (UINT i = 0; i <= 119; ++i)
                if (tracker.Observe(i, i % 17 == 0, measured)) sequenceOkay = false;
            test(sequenceOkay && measured.observedGopIntervals == 7 && measured.maximumObservedGopFrames == 17);
            tracker.nextIndex = 240; // Inject an already overlong gap without a frame loop.
            test(tracker.Observe(240, true, measured) != nullptr && tracker.lastCleanIndex == 119);
            tracker.nextIndex = UINT_MAX;
            test(tracker.Observe(UINT_MAX, true, measured) != nullptr);
        }
        {
            GopTracker tracker;
            Evidence measured;
            test(tracker.Observe(0, true, measured) != nullptr);
        }
        return passed ? count : 0;
    }

    HardwareVideoSession::HardwareVideoSession() noexcept = default;
    HardwareVideoSession::~HardwareVideoSession() { (void)Close(); }

    bool HardwareVideoSession::Fail(const char* reason, HRESULT hr) noexcept
    {
        evidence_.reason = reason;
        evidence_.hr = hr;
        evidence_.completed = false;
        failed_ = true;
        return false;
    }
    void HardwareVideoSession::Guard() const
    {
        Require(impl_ && impl_->ownerThread == GetCurrentThreadId(), "wrong_session_worker");
        Require(impl_->cancelled && !impl_->cancelled->load(), "cancelled");
        Check(impl_->device->GetDeviceRemovedReason(), "d3d11_device_removed");
        if (impl_->state)
        {
            std::lock_guard<std::mutex> lock(impl_->state->mutex);
            Check(impl_->state->callbackHr, "asynchronous_callback_failed");
        }
    }
    void HardwareVideoSession::Arm()
    {
        auto& value = *impl_;
        {
            std::lock_guard<std::mutex> lock(value.state->mutex);
            Require(!value.state->eventPending && !value.state->event && !value.state->stopping,
                "event_ownership_invalid");
            value.state->eventPending = true;
        }
        const HRESULT hr = value.generator->BeginGetEvent(value.eventCallback.Get(), nullptr);
        if (FAILED(hr))
        {
            std::lock_guard<std::mutex> lock(value.state->mutex);
            value.state->eventPending = false;
            throw Failure{ "async_event_subscription_failed", hr };
        }
    }

    bool HardwareVideoSession::Initialize(ID3D11Device* device, const EncodeConfig& configuration,
        const LiveOptions& options, const std::atomic<bool>& cancelled, PacketObserver& observer) noexcept
    {
        if (initialized_ || impl_ || failed_ || closed_) return Fail("session_not_fresh", E_UNEXPECTED);
        try
        {
            if (const char* reason = ValidateConfiguration(configuration)) throw Failure{ reason, E_INVALIDARG };
            Require(device && options.candidateIndex <= 15 && options.operationTimeoutMs >= 1000 &&
                options.operationTimeoutMs <= 30000, "invalid_live_session_options");
            LONGLONG duration = 0;
            Require(ValidateCfrTime(configuration, options.epochTime100ns, 0, options.epochTime100ns, duration),
                "invalid_video_epoch");
            impl_ = std::make_unique<Impl>();
            auto& value = *impl_;
            value.configuration = configuration;
            value.options = options;
            value.ownerThread = GetCurrentThreadId();
            value.cancelled = &cancelled;
            value.observer = &observer;
            value.device = device;
            const auto deadline = Clock::now() + std::chrono::milliseconds(options.operationTimeoutMs);
            Guard();
            Check(value.device.As(&value.multithread), "device_multithread_interface_missing");
            Require(value.multithread->GetMultithreadProtected() != FALSE, "device_multithread_protection_required");
            // The live API deliberately has no game-closed policy. All adapter,
            // hardware/async/D3D/format checks are the shared verified path.
            value.gop.maximumFrames = 2 * configuration.frameRate;
            detail::Initialize(device, options.candidateIndex, configuration, value.session, evidence_, false,
                value.gop.maximumFrames);
            Guard();
            Require(Clock::now() < deadline, "session_initialization_deadline");
            value.state = std::make_shared<SharedState>();
            evidence_.allocatedInputBindFlags = evidence_.inputBindFlags | D3D11_BIND_RENDER_TARGET;
            evidence_.frameProviderUsed = true;
            CreateTextures(device, value.state, evidence_.allocatedInputBindFlags, configuration, true);
            Check(value.session.transform.As(&value.generator), "async_event_interface_missing");
            const std::weak_ptr<SharedState> weak = value.state;
            const ComPtr<IMFMediaEventGenerator> callbackGenerator = value.generator;
            value.eventCallback = Microsoft::WRL::Make<Callback>([weak, callbackGenerator](IMFAsyncResult* result) -> HRESULT
            {
                ComPtr<IMFMediaEvent> event;
                const HRESULT hr = callbackGenerator->EndGetEvent(result, &event);
                if (auto shared = weak.lock())
                {
                    {
                        std::lock_guard<std::mutex> lock(shared->mutex);
                        shared->eventPending = false;
                        if (!shared->stopping)
                        {
                            if (FAILED(hr)) shared->callbackHr = hr;
                            else if (shared->event) shared->callbackHr = E_UNEXPECTED;
                            else shared->event = event;
                        }
                    }
                    shared->changed.notify_all();
                }
                return S_OK;
            });
            Require(value.eventCallback.Get() != nullptr, "event_callback_allocation_failed");
            ComPtr<IMFMediaType> type;
            Check(value.session.transform->GetOutputCurrentType(value.session.outputId, &type), "observer_output_type_failed");
            Check(observer.OnConfiguration(type.Get(), configuration), "live_configuration_observer_failed");
            Guard();
            Require(Clock::now() < deadline, "session_initialization_deadline");
            Arm();
            Check(value.session.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "begin_streaming_failed");
            value.session.streaming = true;
            Check(value.session.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "start_stream_failed");
            Guard();
            Require(Clock::now() < deadline, "session_initialization_deadline");
            initialized_ = true;
            evidence_.reason = "live_hardware_encoder_ready";
            return true;
        }
        catch (const Failure& failure) { Fail(failure.reason, failure.hr); }
        catch (const std::bad_alloc&) { Fail("allocation_failed", E_OUTOFMEMORY); }
        catch (...) { Fail("unexpected_native_failure", E_FAIL); }
        (void)Close();
        return false;
    }

    void HardwareVideoSession::ProcessEvent(IMFMediaEvent* event)
    {
        auto& value = *impl_;
        HRESULT status = S_OK;
        Check(event->GetStatus(&status), "event_status_query_failed");
        Check(status, "hardware_event_failed");
        MediaEventType type = MEUnknown;
        Check(event->GetType(&type), "event_type_query_failed");
        if (type == METransformNeedInput)
        {
            UINT32 stream = 0;
            Check(event->GetUINT32(MF_EVENT_MFT_INPUT_STREAM_ID, &stream), "input_event_stream_failed");
            Require(stream == value.session.inputId, "input_event_stream_mismatch");
            ++evidence_.needInputEvents;
            if (!value.draining)
            {
                Require(value.inputCredits < 32, "input_credit_limit_exceeded");
                ++value.inputCredits;
            }
        }
        else if (type == METransformHaveOutput)
        {
            ++evidence_.haveOutputEvents;
            Require(evidence_.outputSamples < evidence_.submitted, "excess_output_samples");
            struct Adapter final : FixtureObserver
            {
                PacketObserver& observer;
                LONGLONG expectedDuration;
                Adapter(PacketObserver& sink, LONGLONG duration) : observer(sink), expectedDuration(duration) {}
                HRESULT OnInput(UINT, UINT) noexcept override { return E_UNEXPECTED; }
                HRESULT OnOutput(IMFMediaType* mediaType, IMFSample* sample) noexcept override
                {
                    LONGLONG duration = 0;
                    const HRESULT hr = sample->GetSampleDuration(&duration);
                    if (FAILED(hr)) return hr;
                    if (duration <= 0 || duration < expectedDuration - 1 || duration > expectedDuration + 1)
                        return E_UNEXPECTED;
                    return observer.OnPacket(mediaType, sample);
                }
            } adapter{ *value.observer, FrameTime(evidence_.outputSamples + 1, value.configuration.frameRate) -
                FrameTime(evidence_.outputSamples, value.configuration.frameRate) };
            const LONGLONG expected = value.options.epochTime100ns + FrameTime(evidence_.outputSamples, value.configuration.frameRate);
            ReadOutput(value.session, value.configuration, evidence_, &adapter, expected, false, &value.gop);
        }
        else if (type == METransformDrainComplete)
        {
            Require(value.draining, "unexpected_drain_completion");
            UINT32 stream = 0;
            Check(event->GetUINT32(MF_EVENT_MFT_INPUT_STREAM_ID, &stream), "drain_event_stream_failed");
            Require(stream == value.session.inputId, "drain_event_stream_mismatch");
            evidence_.drainComplete = true;
        }
        else throw Failure{ "unexpected_transform_event", E_UNEXPECTED };
        if (!evidence_.drainComplete) Arm();
    }

    bool HardwareVideoSession::Pump(UINT waitMs) noexcept
    {
        if (!initialized_ || failed_ || closed_ || waitMs > 1000) return Fail("session_not_pumpable", E_UNEXPECTED);
        if (evidence_.drainComplete) return true;
        try
        {
            Guard();
            auto& value = *impl_;
            const auto deadline = Clock::now() + std::chrono::milliseconds(value.options.operationTimeoutMs);
            const auto waitDeadline = Clock::now() + std::chrono::milliseconds(waitMs);
            for (UINT batch = 0; batch < 32 && !evidence_.drainComplete; ++batch)
            {
                Guard();
                Require(Clock::now() < deadline, "event_processing_deadline");
                ComPtr<IMFMediaEvent> event;
                {
                    std::unique_lock<std::mutex> lock(value.state->mutex);
                    if (batch == 0 && waitMs)
                    {
                        while (!value.state->event && SUCCEEDED(value.state->callbackHr) && !value.cancelled->load() &&
                            Clock::now() < waitDeadline)
                            value.state->changed.wait_until(lock, (std::min)(waitDeadline, Clock::now() + std::chrono::milliseconds(50)));
                    }
                    Check(value.state->callbackHr, "asynchronous_callback_failed");
                    event = std::move(value.state->event);
                }
                Guard();
                if (!event) break;
                ProcessEvent(event.Get());
            }
            Guard();
            Require(Clock::now() < deadline, "event_processing_deadline");
            return true;
        }
        catch (const Failure& failure) { return Fail(failure.reason, failure.hr); }
        catch (const std::bad_alloc&) { return Fail("allocation_failed", E_OUTOFMEMORY); }
        catch (...) { return Fail("unexpected_native_failure", E_FAIL); }
    }

    bool HardwareVideoSession::CanAcceptInput() const noexcept
    {
        if (!initialized_ || failed_ || closed_ || !impl_ || impl_->ownerThread != GetCurrentThreadId() ||
            impl_->draining || !impl_->inputCredits || impl_->cancelled->load()) return false;
        std::lock_guard<std::mutex> lock(impl_->state->mutex);
        return SUCCEEDED(impl_->state->callbackHr) && impl_->state->ownership.current < PoolSize;
    }

    SubmitResult HardwareVideoSession::TrySubmit(UINT frameIndex, LONGLONG time, FrameWriter& writer) noexcept
    {
        if (!initialized_ || failed_ || closed_ || impl_->draining)
        { Fail("session_not_submittable", E_UNEXPECTED); return SubmitResult::Failed; }
        try
        {
            Guard();
            auto& value = *impl_;
            LONGLONG duration = 0;
            Require(frameIndex == evidence_.submitted && ValidateCfrTime(value.configuration, value.options.epochTime100ns,
                frameIndex, time, duration), "discontinuous_video_frame_clock");
            if (!Pump(0)) return SubmitResult::Failed;
            if (!CanAcceptInput()) return SubmitResult::WouldBlock;
            const auto deadline = Clock::now() + std::chrono::milliseconds(value.options.operationTimeoutMs);
            int slot = -1;
            {
                std::lock_guard<std::mutex> lock(value.state->mutex);
                Check(value.state->callbackHr, "asynchronous_callback_failed");
                slot = value.state->ownership.Acquire();
            }
            if (slot < 0) return SubmitResult::WouldBlock;
            struct Adapter final : FixtureFrameProvider
            {
                FrameWriter& writer;
                explicit Adapter(FrameWriter& source) : writer(source) {}
                HRESULT Initialize(ID3D11Device*, const EncodeConfig&) noexcept override { return E_UNEXPECTED; }
                HRESULT Fill(UINT frame, UINT, ID3D11Texture2D* destination) noexcept override
                { return writer.Fill(frame, destination); }
            } adapter{ writer };
            Submit(value.session, value.state, static_cast<UINT>(slot), frameIndex, value.configuration,
                &adapter, value.multithread.Get(), evidence_, time, duration);
            if (evidence_.submitted == 0) firstInputTime_ = time;
            lastInputTime_ = time;
            ++evidence_.submitted;
            --value.inputCredits;
            Guard();
            Require(Clock::now() < deadline, "frame_submission_deadline");
            evidence_.reason = "live_frame_submitted";
            return SubmitResult::Submitted;
        }
        catch (const Failure& failure) { Fail(failure.reason, failure.hr); }
        catch (const std::bad_alloc&) { Fail("allocation_failed", E_OUTOFMEMORY); }
        catch (...) { Fail("unexpected_native_failure", E_FAIL); }
        return SubmitResult::Failed;
    }

    bool HardwareVideoSession::Drain() noexcept
    {
        if (!initialized_ || failed_ || closed_ || impl_->draining || evidence_.submitted == 0)
            return Fail("session_not_drainable", E_UNEXPECTED);
        try
        {
            Guard();
            auto& value = *impl_;
            const auto deadline = Clock::now() + std::chrono::milliseconds(value.options.operationTimeoutMs);
            Check(value.session.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, value.session.inputId), "end_of_stream_failed");
            Check(value.session.transform->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, value.session.inputId), "drain_request_failed");
            value.draining = true;
            value.inputCredits = 0;
            while (!evidence_.drainComplete)
            {
                Guard();
                Require(Clock::now() < deadline, "drain_deadline_reached");
                if (!Pump(50)) return false;
            }
            Guard();
            Require(Clock::now() < deadline, "drain_deadline_reached");
            VerifyOutput(value.session, value.configuration, evidence_);
            Require(evidence_.outputSamples == evidence_.submitted && evidence_.encodedBytes > 0,
                "encoded_frame_count_mismatch");
            Require(evidence_.cleanPoints > 0 && evidence_.sequenceHeaderBytes > 0, "encoded_configuration_evidence_missing");
            evidence_.completed = true;
            evidence_.reason = "live_hardware_encoder_drained";
            return true;
        }
        catch (const Failure& failure) { return Fail(failure.reason, failure.hr); }
        catch (const std::bad_alloc&) { return Fail("allocation_failed", E_OUTOFMEMORY); }
        catch (...) { return Fail("unexpected_native_failure", E_FAIL); }
    }

    HRESULT HardwareVideoSession::Close() noexcept
    {
        if (closed_) return evidence_.cleanupHr;
        if (!impl_) { closed_ = true; return S_OK; }
        auto& value = *impl_;
        if (value.ownerThread != GetCurrentThreadId()) return E_UNEXPECTED;
        if (value.state)
        {
            std::lock_guard<std::mutex> lock(value.state->mutex);
            value.state->stopping = true;
        }
        const HRESULT hr = value.session.Close();
        if (FAILED(hr)) evidence_.cleanupHr = hr;
        value.generator.Reset();
        value.eventCallback.Reset();
        if (value.state)
        {
            std::unique_lock<std::mutex> lock(value.state->mutex);
            value.state->changed.wait_for(lock, std::chrono::seconds(2), [&]()
            { return value.state->ownership.current == 0 && !value.state->eventPending; });
            evidence_.samplesReturned = value.state->ownership.current == 0;
            evidence_.eventCallbackDrained = !value.state->eventPending;
            evidence_.returnedSamples = value.state->ownership.returned;
            evidence_.peakOwnedSamples = value.state->ownership.peak;
            if (FAILED(value.state->callbackHr) && SUCCEEDED(evidence_.cleanupHr)) evidence_.cleanupHr = value.state->callbackHr;
            if ((!evidence_.samplesReturned || !evidence_.eventCallbackDrained) && SUCCEEDED(evidence_.cleanupHr))
                evidence_.cleanupHr = E_FAIL;
        }
        if (FAILED(evidence_.cleanupHr))
        {
            evidence_.completed = false;
            if (!failed_) { evidence_.reason = "encoder_cleanup_incomplete"; evidence_.hr = evidence_.cleanupHr; }
        }
        evidence_.elapsedMs = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
            Clock::now() - value.started).count());
        impl_.reset();
        closed_ = true;
        return evidence_.cleanupHr;
    }
}
