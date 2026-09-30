#pragma once

#include <windows.h>
#include <d3d11.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdint>

namespace recorder::encoder
{
    constexpr UINT Width = 1920;
    constexpr UINT Height = 1080;
    constexpr UINT FrameRate = 30;
    constexpr UINT PoolSize = 3;
    constexpr UINT Bitrate = 8000000;
    // Bounded application policy, not a claim about hardware acceptance or
    // visual quality. Exact media-type negotiation still decides support.
    constexpr UINT MinimumBitrate = 100000;
    constexpr UINT MaximumBitrate = 120000000;

    struct EncodeConfig
    {
        UINT width = Width, height = Height, frameRate = FrameRate, bitrate = Bitrate;
        UINT pixelAspectNumerator = 1, pixelAspectDenominator = 1;
        UINT chromaSiting = 0; // Unspecified preserves the original fixture.
    };
    const char* ValidateConfiguration(const EncodeConfig& configuration) noexcept;

    enum class Mode { Help, SelfTest, Encode };
    struct Options
    {
        Mode mode = Mode::Help;
        UINT adapterIndex = 0;
        UINT encoderIndex = 0;
        UINT frames = 60;
        UINT timeoutMs = 10000;
        EncodeConfig configuration{};
    };

    // Parsing and contract tests do not initialize COM, MF, graphics or capture.
    bool ParseOptions(int argc, const wchar_t* const* argv, Options& result) noexcept;
    UINT RunContractTests() noexcept; // Zero denotes failure; otherwise checks passed.

    struct Evidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK;
        HRESULT cleanupHr = S_OK;
        bool completed = false;
        bool hardwareRegistration = false;
        bool activatedHardwareAttribute = false;
        bool asynchronous = false;
        bool d3d11Aware = false;
        bool sameDeviceManager = false;
        bool baselineProfile = false;
        bool bPictureControlSupported = false;
        bool bPictureZeroReadback = false;
        bool lowLatencySupported = false;
        bool lowLatencyReadback = false;
        bool gopControlSupported = false;
        bool gopSizeReadback = false;
        HRESULT gopModifiableQueryHr = E_PENDING;
        bool drainComplete = false;
        bool samplesReturned = false;
        bool eventCallbackDrained = false;
        bool inputAttributeStoreAvailable = false;
        bool inputBindHintAvailable = false;
        bool configurationValidated = false;
        bool configurationNegotiated = false;
        bool frameProviderUsed = false;
        EncodeConfig configuration{};
        UINT providerFramesFilled = 0;
        UINT allocatedInputBindFlags = 0;
        UINT hardwareCandidates = 0;
        UINT submitted = 0;
        UINT outputSamples = 0;
        UINT cleanPoints = 0;
        UINT missingCleanPointAttributes = 0;
        // Zero for the standalone fixture, which preserves vendor GOP defaults.
        UINT requestedGopFrames = 0;
        UINT negotiatedGopFrames = 0;
        UINT observedGopIntervals = 0;
        UINT maximumObservedGopFrames = 0;
        UINT trailingGopFrames = 0;
        UINT sequenceHeaderBytes = 0;
        UINT returnedSamples = 0;
        UINT peakOwnedSamples = 0;
        UINT inputBindFlags = 0;
        UINT needInputEvents = 0;
        UINT haveOutputEvents = 0;
        std::uint64_t encodedBytes = 0;
        std::uint64_t checksumFnv1a64 = 14695981039346656037ull;
        std::uint64_t elapsedMs = 0;
    };

    // Caller initializes MTA/MF and supplies the same hardware D3D11 device used
    // for its input surfaces. No device, software transform or adapter fallback.
    struct HardwareSession
    {
        Microsoft::WRL::ComPtr<IMFActivate> activation;
        Microsoft::WRL::ComPtr<IMFTransform> transform;
        Microsoft::WRL::ComPtr<IMFDXGIDeviceManager> manager;
        Microsoft::WRL::ComPtr<IMFShutdown> shutdown;
        DWORD inputId = 0;
        DWORD outputId = 0;
        bool streaming = false;
        HardwareSession() = default;
        ~HardwareSession();
        HardwareSession(const HardwareSession&) = delete;
        HardwareSession& operator=(const HardwareSession&) = delete;
        HRESULT Close() noexcept;
    };

    // Returns a fixed reason/HRESULT on incompatibility. Must not run while
    // Forza is open. The original overload retains 1080p30 NV12 -> H264.
    bool InitializeHardwareH264(ID3D11Device* device, UINT candidateIndex,
        HardwareSession& session, Evidence& evidence) noexcept;
    bool InitializeHardwareH264(ID3D11Device* device, UINT candidateIndex,
        const EncodeConfig& configuration, HardwareSession& session, Evidence& evidence) noexcept;

    // Headless synthetic encoding only: no WGC, window, desktop/game pixels,
    // audio, raw-pixel readback, file output, or performance acceptance claim.
    // Optional synchronous observer for downstream synthetic buffer/export
    // checks. References are borrowed for the callback only. The ordinary
    // encoder fixture supplies no observer and retains no encoded content.
    struct FixtureObserver
    {
        virtual ~FixtureObserver() = default;
        virtual HRESULT OnInput(UINT frame, UINT surfaceSlot) noexcept = 0;
        virtual HRESULT OnOutput(IMFMediaType* type, IMFSample* sample) noexcept = 0;
    };

    // Synthetic input extension. Initialize runs before streaming. Fill gets a
    // borrowed free NV12 surface after pool acquisition and runs under the
    // complete device multithread critical section; it must only submit GPU
    // work, never wait, call the encoder or retain the destination. Failure
    // returns the acquired slot before the tracked-sample allocator is armed.
    // Surfaces include RENDER_TARGET when a provider is supplied. The provider
    // and observer must outlive the synchronous RunSyntheticFixture call.
    struct FixtureFrameProvider
    {
        virtual ~FixtureFrameProvider() = default;
        virtual HRESULT Initialize(ID3D11Device* device, const EncodeConfig& configuration) noexcept = 0;
        virtual HRESULT Fill(UINT frame, UINT surfaceSlot, ID3D11Texture2D* destination) noexcept = 0;
    };
    Evidence RunSyntheticFixture(const Options& options, const std::atomic<bool>& cancelled,
        FixtureObserver* observer = nullptr, FixtureFrameProvider* provider = nullptr) noexcept;
}
