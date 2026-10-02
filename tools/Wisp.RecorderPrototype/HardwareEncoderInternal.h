#pragma once

#include "HardwareEncoder.h"
#include <d3d10_1.h>
#include <wrl/implements.h>
#include <algorithm>
#include <array>
#include <climits>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>

// Private shared mechanics. Fixture policy and live scheduling remain separate.
namespace recorder::encoder::detail
{
    using Microsoft::WRL::ComPtr;
    struct Failure { const char* reason; HRESULT hr; };
    inline void Check(HRESULT hr, const char* reason)
    { if (FAILED(hr)) throw Failure{ reason, hr }; }
    inline void Require(bool condition, const char* reason)
    { if (!condition) throw Failure{ reason, E_FAIL }; }
    inline void CheckCodecSuccess(HRESULT hr, const char* reason)
    { if (hr != S_OK) throw Failure{ reason, FAILED(hr) ? hr : E_FAIL }; }
    inline void CheckGopModifiability(HRESULT hr)
    {
        // Some hardware MFTs expose the property but do not implement this
        // advisory query. Only SetValue/readback/output can establish support.
        if (hr != E_NOTIMPL) CheckCodecSuccess(hr, "gop_control_not_modifiable");
    }
    inline bool GopReadbackMatches(UINT requested, const VARIANT& observed) noexcept
    { return (requested == 60 || requested == 120) && observed.vt == VT_UI4 && observed.ulVal == requested; }
    struct GopTracker
    {
        UINT maximumFrames = 0, nextIndex = 0, lastCleanIndex = 0;
        bool seenClean = false;
        const char* Observe(UINT index, bool clean, Evidence& evidence) noexcept
        {
            if ((maximumFrames != 60 && maximumFrames != 120) || index != nextIndex || index == UINT_MAX)
                return "gop_output_sequence_invalid";
            if (!seenClean && !clean) return "first_output_not_random_access";
            const UINT distance = index - lastCleanIndex;
            if (seenClean && (distance > maximumFrames || (!clean && distance == maximumFrames)))
                return "output_gop_interval_exceeded";
            if (clean)
            {
                if (seenClean)
                {
                    ++evidence.observedGopIntervals;
                    evidence.maximumObservedGopFrames = (std::max)(evidence.maximumObservedGopFrames, distance);
                }
                lastCleanIndex = index;
                seenClean = true;
            }
            evidence.trailingGopFrames = index - lastCleanIndex + 1;
            ++nextIndex;
            return nullptr;
        }
    };
    inline bool AlignmentMask(DWORD byteAlignment, DWORD& mask) noexcept
    {
        if (byteAlignment > 65536 || (byteAlignment && (byteAlignment & (byteAlignment - 1)))) return false;
        mask = byteAlignment ? byteAlignment - 1 : 0;
        return true;
    }

    struct Ownership
    {
        std::array<bool, PoolSize> busy{};
        UINT current = 0, peak = 0, returned = 0;
        int Acquire() noexcept
        {
            for (UINT index = 0; index < PoolSize; ++index)
            {
                if (busy[index]) continue;
                busy[index] = true;
                peak = (std::max)(peak, ++current);
                return static_cast<int>(index);
            }
            return -1;
        }
        bool Release(UINT index) noexcept
        {
            if (index >= PoolSize || !busy[index] || !current) return false;
            busy[index] = false;
            --current;
            ++returned;
            return true;
        }
    };
    struct SharedState
    {
        std::mutex mutex;
        std::condition_variable changed;
        Ownership ownership;
        std::array<ComPtr<ID3D11Texture2D>, PoolSize> textures;
        ComPtr<IMFMediaEvent> event;
        HRESULT callbackHr = S_OK;
        bool eventPending = false, stopping = false;
    };
    class Callback final : public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IMFAsyncCallback>
    {
    public:
        explicit Callback(std::function<HRESULT(IMFAsyncResult*)> invoke) : invoke_(std::move(invoke)) {}
        STDMETHODIMP GetParameters(DWORD*, DWORD*) override { return E_NOTIMPL; }
        STDMETHODIMP Invoke(IMFAsyncResult* result) override
        {
            try { return invoke_(result); }
            catch (...) { return E_FAIL; }
        }
    private:
        std::function<HRESULT(IMFAsyncResult*)> invoke_;
    };

    LONGLONG FrameTime(UINT frame, UINT frameRate = FrameRate) noexcept;
    void Initialize(ID3D11Device*, UINT candidateIndex, const EncodeConfig&,
        HardwareSession&, Evidence&, bool requireGameClosed, UINT requiredGopFrames = 0);
    void VerifyOutput(HardwareSession&, const EncodeConfig&, Evidence&);
    void CreateTextures(ID3D11Device*, const std::shared_ptr<SharedState>&, UINT bindFlags,
        const EncodeConfig&, bool provider);
    bool Submit(HardwareSession&, const std::shared_ptr<SharedState>&, UINT slot, UINT frame,
        const EncodeConfig&, FixtureFrameProvider*, ID3D10Multithread*, Evidence&,
        LONGLONG time100ns, LONGLONG duration100ns);
    void ReadOutput(HardwareSession&, const EncodeConfig&, Evidence&, FixtureObserver*,
        LONGLONG expectedTime100ns, bool checksum, GopTracker* gop = nullptr);
}
