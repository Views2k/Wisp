#pragma once

#include <windows.h>
#include <d3d11.h>
#include <dxgicommon.h>
#include <cstdint>
#include <memory>

namespace recorder::capture
{
    struct TargetIdentity
    {
        HWND window = nullptr;
        DWORD processId = 0;
        std::uint64_t creationTime = 0;
    };
    struct Options { UINT frameRate = 60; bool borderlessAllowed = false; };
    enum class SourceEncoding { Unknown, SrgbBgra8, LinearScRgbFp16 };
    struct SourceDescription
    {
        UINT width = 0, height = 0;
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
        SourceEncoding encoding = SourceEncoding::Unknown;
        DXGI_COLOR_SPACE_TYPE outputColorSpace = DXGI_COLOR_SPACE_CUSTOM;
        bool hdr = false, referenceWhiteQueried = false;
        // OS SDR-white exposure reference, not measured game paper white or peak.
        float referenceWhiteNits = 0;
    };
    struct FrameInfo
    {
        std::uint64_t version = 0;
        // Monotonic compositor metadata, in100ns QPC units. Not an encoded CFR PTS.
        LONGLONG timestamp100ns = 0;
        // Original compositor value retained separately from monotonic metadata.
        LONGLONG rawTimestamp100ns = 0;
        // Local QPC ticks observed after dequeuing this frame, before surface
        // work or the device lock. Paired with the texture/version under that lock.
        std::uint64_t receivedQpc = 0;
    };
    struct Evidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK, cleanupHr = S_OK;
        bool initialized = false, started = false, stopped = false;
        bool callbacksDrained = false;
        bool borderlessRequested = false, borderRequiredSetFalse = false;
        bool duplicationInvalidated = false;
        std::uint64_t copiedFrames = 0, emptyCallbacks = 0, timestampClamps = 0;
        SourceDescription source{};
    };
    // Only normalizes duplicate/backward positive compositor metadata. Encoded
    // CFR timestamps and the session's original A/V epoch are not adjusted.
    bool NormalizeFrameTimestamp(LONGLONG raw, LONGLONG previous,
        LONGLONG& normalized, bool& clamped) noexcept;
    struct FrameConsumer
    {
        virtual ~FrameConsumer() = default;
        // Borrowed textures for this call only. Submit same-device GPU commands;
        // do not retain, wait, call MF or re-enter capture. Caller holds the
        // recorder device's ID3D10Multithread lock over the entire conversion.
        virtual HRESULT Submit(ID3D11Texture2D* source, ID3D11Texture2D* destination) noexcept = 0;
    };

    // Inert until Initialize/Start. Caller owns an MTA worker; all public calls
    // and destruction occur there. No focus changes, window creation, audio,
    // pixel readback, encoding, file output or arbitrary-process capture.
    // Border suppression is requested only after explicit OS access approval.
    // Windows can still retain a border; no OS policy or setting is bypassed.
    class GameWindowCapture final
    {
    public:
        GameWindowCapture() noexcept;
        ~GameWindowCapture();
        GameWindowCapture(const GameWindowCapture&) = delete;
        GameWindowCapture& operator=(const GameWindowCapture&) = delete;
        bool Initialize(const TargetIdentity&, const Options&) noexcept;
        bool Start() noexcept;
        // Call at least every 250 ms on the worker, outside its D3D lock.
        // Display queries are throttled to that interval, not run per frame.
        // Any identity/geometry/display epoch change stops this session. The
        // orchestrator must close and explicitly initialize a new session.
        bool CheckTarget() noexcept;
        // Borrowed until Close. Initialize encoder/converters with this device;
        // do not use Wisp's HUD device. Its multithread protection is enabled.
        ID3D11Device* Device() const noexcept;
        const SourceDescription& Source() const noexcept;
        // ONLY from the encoder's locked FrameWriter call. S_FALSE means no
        // frame yet; S_OK means GPU conversion commands were submitted, not
        // completed. Reusing the latest version is explicit in returned info;
        // the scheduler owns cadence, freshness and duplication decisions.
        HRESULT SubmitLatestLocked(ID3D11Texture2D* destination, FrameConsumer&, FrameInfo&) noexcept;
        Evidence Result() const noexcept;
        // Revokes and closes without holding a D3D/callback lock; waits up to
        // two seconds for in-flight callbacks. The host still owns its watchdog
        // around synchronous Windows API calls. Late callbacks own their state.
        HRESULT Close() noexcept;
    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
        SourceDescription source_{};
        Evidence evidence_{};
        bool closed_ = false;
        bool Fail(const char*, HRESULT) noexcept;
    };
}
