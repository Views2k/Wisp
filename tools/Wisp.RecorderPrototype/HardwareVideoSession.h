#pragma once

#include "HardwareEncoder.h"
#include <memory>

namespace recorder::encoder
{
    struct LiveOptions
    {
        UINT candidateIndex = 0;
        UINT operationTimeoutMs = 10000;
        LONGLONG epochTime100ns = 0;
    };
    enum class SubmitResult { Submitted, WouldBlock, Failed };

    struct FrameWriter
    {
        virtual ~FrameWriter() = default;
        // Borrowed free NV12 pool surface. Entire call is inside the device's
        // multithread lock. Submit GPU work only: no waits, MF calls, retention
        // of this surface or re-entry. The caller retains any captured source.
        // S_FALSE declines this frame before encoder input, without advancing
        // its frame index or poisoning the session (for an explicit pause).
        virtual HRESULT Fill(UINT frameIndex, ID3D11Texture2D* destination) noexcept = 0;
    };
    struct PacketObserver
    {
        virtual ~PacketObserver() = default;
        // Borrowed references on the session's ordinary worker, never on the
        // MFT event callback thread. Any retention/copy must have its own bound.
        virtual HRESULT OnConfiguration(IMFMediaType*, const EncodeConfig&) noexcept = 0;
        virtual HRESULT OnPacket(IMFMediaType*, IMFSample*) noexcept = 0;
    };

    // CPU-only timestamp contract. Rejects overflow, unsupported rates, the
    // terminal UINT index and any discontinuity from the caller's fixed epoch.
    bool ValidateCfrTime(const EncodeConfig&, LONGLONG epochTime100ns, UINT frameIndex,
        LONGLONG suppliedTime100ns, LONGLONG& duration100ns) noexcept;
    UINT RunLiveContractTests() noexcept;

    // Caller initializes MTA/MF and owns a hardware D3D11 device with immediate
    // context multithread protection enabled. Every method and destruction runs
    // on that single worker. Cancellation and observer outlive Close. No game
    // policy, WGC, audio, scheduling, thread, file or pixel readback is created.
    // Requires a negotiated two-second GOP maximum and verifies actual random-
    // access output spacing. Unsupported controls or noncompliant output fail.
    class HardwareVideoSession final
    {
    public:
        HardwareVideoSession() noexcept;
        ~HardwareVideoSession();
        HardwareVideoSession(const HardwareVideoSession&) = delete;
        HardwareVideoSession& operator=(const HardwareVideoSession&) = delete;
        bool Initialize(ID3D11Device*, const EncodeConfig&, const LiveOptions&,
            const std::atomic<bool>& cancelled, PacketObserver&) noexcept;
        // Processes a bounded event batch; wait is 0..1000 ms. A timed-out wait
        // is normal and does not mean a frame was accepted or produced.
        bool Pump(UINT waitMs) noexcept;
        bool CanAcceptInput() const noexcept;
        SubmitResult TrySubmit(UINT frameIndex, LONGLONG time100ns, FrameWriter&) noexcept;
        bool Drain() noexcept;
        HRESULT Close() noexcept;
        // The common fixture checksum field is unused by a live session.
        const Evidence& Result() const noexcept { return evidence_; }
        LONGLONG FirstInputTime100ns() const noexcept { return firstInputTime_; }
        LONGLONG LastInputTime100ns() const noexcept { return lastInputTime_; }
    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
        Evidence evidence_{};
        LONGLONG firstInputTime_ = 0, lastInputTime_ = 0;
        bool initialized_ = false, failed_ = false, closed_ = false;
        bool Fail(const char*, HRESULT) noexcept;
        void Guard() const;
        void Arm();
        void ProcessEvent(IMFMediaEvent*);
    };
}
