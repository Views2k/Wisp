#pragma once

#include "HardwareVideoSession.h"
#include <memory>

namespace recorder::lossless
{
    constexpr UINT SurfaceCount = 4;
    constexpr UINT MaximumPacketBytes = 64u * 1024 * 1024;
    struct Options
    {
        UINT operationTimeoutMs = 3000;
        LONGLONG epochTime100ns = 0;
    };
    struct Evidence
    {
        const char* reason = "not_started";
        const char* cleanupReason = "not_started";
        HRESULT hr = S_OK, cleanupHr = S_OK;
        UINT nvencStatus = 0;
        UINT submitted = 0, encoded = 0, outputSamples = 0, peakInFlight = 0, largestPacketBytes = 0;
        std::uint64_t compressedBytes = 0;
        bool initialized = false, asynchronous = false, drainComplete = false;
        bool resourcesRetained = false;
    };
    const char* ValidateConfiguration(const encoder::EncodeConfig&, const Options&) noexcept;
    bool ValidateFrameTime(UINT rate, LONGLONG epoch, UINT index, LONGLONG time, LONGLONG& duration) noexcept;

    // Separate full-color encoder. Config bitrate/chromaSiting must be zero:
    // there is no lossy rate target or chroma subsampling. AYUV surfaces carry
    // Y=G,U=B,V=R from HdrFrameConverter::PreparedRgbAyuv, with identity VUI.
    // The existing FrameWriter callback receives AYUV here, not NV12. It may
    // only submit GPU work while the device multithread lock is held.
    // PacketObserver receives compressed MF wrappers, never converted pixels.
    //
    // Caller owns COM/MF, the same hardware device with multithread protection,
    // cancellation, worker thread and a process watchdog for blocked driver APIs.
    // TrySubmit/Pump use no CPU pixel readback and no GPU/event wait. A fixed
    // four-surface pool applies backpressure; no fallback encoder is attempted.
    // Drain and Close are bounded waiting operations, called outside device locks.
    // Close retires in-flight work even after cancellation, without observer calls.
    // On cleanup failure the helper must exit: native resources remain quarantined
    // until process teardown rather than being freed while the driver owns them.
    class NvencLosslessVideoSession final
    {
    public:
        NvencLosslessVideoSession() noexcept;
        ~NvencLosslessVideoSession();
        NvencLosslessVideoSession(const NvencLosslessVideoSession&) = delete;
        NvencLosslessVideoSession& operator=(const NvencLosslessVideoSession&) = delete;
        bool Initialize(ID3D11Device*, const encoder::EncodeConfig&, const Options&,
            const std::atomic<bool>& cancelled, encoder::PacketObserver&) noexcept;
        bool CanAcceptInput() const noexcept;
        encoder::SubmitResult TrySubmit(UINT index, LONGLONG time100ns, encoder::FrameWriter&) noexcept;
        bool Pump() noexcept;
        bool BeginDrain() noexcept;
        bool Drain() noexcept;
        HRESULT Close() noexcept;
        const Evidence& Result() const noexcept { return evidence_; }
    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
        Evidence evidence_{};
        bool failed_ = false, closed_ = false;
        bool Fail(const char*, HRESULT, UINT nvencStatus = 0) noexcept;
        void Guard() const;
    };
}
