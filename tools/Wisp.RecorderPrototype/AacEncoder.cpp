#include "AacEncoder.h"

#include <mfapi.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <mmreg.h>
#include <algorithm>
#include <cstring>
#include <cwchar>
#include <limits>
#include <new>

namespace recorder::aac
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        struct Failure { const char* reason; HRESULT hr; };
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_FAIL }; }
        bool Number(const wchar_t* text, UINT minimum, UINT maximum, UINT& result) noexcept
        {
            if (!text || !*text) return false;
            UINT value = 0;
            for (; *text; ++text)
            {
                if (*text < L'0' || *text > L'9') return false;
                const UINT digit = static_cast<UINT>(*text - L'0');
                if (value > (maximum - digit) / 10) return false;
                value = value * 10 + digit;
            }
            if (value < minimum || value > maximum) return false;
            result = value;
            return true;
        }
        void Set(IMFAttributes* attributes, REFGUID key, UINT32 value)
        {
            Check(attributes->SetUINT32(key, value), "media_attribute_set_failed");
        }
        void Equal(IMFAttributes* attributes, REFGUID key, UINT32 expected)
        {
            UINT32 actual = 0;
            Check(attributes->GetUINT32(key, &actual), "media_attribute_read_failed");
            Require(actual == expected, "media_attribute_mismatch");
        }
        void EqualGuid(IMFAttributes* attributes, REFGUID key, REFGUID expected)
        {
            GUID value{};
            Check(attributes->GetGUID(key, &value), "media_guid_read_failed");
            Require(value == expected, "media_guid_mismatch");
        }
        struct BufferLock
        {
            IMFMediaBuffer* buffer;
            HRESULT& cleanup;
            BYTE* data = nullptr;
            DWORD capacity = 0, length = 0;
            bool locked = false;
            BufferLock(IMFMediaBuffer* value, HRESULT& cleanupHr) : buffer(value), cleanup(cleanupHr)
            {
                Check(buffer->Lock(&data, &capacity, &length), "buffer_lock_failed");
                locked = true;
            }
            HRESULT Release() noexcept
            {
                if (!locked) return S_OK;
                locked = false;
                const HRESULT hr = buffer->Unlock();
                if (FAILED(hr) && SUCCEEDED(cleanup)) cleanup = hr;
                return hr;
            }
            void Unlock() { Check(Release(), "buffer_unlock_failed"); }
            ~BufferLock() { (void)Release(); }
        };
    }

    const char* ValidateConfiguration(const Configuration& value) noexcept
    {
        if (value.bitrate != 96000 && value.bitrate != 128000 &&
            value.bitrate != 160000 && value.bitrate != 192000) return "unsupported_aac_bitrate";
        if ((value.mode == SessionMode::FiniteFixture &&
                (value.maximumSourceFrames == 0 || value.maximumSourceFrames > MaximumSourceFrames)) ||
            (value.mode == SessionMode::UntilStopped && value.maximumSourceFrames != 0) ||
            (value.mode != SessionMode::FiniteFixture && value.mode != SessionMode::UntilStopped))
            return "invalid_source_frame_bound";
        if (value.operationTimeoutMs < 1000 || value.operationTimeoutMs > 30000)
            return "invalid_operation_timeout";
        std::uint64_t limit = 0;
        if (!EffectiveSourceFrameLimit(value, limit))
            return "invalid_epoch_time";
        return nullptr;
    }

    bool FrameTime(LONGLONG epoch, std::uint64_t frame, LONGLONG& time) noexcept
    {
        if (epoch < 0) return false;
        constexpr auto maximum = static_cast<std::uint64_t>((std::numeric_limits<LONGLONG>::max)());
        const auto seconds = frame / SampleRate;
        const auto remainder = frame % SampleRate;
        if (seconds > maximum / 10000000) return false;
        const auto whole = seconds * 10000000;
        const auto fraction = remainder * 10000000 / SampleRate;
        if (whole > maximum - fraction || whole + fraction > maximum - static_cast<std::uint64_t>(epoch))
            return false;
        time = epoch + static_cast<LONGLONG>(whole + fraction);
        return true;
    }

    bool ValidateInputBoundary(const Configuration& config, std::uint64_t accepted,
        std::uint64_t firstFrame, UINT frames) noexcept
    {
        std::uint64_t limit = 0;
        return frames > 0 && frames <= MaximumChunkFrames && firstFrame == accepted &&
            EffectiveSourceFrameLimit(config, limit) && accepted <= limit && frames <= limit - accepted;
    }

    bool EffectiveSourceFrameLimit(const Configuration& config, std::uint64_t& limit) noexcept
    {
        limit = 0;
        if (config.epochTime100ns < 0) return false;
        if (config.mode == SessionMode::FiniteFixture)
        {
            if (config.maximumSourceFrames == 0 || config.maximumSourceFrames > MaximumSourceFrames) return false;
            LONGLONG end = 0;
            if (!FrameTime(config.epochTime100ns, config.maximumSourceFrames + CodecFrames, end)) return false;
            limit = config.maximumSourceFrames;
            return true;
        }
        if (config.mode != SessionMode::UntilStopped || config.maximumSourceFrames != 0) return false;
        constexpr auto maximum = static_cast<std::uint64_t>((std::numeric_limits<LONGLONG>::max)());
        const auto remaining = maximum - static_cast<std::uint64_t>(config.epochTime100ns);
        // Divide before multiplying: neither whole seconds nor the fractional
        // remainder can overflow. Round down and reserve one complete block.
        const auto frames = (remaining / 10000000) * SampleRate +
            (remaining % 10000000) * SampleRate / 10000000;
        if (frames <= CodecFrames) return false;
        limit = frames - CodecFrames;
        return true;
    }

    bool CounterAdditionFits(std::uint64_t value, std::uint64_t increment) noexcept
    {
        return value <= (std::numeric_limits<std::uint64_t>::max)() - increment;
    }

    UINT TailPadding(std::uint64_t frames) noexcept
    {
        return static_cast<UINT>((CodecFrames - frames % CodecFrames) % CodecFrames);
    }

    bool ValidateInputAllocation(const MFT_INPUT_STREAM_INFO& info) noexcept
    {
        constexpr DWORD supported = MFT_INPUT_STREAM_WHOLE_SAMPLES | MFT_INPUT_STREAM_FIXED_SAMPLE_SIZE |
            MFT_INPUT_STREAM_HOLDS_BUFFERS | MFT_INPUT_STREAM_DOES_NOT_ADDREF |
            MFT_INPUT_STREAM_REMOVABLE | MFT_INPUT_STREAM_OPTIONAL;
        // A single uncompressed frame per buffer or in-place transform needs a
        // different contract. Our PCM blocks contain 1024 whole stereo frames.
        return (info.dwFlags & ~supported) == 0 && info.cbSize <= CodecFrames * FrameBytes &&
            ((info.dwFlags & MFT_INPUT_STREAM_FIXED_SAMPLE_SIZE) == 0 || info.cbSize == CodecFrames * FrameBytes) &&
            !((info.dwFlags & MFT_INPUT_STREAM_HOLDS_BUFFERS) && (info.dwFlags & MFT_INPUT_STREAM_DOES_NOT_ADDREF)) &&
            info.cbAlignment <= 4096 &&
            (info.cbAlignment == 0 || (info.cbAlignment & (info.cbAlignment - 1)) == 0);
    }

    bool ValidateCodecConfiguration(CodecConfiguration& value) noexcept
    {
        constexpr UINT prefix = sizeof(HEAACWAVEINFO) - sizeof(WAVEFORMATEX);
        static_assert(prefix == 12);
        value.audioSpecificConfigOffset = 0;
        value.audioSpecificConfigBytes = 0;
        if (value.userDataBytes != prefix + 2) return false;
        HEAACWAVEINFO info{};
        std::memcpy(reinterpret_cast<BYTE*>(&info) + sizeof(WAVEFORMATEX), value.userData.data(), prefix);
        if (info.wPayloadType != 0 || info.wAudioProfileLevelIndication != 0x29 ||
            info.wStructType != 0 || info.wReserved1 != 0 || info.dwReserved2 != 0) return false;
        const auto* asc = value.userData.data() + prefix;
        const UINT objectType = asc[0] >> 3;
        const UINT frequencyIndex = ((asc[0] & 7u) << 1) | (asc[1] >> 7);
        const UINT channelConfiguration = (asc[1] >> 3) & 15u;
        // Fixed two-byte GASpecificConfig: 1024 frames, no dependent core coder
        // or extension. Additional syntax needs its own validation before this
        // narrow path can accept it; do not silently relabel it as verified LC.
        if (objectType != 2 || frequencyIndex != 3 || channelConfiguration != 2 || (asc[1] & 7u) != 0)
            return false;
        value.audioSpecificConfigOffset = prefix;
        value.audioSpecificConfigBytes = value.userDataBytes - prefix;
        return true;
    }

    bool ParseOptions(int argc, const wchar_t* const* argv, Options& result) noexcept
    {
        result = {};
        if (argc < 1 || !argv) return false;
        if (argc == 1) return true;
        if (!argv[1]) return false;
        if (argc == 2 && std::wcscmp(argv[1], L"--help") == 0) return true;
        if (argc == 2 && std::wcscmp(argv[1], L"--self-test") == 0)
        { result.mode = Mode::SelfTest; return true; }
        if (std::wcscmp(argv[1], L"--encode-fixture") != 0) return false;
        result.mode = Mode::Encode;
        bool frames = false, timeout = false;
        for (int i = 2; i < argc; i += 2)
        {
            if (i + 1 >= argc || !argv[i] || !argv[i + 1]) return false;
            if (std::wcscmp(argv[i], L"--pcm-frames") == 0 && !frames)
            {
                frames = true;
                if (!Number(argv[i + 1], 1, SampleRate * 10, result.pcmFrames)) return false;
            }
            else if (std::wcscmp(argv[i], L"--timeout-ms") == 0 && !timeout)
            {
                timeout = true;
                if (!Number(argv[i + 1], 1000, 30000, result.timeoutMs)) return false;
            }
            else return false;
        }
        return true;
    }

    Encoder::~Encoder() { (void)Close(); }
    bool Encoder::Fail(const char* reason, HRESULT hr) noexcept
    {
        evidence_.reason = reason;
        evidence_.hr = hr;
        evidence_.completed = false;
        failed_ = true;
        return false;
    }
    void Encoder::CheckGuard(ULONGLONG deadline)
    {
        Require(ownerThread_ == GetCurrentThreadId(), "wrong_worker_thread");
        Require(cancelled_ && !cancelled_->load(), "cancelled");
        Require(GetTickCount64() < deadline, "operation_deadline_reached");
    }

    bool Encoder::Initialize(const Configuration& config, const std::atomic<bool>& cancelled,
        PacketObserver* observer) noexcept
    {
        if (initialized_ || transform_ || failed_ || closed_) return Fail("session_not_fresh", E_UNEXPECTED);
        if (const auto* reason = ValidateConfiguration(config)) return Fail(reason, E_INVALIDARG);
        configuration_ = config;
        cancelled_ = &cancelled;
        observer_ = observer;
        ownerThread_ = GetCurrentThreadId();
        const auto deadline = GetTickCount64() + config.operationTimeoutMs;
        evidence_.configuredBitrate = config.bitrate;
        evidence_.continuous = config.mode == SessionMode::UntilStopped;
        try
        {
            CheckGuard(deadline);
            Check(CoCreateInstance(CLSID_AACMFTEncoder, nullptr, CLSCTX_INPROC_SERVER,
                IID_PPV_ARGS(&transform_)), "microsoft_aac_activation_failed");
            evidence_.activatedMicrosoftAac = true;
            ComPtr<IMFAttributes> attributes;
            HRESULT hr = transform_->GetAttributes(&attributes);
            if (hr != E_NOTIMPL)
            {
                Check(hr, "transform_attributes_failed");
                Require(attributes != nullptr, "transform_attributes_missing");
                UINT32 async = 0;
                hr = attributes->GetUINT32(MF_TRANSFORM_ASYNC, &async);
                if (hr != MF_E_ATTRIBUTENOTFOUND) Check(hr, "async_attribute_failed");
                Require(async == 0, "unexpected_async_aac_transform");
            }
            evidence_.synchronous = true;
            DWORD inputs = 0, outputs = 0;
            Check(transform_->GetStreamCount(&inputs, &outputs), "stream_count_failed");
            Require(inputs == 1 && outputs == 1, "unexpected_stream_count");
            hr = transform_->GetStreamIDs(1, &inputId_, 1, &outputId_);
            if (hr == E_NOTIMPL) inputId_ = outputId_ = 0;
            else Check(hr, "stream_ids_failed");
            ComPtr<IMFMediaType> input;
            Check(MFCreateMediaType(&input), "input_type_creation_failed");
            Check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), "input_major_type_failed");
            Check(input->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM), "input_subtype_failed");
            Set(input.Get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            Set(input.Get(), MF_MT_AUDIO_NUM_CHANNELS, Channels);
            Set(input.Get(), MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            Set(input.Get(), MF_MT_AUDIO_BLOCK_ALIGNMENT, FrameBytes);
            Set(input.Get(), MF_MT_AUDIO_AVG_BYTES_PER_SECOND, SampleRate * FrameBytes);
            Check(transform_->SetInputType(inputId_, input.Get(), 0), "pcm_input_type_refused");
            ComPtr<IMFMediaType> output;
            Check(MFCreateMediaType(&output), "output_type_creation_failed");
            Check(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), "output_major_type_failed");
            Check(output->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC), "output_subtype_failed");
            Set(output.Get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            Set(output.Get(), MF_MT_AUDIO_NUM_CHANNELS, Channels);
            Set(output.Get(), MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            Set(output.Get(), MF_MT_AUDIO_AVG_BYTES_PER_SECOND, config.bitrate / 8);
            Set(output.Get(), MF_MT_AAC_PAYLOAD_TYPE, 0);
            Set(output.Get(), MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
            Check(transform_->SetOutputType(outputId_, output.Get(), 0), "aac_output_type_refused");
            Check(transform_->GetInputCurrentType(inputId_, &input), "input_readback_failed");
            EqualGuid(input.Get(), MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            EqualGuid(input.Get(), MF_MT_SUBTYPE, MFAudioFormat_PCM);
            Equal(input.Get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            Equal(input.Get(), MF_MT_AUDIO_NUM_CHANNELS, Channels);
            Equal(input.Get(), MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            Equal(input.Get(), MF_MT_AUDIO_BLOCK_ALIGNMENT, FrameBytes);
            Check(transform_->GetOutputCurrentType(outputId_, &outputType_), "output_readback_failed");
            EqualGuid(outputType_.Get(), MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            EqualGuid(outputType_.Get(), MF_MT_SUBTYPE, MFAudioFormat_AAC);
            Equal(outputType_.Get(), MF_MT_AUDIO_SAMPLES_PER_SECOND, SampleRate);
            Equal(outputType_.Get(), MF_MT_AUDIO_NUM_CHANNELS, Channels);
            Equal(outputType_.Get(), MF_MT_AUDIO_BITS_PER_SAMPLE, BitsPerSample);
            Equal(outputType_.Get(), MF_MT_AUDIO_AVG_BYTES_PER_SECOND, config.bitrate / 8);
            Equal(outputType_.Get(), MF_MT_AAC_PAYLOAD_TYPE, 0);
            Equal(outputType_.Get(), MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
            evidence_.negotiated = true;
            MFT_INPUT_STREAM_INFO inputInfo{};
            Check(transform_->GetInputStreamInfo(inputId_, &inputInfo), "input_stream_info_failed");
            evidence_.inputMinimumBytes = inputInfo.cbSize;
            evidence_.inputAlignmentBytes = inputInfo.cbAlignment;
            evidence_.inputStreamFlags = inputInfo.dwFlags;
            Require(ValidateInputAllocation(inputInfo), "input_allocation_requirements_unsupported");
            inputAlignment_ = inputInfo.cbAlignment;
            Check(outputType_->GetBlobSize(MF_MT_USER_DATA, &codec_.userDataBytes), "aac_config_missing");
            Require(codec_.userDataBytes <= MaximumConfigBytes, "aac_config_too_large");
            UINT32 actual = 0;
            Check(outputType_->GetBlob(MF_MT_USER_DATA, codec_.userData.data(), MaximumConfigBytes, &actual),
                "aac_config_read_failed");
            Require(actual == codec_.userDataBytes && ValidateCodecConfiguration(codec_), "aac_config_incompatible");
            evidence_.codecConfigurationVerified = true;
            evidence_.configurationBytes = codec_.userDataBytes;
            evidence_.audioSpecificConfigBytes = codec_.audioSpecificConfigBytes;
            CheckGuard(deadline);
            if (observer_) Check(observer_->OnConfiguration(codec_, outputType_.Get()), "configuration_observer_failed");
            Check(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "begin_streaming_failed");
            started_ = true;
            Check(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "start_stream_failed");
            CheckGuard(deadline);
            initialized_ = true;
            evidence_.reason = "ready";
            return true;
        }
        catch (const Failure& failure) { return Fail(failure.reason, failure.hr); }
        catch (const std::bad_alloc&) { return Fail("allocation_failed", E_OUTOFMEMORY); }
        catch (...) { return Fail("unexpected_exception", E_UNEXPECTED); }
    }

    void Encoder::PumpOutput(ULONGLONG deadline)
    {
        // Per-call bound is separate from the lifetime packet bound. No busy
        // waiting: NEED_MORE_INPUT immediately hands control back to the caller.
        for (UINT attempt = 0; attempt < 128; ++attempt)
        {
            CheckGuard(deadline);
            MFT_OUTPUT_STREAM_INFO info{};
            Check(transform_->GetOutputStreamInfo(outputId_, &info), "output_stream_info_failed");
            Require(info.cbSize <= MaximumPacketBytes && info.cbAlignment <= 4096 &&
                (info.cbAlignment == 0 || (info.cbAlignment & (info.cbAlignment - 1)) == 0),
                "output_allocation_requirements_unsupported");
            ComPtr<IMFSample> supplied;
            if ((info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) == 0)
            {
                Require(info.cbSize > 0, "output_buffer_size_missing");
                Check(MFCreateSample(&supplied), "output_sample_creation_failed");
                ComPtr<IMFMediaBuffer> buffer;
                Check(MFCreateAlignedMemoryBuffer(info.cbSize, info.cbAlignment ? info.cbAlignment - 1 : 0, &buffer),
                    "output_buffer_creation_failed");
                Check(supplied->AddBuffer(buffer.Get()), "output_add_buffer_failed");
            }
            MFT_OUTPUT_DATA_BUFFER data{};
            data.dwStreamID = outputId_;
            data.pSample = supplied.Get();
            DWORD status = 0;
            const HRESULT hr = transform_->ProcessOutput(0, 1, &data, &status);
            ComPtr<IMFCollection> events;
            events.Attach(data.pEvents);
            ComPtr<IMFSample> sample;
            if (data.pSample == supplied.Get()) sample = supplied;
            else sample.Attach(data.pSample);
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            Check(hr, "aac_process_output_failed");
            Require(status == 0 && (data.dwStatus & ~static_cast<DWORD>(MFT_OUTPUT_DATA_BUFFER_INCOMPLETE)) == 0,
                "unexpected_output_status");
            if (events)
            {
                DWORD count = 0;
                Check(events->GetElementCount(&count), "output_events_read_failed");
                Require(count == 0, "unexpected_output_event");
            }
            Require(sample != nullptr, "output_sample_missing");
            DWORD bytes = 0;
            Check(sample->GetTotalLength(&bytes), "output_length_failed");
            Require(bytes > 0 && bytes <= MaximumPacketBytes, "output_packet_size_invalid");
            LONGLONG time = 0, duration = 0;
            Check(sample->GetSampleTime(&time), "output_timestamp_missing");
            Check(sample->GetSampleDuration(&duration), "output_duration_missing");
            // Each documented output represents 1024 frames. Allow integer
            // rounding of the MFT's duration; do not manufacture replacement time.
            constexpr LONGLONG nominal = 10000000ll * CodecFrames / SampleRate;
            Require(duration >= nominal - 1 && duration <= nominal + 1, "unexpected_aac_frame_duration");
            Require(time <= (std::numeric_limits<LONGLONG>::max)() - duration &&
                time >= (std::numeric_limits<LONGLONG>::min)() + configuration_.epochTime100ns,
                "output_timestamp_overflow");
            bool nonContiguous = false;
            if (evidence_.haveOutput)
            {
                Require(time > previousOutputTime_, "nonincreasing_output_timestamp");
                nonContiguous = time != previousOutputEnd_;
            }
            // Bounded extra packets permit observing priming without pretending
            // the allowance is an encoder-delay value or a padding measurement.
            Require(evidence_.outputPackets < evidence_.inputSamples ||
                evidence_.outputPackets - evidence_.inputSamples < 64, "output_packet_count_bound");
            Require(CounterAdditionFits(evidence_.outputPackets, 1) &&
                CounterAdditionFits(evidence_.outputBytes, bytes) &&
                (!nonContiguous || CounterAdditionFits(evidence_.nonContiguousOutputBoundaries, 1)),
                "output_counter_limit");
            ComPtr<IMFMediaBuffer> buffer;
            Check(sample->ConvertToContiguousBuffer(&buffer), "contiguous_output_buffer_failed");
            BufferLock lock(buffer.Get(), evidence_.cleanupHr);
            Require(lock.data && lock.length == bytes && lock.length <= lock.capacity, "output_buffer_bounds_invalid");
            CheckGuard(deadline);
            const PacketView packet{ lock.data, bytes, evidence_.outputPackets, time, duration };
            if (observer_) Check(observer_->OnPacket(packet), "packet_observer_failed");
            for (DWORD i = 0; i < bytes; ++i)
            {
                evidence_.checksumFnv1a64 ^= lock.data[i];
                evidence_.checksumFnv1a64 *= 1099511628211ull;
            }
            if (!evidence_.haveOutput) evidence_.firstOutputOffset100ns = time - configuration_.epochTime100ns;
            evidence_.minimumOutputDuration100ns = evidence_.haveOutput ?
                (std::min)(evidence_.minimumOutputDuration100ns, duration) : duration;
            evidence_.maximumOutputDuration100ns = (std::max)(evidence_.maximumOutputDuration100ns, duration);
            evidence_.haveOutput = true;
            previousOutputTime_ = time;
            previousOutputEnd_ = time + duration;
            evidence_.lastOutputEndOffset100ns = previousOutputEnd_ - configuration_.epochTime100ns;
            ++evidence_.outputPackets;
            evidence_.outputBytes += bytes;
            if (nonContiguous) ++evidence_.nonContiguousOutputBoundaries;
            lock.Unlock();
        }
        throw Failure{ "output_iteration_bound", E_FAIL };
    }

    void Encoder::SubmitPending(ULONGLONG deadline)
    {
        Require(pendingFrames_ == CodecFrames, "incomplete_submission_block");
        CheckGuard(deadline);
        Require(CounterAdditionFits(evidence_.submittedPcmFrames, CodecFrames) &&
            CounterAdditionFits(evidence_.inputSamples, 1), "input_counter_limit");
        ComPtr<IMFMediaBuffer> buffer;
        Check(MFCreateAlignedMemoryBuffer(CodecFrames * FrameBytes, inputAlignment_ ? inputAlignment_ - 1 : 0,
            &buffer), "input_buffer_creation_failed");
        {
            BufferLock lock(buffer.Get(), evidence_.cleanupHr);
            Require(lock.data && lock.capacity >= CodecFrames * FrameBytes, "input_buffer_too_small");
            std::memcpy(lock.data, pending_.data(), CodecFrames * FrameBytes);
            lock.Unlock();
        }
        Check(buffer->SetCurrentLength(CodecFrames * FrameBytes), "input_length_set_failed");
        ComPtr<IMFSample> sample;
        Check(MFCreateSample(&sample), "input_sample_creation_failed");
        Check(sample->AddBuffer(buffer.Get()), "input_add_buffer_failed");
        LONGLONG time = 0, end = 0;
        Require(FrameTime(configuration_.epochTime100ns, evidence_.submittedPcmFrames, time) &&
            FrameTime(configuration_.epochTime100ns, evidence_.submittedPcmFrames + CodecFrames, end) && end > time,
            "input_frame_clock_invalid");
        Check(sample->SetSampleTime(time), "input_time_set_failed");
        Check(sample->SetSampleDuration(end - time), "input_duration_set_failed");
        if (evidence_.inputSamples == 0) Set(sample.Get(), MFSampleExtension_Discontinuity, TRUE);
        CheckGuard(deadline);
        HRESULT hr = transform_->ProcessInput(inputId_, sample.Get(), 0);
        if (hr == MF_E_NOTACCEPTING)
        {
            const auto before = evidence_.outputPackets;
            PumpOutput(deadline);
            Require(evidence_.outputPackets > before, "input_backpressure_without_output");
            CheckGuard(deadline);
            hr = transform_->ProcessInput(inputId_, sample.Get(), 0);
        }
        Check(hr, "aac_process_input_failed");
        ++evidence_.inputSamples;
        evidence_.submittedPcmFrames += CodecFrames;
        pendingFrames_ = 0;
        PumpOutput(deadline);
    }

    bool Encoder::Feed(const std::int16_t* samples, UINT frames, std::uint64_t firstFrame) noexcept
    {
        if (!initialized_ || failed_ || closed_ || evidence_.drainComplete) return Fail("session_not_feedable", E_UNEXPECTED);
        if (!samples || !ValidateInputBoundary(configuration_, evidence_.logicalSourceFrames, firstFrame, frames))
            return Fail("invalid_or_discontinuous_pcm_input", E_INVALIDARG);
        const auto deadline = GetTickCount64() + configuration_.operationTimeoutMs;
        try
        {
            CheckGuard(deadline);
            UINT copied = 0;
            while (copied < frames)
            {
                const UINT count = (std::min)(frames - copied, CodecFrames - pendingFrames_);
                std::memcpy(pending_.data() + pendingFrames_ * Channels, samples + copied * Channels, count * FrameBytes);
                pendingFrames_ += count;
                copied += count;
                evidence_.logicalSourceFrames += count;
                if (pendingFrames_ == CodecFrames) SubmitPending(deadline);
            }
            CheckGuard(deadline);
            evidence_.reason = "feeding";
            return true;
        }
        catch (const Failure& failure) { return Fail(failure.reason, failure.hr); }
        catch (const std::bad_alloc&) { return Fail("allocation_failed", E_OUTOFMEMORY); }
        catch (...) { return Fail("unexpected_exception", E_UNEXPECTED); }
    }

    bool Encoder::Drain() noexcept
    {
        if (!initialized_ || failed_ || closed_ || evidence_.drainComplete || evidence_.logicalSourceFrames == 0)
            return Fail("session_not_drainable", E_UNEXPECTED);
        const auto deadline = GetTickCount64() + configuration_.operationTimeoutMs;
        try
        {
            CheckGuard(deadline);
            if (pendingFrames_ != 0)
            {
                evidence_.applicationZeroPaddingFrames = CodecFrames - pendingFrames_;
                std::fill(pending_.begin() + pendingFrames_ * Channels, pending_.end(), std::int16_t{ 0 });
                pendingFrames_ = CodecFrames;
                SubmitPending(deadline);
            }
            Check(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, inputId_), "end_of_stream_failed");
            CheckGuard(deadline);
            // For a synchronous MFT, DRAIN's parameter is the input stream ID.
            Check(transform_->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, inputId_), "drain_message_failed");
            PumpOutput(deadline);
            CheckGuard(deadline);
            Require(evidence_.haveOutput, "drained_without_aac_output");
            evidence_.drainComplete = true;
            evidence_.completed = true;
            evidence_.reason = "aac_encoded_and_drained";
            return true;
        }
        catch (const Failure& failure) { return Fail(failure.reason, failure.hr); }
        catch (const std::bad_alloc&) { return Fail("allocation_failed", E_OUTOFMEMORY); }
        catch (...) { return Fail("unexpected_exception", E_UNEXPECTED); }
    }

    HRESULT Encoder::Close() noexcept
    {
        if (closed_) return evidence_.cleanupHr;
        // Same-worker lifetime is mandatory, including destruction. Reject an
        // explicit Close from another worker; destruction must still occur on
        // the owning worker because the members release their COM references.
        if (ownerThread_ && ownerThread_ != GetCurrentThreadId()) return E_UNEXPECTED;
        HRESULT first = evidence_.cleanupHr;
        const auto keep = [&first](HRESULT hr) { if (FAILED(hr) && SUCCEEDED(first)) first = hr; };
        if (transform_)
        {
            if (started_)
            {
                if (!evidence_.drainComplete) keep(transform_->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0));
                keep(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0));
            }
            ComPtr<IMFShutdown> shutdown;
            const HRESULT query = transform_.As(&shutdown);
            if (SUCCEEDED(query)) keep(shutdown->Shutdown());
            else if (query != E_NOINTERFACE) keep(query);
        }
        outputType_.Reset();
        transform_.Reset();
        pending_.fill(0);
        pendingFrames_ = 0;
        observer_ = nullptr;
        cancelled_ = nullptr;
        closed_ = true;
        evidence_.cleanupHr = first;
        if (FAILED(first)) evidence_.completed = false;
        return first;
    }
}
