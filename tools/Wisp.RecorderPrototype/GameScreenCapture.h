#pragma once

#include "GameWindowCapture.h"

namespace recorder::capture
{
    // Pure policy contracts. Pixel format and monitor color space are distinct;
    // unsupported combinations are never silently relabelled as linear scRGB.
    SourceEncoding ScreenSourceEncoding(DXGI_COLOR_SPACE_TYPE, DXGI_FORMAT) noexcept;
    bool ScreenFullscreenBounds(const RECT& client, const RECT& monitor) noexcept;
    bool ScreenQpcTo100ns(std::uint64_t ticks, std::uint64_t frequency, LONGLONG& value) noexcept;
    UINT RunScreenCaptureContracts() noexcept;

    // Captures the whole exact monitor occupied by the verified fullscreen FH6
    // target, including HUD and notifications. This is not isolated HWND capture.
    // Focus/fullscreen checks bracket acquisition and submission, but cannot be
    // atomic with a desktop transition. Separate hardware cursor planes are not
    // composited; a cursor already present in the desktop image is retained.
    //
    // Every public call/destruction belongs to one MTA worker. Initialize only
    // negotiates resources; no frame is acquired until Start. CheckTarget must
    // be called each host tick outside the D3D lock: it acquires at most once per
    // requested frame interval, waits at most 1 ms, and repeats the latest image
    // on timeout. Display-color/white queries retain the 250 ms validation cadence.
    // The host must sleep between ticks and retain its external API watchdog.
    // No WGC, permission requests, audio, window activation, readback or files.
    class GameScreenCapture final
    {
    public:
        GameScreenCapture() noexcept;
        ~GameScreenCapture();
        GameScreenCapture(const GameScreenCapture&) = delete;
        GameScreenCapture& operator=(const GameScreenCapture&) = delete;
        bool Initialize(const TargetIdentity&, const Options&) noexcept;
        bool Start() noexcept;
        bool CheckTarget() noexcept;
        ID3D11Device* Device() const noexcept;
        const SourceDescription& Source() const noexcept;
        // Requires the encoder's existing immediate-context lock. Does not
        // acquire/wait. Validates target before and after borrowed conversion.
        HRESULT SubmitLatestLocked(ID3D11Texture2D*, FrameConsumer&, FrameInfo&) noexcept;
        Evidence Result() const noexcept;
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
