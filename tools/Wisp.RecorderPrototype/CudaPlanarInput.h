#pragma once

#include <d3d11.h>
#include <dxgi.h>
#include <cstdint>

namespace recorder::cuda
{
    // Four owned R16_UINT textures, W by 3H: each MSB-aligned 10-bit plane
    // occupies H rows. No CPU media copy or CUDA kernel is used.
    // All methods belong to the Initialize thread. Caller must finish D3D
    // writes before BeginCopy, wait for IsCopyReady before NVENC mapping, and
    // retire/unmap NVENC before reusing a slot or calling Close.
    // Close polls pending copies for at most timeoutMs. Driver calls themselves
    // require the recorder's process watchdog. Failed cleanup retains resources;
    // the helper process must exit rather than continue or destroy the device.
    class PlanarInput final
    {
    public:
        PlanarInput() noexcept = default;
        ~PlanarInput();
        PlanarInput(const PlanarInput&) = delete;
        PlanarInput& operator=(const PlanarInput&) = delete;
        bool Initialize(IDXGIAdapter*, UINT width, UINT height, UINT timeoutMs) noexcept;
        bool Register(UINT slot, ID3D11Texture2D*) noexcept;
        bool BeginCopy(UINT slot) noexcept;
        bool IsCopyReady(UINT slot, bool& ready) noexcept;
        HRESULT Close() noexcept;
        // Optional scope for the caller's NVENC calls. Not nestable; none of
        // the other methods may run between Enter and Leave.
        HRESULT Enter() noexcept;
        HRESULT Leave() noexcept;
        void* Context() const noexcept;
        std::uint64_t DevicePointer(UINT slot) const noexcept;
        UINT Pitch(UINT slot) const noexcept;
        const char* Reason() const noexcept { return reason_; }
        HRESULT Error() const noexcept { return error_; }

    private:
        struct State;
        State* state_ = nullptr;
        const char* reason_ = "cuda_not_initialized";
        HRESULT error_ = S_OK;
        bool Fail(const char*, HRESULT = E_FAIL) noexcept;
        bool Guard() noexcept;
    };
}
