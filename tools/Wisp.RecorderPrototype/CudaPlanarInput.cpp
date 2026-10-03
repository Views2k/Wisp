#include "CudaPlanarInput.h"

#include <array>
#include <cstddef>
#include <cstring>
#include <limits>
#include <new>
#include <wrl/client.h>

namespace recorder::cuda
{
    namespace
    {
        // Minimal documented Driver API ABI; no Toolkit/runtime dependency.
        // These are the *_v2 exports and the 64-bit CUDA_MEMCPY2D_v2 layout:
        // https://docs.nvidia.com/cuda/cuda-driver-api/cuda_driver_api/structCUDA__MEMCPY2D__v2.html
        using Result = int;
        using Device = int;
        using Handle = void*;
        using Pointer = std::uint64_t;
        constexpr Result Success = 0, NotReady = 600;
        constexpr UINT Slots = 4;
        constexpr std::size_t MaximumSlotBytes = 64u * 1024u * 1024u;
        enum class MemoryType : int { Device = 2, Array = 3 };
        struct Copy2D
        {
            std::size_t srcXInBytes, srcY;
            MemoryType srcMemoryType;
            const void* srcHost;
            Pointer srcDevice;
            Handle srcArray;
            std::size_t srcPitch;
            std::size_t dstXInBytes, dstY;
            MemoryType dstMemoryType;
            void* dstHost;
            Pointer dstDevice;
            Handle dstArray;
            std::size_t dstPitch, WidthInBytes, Height;
        };
        static_assert(sizeof(void*) == 8 && sizeof(std::size_t) == 8 && sizeof(Pointer) == 8);
        static_assert(sizeof(Copy2D) == 128 && alignof(Copy2D) == 8);
        static_assert(offsetof(Copy2D, srcMemoryType) == 16 && offsetof(Copy2D, srcArray) == 40);
        static_assert(offsetof(Copy2D, dstMemoryType) == 72 && offsetof(Copy2D, dstDevice) == 88);
        static_assert(offsetof(Copy2D, dstPitch) == 104 && offsetof(Copy2D, Height) == 120);
        struct Failure { const char* reason; HRESULT error; };
        void Require(bool value, const char* reason, HRESULT error = E_INVALIDARG)
        { if (!value) throw Failure{ reason, error }; }
        void Check(Result value, const char* reason)
        { Require(value == Success, reason, E_FAIL); }
        template<class T> T Export(HMODULE module, const char* name)
        {
            const auto address = GetProcAddress(module, name);
            Require(address != nullptr, "cuda_api_export_missing", E_NOTIMPL);
            T result{};
            static_assert(sizeof(result) == sizeof(address));
            std::memcpy(&result, &address, sizeof(result));
            return result;
        }
        struct Api
        {
            Result(WINAPI* init)(unsigned int) = nullptr;
            Result(WINAPI* getDevice)(Device*, IDXGIAdapter*) = nullptr;
            Result(WINAPI* createContext)(Handle*, unsigned int, Device) = nullptr;
            Result(WINAPI* destroyContext)(Handle) = nullptr;
            Result(WINAPI* pushContext)(Handle) = nullptr;
            Result(WINAPI* popContext)(Handle*) = nullptr;
            Result(WINAPI* allocate)(Pointer*, std::size_t*, std::size_t, std::size_t, unsigned int) = nullptr;
            Result(WINAPI* free)(Pointer) = nullptr;
            Result(WINAPI* registerTexture)(Handle*, ID3D11Resource*, unsigned int) = nullptr;
            Result(WINAPI* unregisterTexture)(Handle) = nullptr;
            Result(WINAPI* map)(unsigned int, Handle*, Handle) = nullptr;
            Result(WINAPI* unmap)(unsigned int, Handle*, Handle) = nullptr;
            Result(WINAPI* mappedArray)(Handle*, Handle, unsigned int, unsigned int) = nullptr;
            Result(WINAPI* copy)(const Copy2D*, Handle) = nullptr;
            Result(WINAPI* createStream)(Handle*, unsigned int) = nullptr;
            Result(WINAPI* destroyStream)(Handle) = nullptr;
            Result(WINAPI* createEvent)(Handle*, unsigned int) = nullptr;
            Result(WINAPI* recordEvent)(Handle, Handle) = nullptr;
            Result(WINAPI* queryEvent)(Handle) = nullptr;
            Result(WINAPI* destroyEvent)(Handle) = nullptr;
            void Load(HMODULE module)
            {
#define CUDA_EXPORT(member, name) member = Export<decltype(member)>(module, name)
                CUDA_EXPORT(init, "cuInit");
                CUDA_EXPORT(getDevice, "cuD3D11GetDevice");
                CUDA_EXPORT(createContext, "cuCtxCreate_v2");
                CUDA_EXPORT(destroyContext, "cuCtxDestroy_v2");
                CUDA_EXPORT(pushContext, "cuCtxPushCurrent_v2");
                CUDA_EXPORT(popContext, "cuCtxPopCurrent_v2");
                CUDA_EXPORT(allocate, "cuMemAllocPitch_v2");
                CUDA_EXPORT(free, "cuMemFree_v2");
                CUDA_EXPORT(registerTexture, "cuGraphicsD3D11RegisterResource");
                CUDA_EXPORT(unregisterTexture, "cuGraphicsUnregisterResource");
                CUDA_EXPORT(map, "cuGraphicsMapResources");
                CUDA_EXPORT(unmap, "cuGraphicsUnmapResources");
                CUDA_EXPORT(mappedArray, "cuGraphicsSubResourceGetMappedArray");
                CUDA_EXPORT(copy, "cuMemcpy2DAsync_v2");
                CUDA_EXPORT(createStream, "cuStreamCreate");
                CUDA_EXPORT(destroyStream, "cuStreamDestroy_v2");
                CUDA_EXPORT(createEvent, "cuEventCreate");
                CUDA_EXPORT(recordEvent, "cuEventRecord");
                CUDA_EXPORT(queryEvent, "cuEventQuery");
                CUDA_EXPORT(destroyEvent, "cuEventDestroy_v2");
#undef CUDA_EXPORT
            }
        };
    }

    struct PlanarInput::State
    {
        struct Slot
        {
            ID3D11Texture2D* texture = nullptr;
            Handle resource = nullptr, event = nullptr;
            Pointer pointer = 0;
            std::size_t pitch = 0;
            ULONGLONG began = 0;
            bool mapped = false, pending = false, copied = false;
        };
        Api api;
        HMODULE module = nullptr;
        Handle context = nullptr, stream = nullptr;
        Device device = 0;
        DWORD owner = GetCurrentThreadId();
        UINT width = 0, height = 0, timeout = 0;
        std::array<Slot, Slots> slots{};
        bool entered = false, quarantined = false;
        bool Push() noexcept
        {
            if (entered || !context || quarantined) return false;
            if (api.pushContext(context) != Success) { quarantined = true; return false; }
            entered = true;
            return true;
        }
        bool Pop() noexcept
        {
            if (!entered) return false;
            Handle previous = nullptr;
            const auto result = api.popContext(&previous);
            entered = false;
            if (result != Success || previous != context) { quarantined = true; return false; }
            return true;
        }
        struct Scope
        {
            State& state;
            bool active = true;
            explicit Scope(State& value) : state(value)
            { Require(state.Push(), "cuda_context_enter_failed", E_FAIL); }
            ~Scope() { if (active) state.Pop(); }
            void End()
            {
                active = false;
                Require(state.Pop(), "cuda_context_restore_failed", E_FAIL);
            }
        };
    };

    bool PlanarInput::Fail(const char* reason, HRESULT error) noexcept
    { reason_ = reason; error_ = error; return false; }

    bool PlanarInput::Guard() noexcept
    {
        if (!state_ || !state_->context) return Fail("cuda_not_initialized", E_UNEXPECTED);
        if (state_->owner != GetCurrentThreadId()) return Fail("cuda_wrong_owner_thread", RPC_E_WRONG_THREAD);
        if (state_->quarantined) return Fail("cuda_resources_quarantined", E_FAIL);
        if (state_->entered) return Fail("cuda_context_scope_already_entered", E_UNEXPECTED);
        return true;
    }

    PlanarInput::~PlanarInput()
    {
        // On failure the State, texture references, module and CUDA allocations
        // deliberately survive this object. Only helper-process exit reclaims
        // potentially driver-owned resources safely.
        Close();
    }

    bool PlanarInput::Initialize(IDXGIAdapter* adapter, UINT width, UINT height, UINT timeoutMs) noexcept
    {
        if (state_) return Fail("cuda_already_initialized", E_UNEXPECTED);
        if (!adapter || width == 0 || height == 0 || width > 3840 || height > 2160 ||
            timeoutMs < 100 || timeoutMs > 10000) return Fail("cuda_configuration_invalid", E_INVALIDARG);
        state_ = new(std::nothrow) State();
        if (!state_) return Fail("cuda_state_allocation_failed", E_OUTOFMEMORY);
        auto& value = *state_;
        value.width = width; value.height = height; value.timeout = timeoutMs;
        try
        {
            value.module = LoadLibraryExW(L"nvcuda.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            Require(value.module != nullptr, "cuda_driver_unavailable", HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND));
            value.api.Load(value.module);
            Check(value.api.init(0), "cuda_driver_initialization_failed");
            Check(value.api.getDevice(&value.device, adapter), "cuda_adapter_unsupported");
            Handle context = nullptr;
            Check(value.api.createContext(&context, 0, value.device), "cuda_context_creation_failed");
            value.context = context;
            // cuCtxCreate pushes the new context; restore the caller immediately.
            value.entered = true;
            Require(value.Pop(), "cuda_context_restore_failed", E_FAIL);
            State::Scope scope(value);
            Handle stream = nullptr;
            Check(value.api.createStream(&stream, 1), "cuda_stream_creation_failed"); // NON_BLOCKING
            value.stream = stream;
            scope.End();
            reason_ = "cuda_initialized"; error_ = S_OK;
            return true;
        }
        catch (const Failure& failure) { Fail(failure.reason, failure.error); }
        catch (...) { Fail("cuda_initialization_failed", E_FAIL); }
        Close();
        return false;
    }

    bool PlanarInput::Register(UINT index, ID3D11Texture2D* texture) noexcept
    {
        if (!Guard()) return false;
        if (index >= Slots || !texture || state_->slots[index].texture)
            return Fail("cuda_slot_registration_invalid", E_INVALIDARG);
        auto& value = *state_;
        auto& slot = value.slots[index];
        try
        {
            D3D11_TEXTURE2D_DESC description{};
            texture->GetDesc(&description);
            Require(description.Width == value.width && description.Height == value.height * 3 &&
                description.Format == DXGI_FORMAT_R16_UINT && description.MipLevels == 1 && description.ArraySize == 1 &&
                description.SampleDesc.Count == 1 && description.SampleDesc.Quality == 0 &&
                description.Usage == D3D11_USAGE_DEFAULT && description.CPUAccessFlags == 0,
                "cuda_planar_texture_invalid");
            Microsoft::WRL::ComPtr<ID3D11Device> device;
            Microsoft::WRL::ComPtr<IDXGIDevice> dxgi;
            Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
            texture->GetDevice(&device);
            Require(SUCCEEDED(device.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)), "cuda_texture_adapter_failed");
            State::Scope scope(value);
            Device actual = 0;
            Check(value.api.getDevice(&actual, adapter.Get()), "cuda_texture_device_failed");
            Require(actual == value.device, "cuda_texture_adapter_mismatch");
            slot.texture = texture; texture->AddRef();
            Pointer pointer = 0;
            std::size_t pitch = 0;
            Check(value.api.allocate(&pointer, &pitch, static_cast<std::size_t>(value.width) * 2,
                static_cast<std::size_t>(value.height) * 3, 16), "cuda_planar_allocation_failed");
            slot.pointer = pointer; slot.pitch = pitch;
            Require(slot.pitch >= static_cast<std::size_t>(value.width) * 2 && slot.pitch % 4 == 0 &&
                slot.pitch <= (std::numeric_limits<UINT>::max)() &&
                slot.pitch <= MaximumSlotBytes / (static_cast<std::size_t>(value.height) * 3), "cuda_pitch_out_of_bounds");
            Handle resource = nullptr, event = nullptr;
            Check(value.api.registerTexture(&resource, texture, 0), "cuda_texture_registration_failed");
            slot.resource = resource;
            Check(value.api.createEvent(&event, 2), "cuda_event_creation_failed"); // DISABLE_TIMING
            slot.event = event;
            scope.End();
            reason_ = "cuda_slot_registered"; error_ = S_OK;
            return true;
        }
        catch (const Failure& failure) { return Fail(failure.reason, failure.error); }
        catch (...) { return Fail("cuda_slot_registration_failed", E_FAIL); }
    }

    bool PlanarInput::BeginCopy(UINT index) noexcept
    {
        if (!Guard()) return false;
        if (index >= Slots || !state_->slots[index].event || state_->slots[index].pending)
            return Fail("cuda_copy_slot_invalid", E_INVALIDARG);
        auto& value = *state_;
        auto& slot = value.slots[index];
        try
        {
            State::Scope scope(value);
            slot.began = GetTickCount64(); slot.copied = false;
            Check(value.api.map(1, &slot.resource, value.stream), "cuda_texture_map_failed");
            slot.mapped = true;
            Handle mapped = nullptr;
            Check(value.api.mappedArray(&mapped, slot.resource, 0, 0), "cuda_array_lookup_failed");
            Copy2D copy{};
            copy.srcMemoryType = MemoryType::Array; copy.srcArray = mapped;
            copy.dstMemoryType = MemoryType::Device; copy.dstDevice = slot.pointer; copy.dstPitch = slot.pitch;
            copy.WidthInBytes = static_cast<std::size_t>(value.width) * 2;
            copy.Height = static_cast<std::size_t>(value.height) * 3;
            Check(value.api.copy(&copy, value.stream), "cuda_planar_copy_failed");
            Check(value.api.unmap(1, &slot.resource, value.stream), "cuda_texture_unmap_failed");
            slot.mapped = false; slot.pending = true;
            Check(value.api.recordEvent(slot.event, value.stream), "cuda_copy_event_failed");
            scope.End();
            reason_ = "cuda_copy_pending"; error_ = S_OK;
            return true;
        }
        catch (const Failure& failure) { value.quarantined = true; return Fail(failure.reason, failure.error); }
        catch (...) { value.quarantined = true; return Fail("cuda_copy_failed", E_FAIL); }
    }

    bool PlanarInput::IsCopyReady(UINT index, bool& ready) noexcept
    {
        ready = false;
        if (!Guard()) return false;
        if (index >= Slots || !state_->slots[index].event)
            return Fail("cuda_copy_query_invalid", E_INVALIDARG);
        auto& value = *state_;
        auto& slot = value.slots[index];
        if (!slot.pending) { ready = slot.copied; return true; }
        try
        {
            State::Scope scope(value);
            const auto status = value.api.queryEvent(slot.event);
            Require(status == Success || status == NotReady, "cuda_copy_query_failed", E_FAIL);
            if (status == Success) { slot.pending = false; slot.copied = true; ready = true; }
            scope.End();
            if (!ready && GetTickCount64() - slot.began >= value.timeout)
                return Fail("cuda_copy_timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            return true;
        }
        catch (const Failure& failure) { value.quarantined = true; return Fail(failure.reason, failure.error); }
        catch (...) { value.quarantined = true; return Fail("cuda_copy_query_failed", E_FAIL); }
    }

    HRESULT PlanarInput::Enter() noexcept
    {
        if (!Guard()) return error_;
        if (!state_->Push()) { Fail("cuda_context_enter_failed", E_FAIL); return error_; }
        return S_OK;
    }

    HRESULT PlanarInput::Leave() noexcept
    {
        if (!state_ || state_->owner != GetCurrentThreadId() || !state_->entered)
        { Fail("cuda_context_leave_invalid", E_UNEXPECTED); return error_; }
        if (!state_->Pop()) { Fail("cuda_context_restore_failed", E_FAIL); return error_; }
        return S_OK;
    }

    void* PlanarInput::Context() const noexcept
    { return state_ && state_->owner == GetCurrentThreadId() && !state_->quarantined ? state_->context : nullptr; }
    std::uint64_t PlanarInput::DevicePointer(UINT index) const noexcept
    {
        return state_ && state_->owner == GetCurrentThreadId() && !state_->quarantined && index < Slots &&
            state_->slots[index].event ? state_->slots[index].pointer : 0;
    }
    UINT PlanarInput::Pitch(UINT index) const noexcept
    {
        return state_ && state_->owner == GetCurrentThreadId() && !state_->quarantined && index < Slots &&
            state_->slots[index].event ? static_cast<UINT>(state_->slots[index].pitch) : 0;
    }

    HRESULT PlanarInput::Close() noexcept
    {
        if (!state_) return S_OK;
        auto& value = *state_;
        if (value.owner != GetCurrentThreadId())
        { Fail("cuda_wrong_owner_thread", RPC_E_WRONG_THREAD); return error_; }
        if (value.quarantined || value.entered)
        { Fail("cuda_cleanup_requires_process_exit", E_FAIL); return error_; }
        try
        {
            if (value.context)
            {
                State::Scope scope(value);
                const auto began = GetTickCount64();
                for (;;)
                {
                    bool pending = false;
                    for (auto& slot : value.slots)
                    {
                        if (!slot.pending) continue;
                        const auto status = value.api.queryEvent(slot.event);
                        Require(status == Success || status == NotReady, "cuda_cleanup_query_failed", E_FAIL);
                        if (status == Success) slot.pending = false;
                        else pending = true;
                    }
                    if (!pending) break;
                    Require(GetTickCount64() - began < value.timeout, "cuda_cleanup_timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                    Sleep(1);
                }
                for (auto& slot : value.slots)
                {
                    Require(!slot.mapped, "cuda_cleanup_resource_still_mapped", E_FAIL);
                    if (slot.event) { Check(value.api.destroyEvent(slot.event), "cuda_event_destroy_failed"); slot.event = nullptr; }
                    if (slot.resource) { Check(value.api.unregisterTexture(slot.resource), "cuda_texture_unregister_failed"); slot.resource = nullptr; }
                    if (slot.pointer) { Check(value.api.free(slot.pointer), "cuda_memory_free_failed"); slot.pointer = 0; }
                    if (slot.texture) { slot.texture->Release(); slot.texture = nullptr; }
                }
                if (value.stream) { Check(value.api.destroyStream(value.stream), "cuda_stream_destroy_failed"); value.stream = nullptr; }
                scope.End();
                Check(value.api.destroyContext(value.context), "cuda_context_destroy_failed");
                value.context = nullptr;
            }
            if (value.module) FreeLibrary(value.module);
            delete state_; state_ = nullptr;
            return S_OK;
        }
        catch (const Failure& failure) { value.quarantined = true; Fail(failure.reason, failure.error); }
        catch (...) { value.quarantined = true; Fail("cuda_cleanup_failed", E_FAIL); }
        return error_;
    }
}
