#pragma once

#include <windows.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <cstdint>

namespace recorder::aac
{
    constexpr UINT SampleRate = 48000;
    constexpr UINT Channels = 2;
    constexpr UINT BitsPerSample = 16;
    constexpr UINT FrameBytes = 4;
    constexpr UINT CodecFrames = 1024;
    constexpr UINT MaximumChunkFrames = 4800;
    constexpr UINT MaximumPacketBytes = 65536;
    constexpr UINT MaximumConfigBytes = 1024;
    constexpr std::uint64_t MaximumSourceFrames = 300ull * SampleRate;

    enum class SessionMode { FiniteFixture, UntilStopped };

    struct Configuration
    {
        UINT bitrate = 192000; // Microsoft AAC supports 96/128/160/192 kbps stereo.
        LONGLONG epochTime100ns = 0;
        std::uint64_t maximumSourceFrames = MaximumSourceFrames; // UntilStopped requires 0.
        UINT operationTimeoutMs = 10000;
        SessionMode mode = SessionMode::FiniteFixture;
    };
    const char* ValidateConfiguration(const Configuration&) noexcept;
    bool FrameTime(LONGLONG epoch, std::uint64_t frame, LONGLONG& time) noexcept;
    // Continuous sessions are limited by the signed 100 ns clock, with a final
    // codec block reserved for padding. This does not expand payload retention.
    bool EffectiveSourceFrameLimit(const Configuration&, std::uint64_t& limit) noexcept;
    bool CounterAdditionFits(std::uint64_t value, std::uint64_t increment) noexcept;
    bool ValidateInputBoundary(const Configuration&, std::uint64_t accepted,
        std::uint64_t firstFrame, UINT frames) noexcept;
    UINT TailPadding(std::uint64_t frames) noexcept;
    bool ValidateInputAllocation(const MFT_INPUT_STREAM_INFO&) noexcept;

    struct CodecConfiguration
    {
        std::array<BYTE, MaximumConfigBytes> userData{};
        UINT userDataBytes = 0;
        UINT audioSpecificConfigOffset = 0;
        UINT audioSpecificConfigBytes = 0;
    };
    // Microsoft MFAudioFormat_AAC uses the HEAACWAVEINFO tail followed by ASC,
    // unlike RAW_AAC1 whose user data is ASC alone. Keep the original blob.
    bool ValidateCodecConfiguration(CodecConfiguration&) noexcept;

    struct PacketView
    {
        const BYTE* data = nullptr;
        UINT bytes = 0;
        std::uint64_t index = 0;
        LONGLONG time100ns = 0;     // Actual MFT output; never silently rebased.
        LONGLONG duration100ns = 0;
    };
    struct PacketObserver
    {
        virtual ~PacketObserver() = default;
        // All references are borrowed during synchronous callbacks only. Copy
        // into bounded retention if needed; do not re-enter this encoder.
        virtual HRESULT OnConfiguration(const CodecConfiguration&, IMFMediaType*) noexcept = 0;
        virtual HRESULT OnPacket(const PacketView&) noexcept = 0;
    };

    struct Evidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK;
        HRESULT cleanupHr = S_OK;
        bool activatedMicrosoftAac = false;
        bool synchronous = false;
        bool negotiated = false;
        bool codecConfigurationVerified = false;
        bool drainComplete = false;
        bool completed = false;
        bool haveOutput = false;
        bool codecDelayKnown = false; // Not specified by the encoder contract.
        bool codecTrailingPaddingKnown = false;
        bool continuous = false;
        UINT configuredBitrate = 0;
        UINT inputMinimumBytes = 0;
        UINT inputAlignmentBytes = 0;
        DWORD inputStreamFlags = 0;
        UINT configurationBytes = 0;
        UINT audioSpecificConfigBytes = 0;
        UINT applicationZeroPaddingFrames = 0;
        std::uint64_t logicalSourceFrames = 0;
        std::uint64_t submittedPcmFrames = 0;
        std::uint64_t inputSamples = 0;
        std::uint64_t outputPackets = 0;
        std::uint64_t outputBytes = 0;
        std::uint64_t checksumFnv1a64 = 14695981039346656037ull;
        LONGLONG firstOutputOffset100ns = 0;
        LONGLONG lastOutputEndOffset100ns = 0;
        LONGLONG minimumOutputDuration100ns = 0;
        LONGLONG maximumOutputDuration100ns = 0;
        std::uint64_t nonContiguousOutputBoundaries = 0;
    };

    // A single ordinary worker owns COM MTA/MF startup and the complete session.
    // Construction is inert. No endpoint, process loopback, playback, capture,
    // MMCSS or GPU activation occurs here. Failure is terminal for this epoch.
    // PCM is interleaved signed 16-bit stereo. Frame indices are relative to the
    // explicitly supplied epoch, not guessed from an audio device position.
    // UntilStopped keeps one encoder across saves. Drain only when ending the
    // session; per-operation deadlines and borrowed-packet ownership still apply.
    class Encoder final
    {
    public:
        Encoder() = default;
        ~Encoder();
        Encoder(const Encoder&) = delete;
        Encoder& operator=(const Encoder&) = delete;
        bool Initialize(const Configuration&, const std::atomic<bool>& cancelled,
            PacketObserver* observer = nullptr) noexcept;
        bool Feed(const std::int16_t* interleaved, UINT frames, std::uint64_t firstFrame) noexcept;
        // Completes a partial 1024-frame block with explicit zero PCM. This is
        // application padding only; codec priming/trailing padding stay unknown.
        bool Drain() noexcept;
        HRESULT Close() noexcept;
        const Evidence& Result() const noexcept { return evidence_; }
        const CodecConfiguration& Format() const noexcept { return codec_; }
    private:
        void CheckGuard(ULONGLONG deadline);
        bool Fail(const char* reason, HRESULT hr) noexcept;
        void SubmitPending(ULONGLONG deadline);
        void PumpOutput(ULONGLONG deadline);
        Configuration configuration_{};
        Evidence evidence_{};
        CodecConfiguration codec_{};
        Microsoft::WRL::ComPtr<IMFTransform> transform_;
        Microsoft::WRL::ComPtr<IMFMediaType> outputType_;
        const std::atomic<bool>* cancelled_ = nullptr;
        PacketObserver* observer_ = nullptr;
        std::array<std::int16_t, CodecFrames * Channels> pending_{};
        UINT pendingFrames_ = 0;
        DWORD inputId_ = 0, outputId_ = 0, ownerThread_ = 0;
        DWORD inputAlignment_ = 0;
        bool started_ = false, failed_ = false, initialized_ = false, closed_ = false;
        LONGLONG previousOutputTime_ = 0, previousOutputEnd_ = 0;
    };

    enum class Mode { Help, SelfTest, Encode };
    struct Options
    {
        Mode mode = Mode::Help;
        UINT pcmFrames = SampleRate * 2;
        UINT timeoutMs = 10000;
    };
    bool ParseOptions(int argc, const wchar_t* const* argv, Options&) noexcept;
    UINT RunContractTests() noexcept;
}
