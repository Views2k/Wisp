// Diagnostic link replacement only. The production capture implementation is
// neither compiled nor changed by this fixture. No screen/window APIs are used.
#include "GameScreenCapture.h"
#include <d3d10.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>

#if !defined(WISP_SYNTHETIC_HOST_FIXTURE)
#error This source belongs only to the explicit synthetic host diagnostic.
#endif

namespace recorder::capture
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        constexpr UINT Width = 3840, Height = 2160;
        UINT DetailCellSize = 0; // Configured before the host thread starts.
        std::atomic<std::uint64_t> CapturedFrames{0};
        constexpr char Shader[] = R"(
cbuffer Clock : register(b0) { uint frame; uint highOutput; uint cellSize; uint padding; };
float4 VS(uint id : SV_VertexID) : SV_POSITION {
    return float4(id == 1 ? 3 : -1, id == 2 ? -3 : 1, 0, 1);
}
float4 PS(float4 p : SV_POSITION) : SV_TARGET {
    uint2 q = uint2(p.xy);
    uint x = (q.x + frame * 3) % 3840;
    uint y = (q.y + frame) % 2160;
    uint h = ((x / 8) * 73856093u) ^ ((y / 8) * 19349663u);
    if (highOutput != 0) {
        uint side = cellSize == 8 ? 8u : 2u;
        h = ((q.x / side) * 73856093u) ^ ((q.y / side) * 19349663u) ^ (frame * 83492791u);
        h = (h ^ (h >> 16)) * 2246822519u;
    }
    h ^= h >> 13;
    float3 detail = float3(h & 255u, (h >> 8) & 255u, (h >> 16) & 255u) / 255.0;
    float3 gradient = float3(float(x) / 3840.0, float(y) / 2160.0, 0.35);
    float lines = ((x % 192) < 3 || (y % 120) < 3) ? 0.2 : 0;
    return float4(saturate(gradient * 0.65 + detail * 0.35 + lines), 1);
})";
        void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
        struct ContextLock
        {
            ID3D10Multithread* value;
            explicit ContextLock(ID3D10Multithread* p) : value(p) { value->Enter(); }
            ~ContextLock() { value->Leave(); }
        };
    }
    struct GameScreenCapture::Impl
    {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<ID3D10Multithread> multithread;
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11RenderTargetView> target;
        ComPtr<ID3D11VertexShader> vertex;
        ComPtr<ID3D11PixelShader> pixel;
        ComPtr<ID3D11Buffer> clock;
        FrameInfo frame{};
        TargetIdentity identity{};
        std::uint64_t frequency = 0, began = 0, lastIndex = UINT64_MAX;
    };
    GameScreenCapture::GameScreenCapture() noexcept = default;
    GameScreenCapture::~GameScreenCapture() { (void)Close(); }
    bool GameScreenCapture::Fail(const char* reason, HRESULT hr) noexcept
    { evidence_.reason = reason; evidence_.hr = hr; return false; }
    void ConfigureSyntheticPattern(UINT cellSize) noexcept { DetailCellSize = cellSize; }
    std::uint64_t SyntheticFrameCount() noexcept { return CapturedFrames.load(); }

    bool GameScreenCapture::Initialize(const TargetIdentity& identity, const Options& options) noexcept
    {
        const char* stage = "fixture_identity_failed";
        try
        {
            FILETIME creation{}, exit{}, kernel{}, user{};
            if (impl_ || closed_ || identity.processId != GetCurrentProcessId() ||
                identity.window != reinterpret_cast<HWND>(static_cast<UINT_PTR>(1)) || options.frameRate != 60 ||
                !GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user) ||
                ((static_cast<std::uint64_t>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime) != identity.creationTime)
                return Fail("fixture_identity_invalid", E_INVALIDARG);
            stage = "fixture_allocation_failed";
            impl_ = std::make_unique<Impl>();
            auto& v = *impl_;
            v.identity = identity;
            LARGE_INTEGER frequency{};
            if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) return Fail("fixture_clock_failed", E_FAIL);
            v.frequency = static_cast<std::uint64_t>(frequency.QuadPart);
            const D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_0 };
            stage = "fixture_device_creation_failed";
            Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, 1, D3D11_SDK_VERSION, &v.device, nullptr, &v.context));
            stage = "fixture_device_lock_query_failed";
            Check(v.device.As(&v.multithread));
            (void)v.multithread->SetMultithreadProtected(TRUE);
            if (!v.multithread->GetMultithreadProtected()) return Fail("fixture_device_lock_failed", E_FAIL);
            D3D11_TEXTURE2D_DESC texture{};
            texture.Width = Width; texture.Height = Height; texture.MipLevels = texture.ArraySize = texture.SampleDesc.Count = 1;
            texture.Format = DXGI_FORMAT_B8G8R8A8_UNORM; texture.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            stage = "fixture_texture_creation_failed";
            Check(v.device->CreateTexture2D(&texture, nullptr, &v.texture));
            stage = "fixture_render_target_creation_failed";
            Check(v.device->CreateRenderTargetView(v.texture.Get(), nullptr, &v.target));
            ComPtr<ID3DBlob> vs, ps, errors;
            stage = "fixture_vertex_compile_failed";
            Check(D3DCompile(Shader, sizeof(Shader) - 1, nullptr, nullptr, nullptr, "VS", "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &vs, &errors));
            errors.Reset();
            stage = "fixture_pixel_compile_failed";
            Check(D3DCompile(Shader, sizeof(Shader) - 1, nullptr, nullptr, nullptr, "PS", "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &ps, &errors));
            stage = "fixture_vertex_creation_failed";
            Check(v.device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &v.vertex));
            stage = "fixture_pixel_creation_failed";
            Check(v.device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &v.pixel));
            D3D11_BUFFER_DESC buffer{}; buffer.ByteWidth = 16; buffer.Usage = D3D11_USAGE_DEFAULT; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            stage = "fixture_clock_buffer_creation_failed";
            Check(v.device->CreateBuffer(&buffer, nullptr, &v.clock));
            source_ = { Width, Height, DXGI_FORMAT_B8G8R8A8_UNORM, SourceEncoding::SrgbBgra8,
                DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, false, false, 0 };
            evidence_.initialized = true; evidence_.source = source_; evidence_.reason = "fixture_initialized";
            return true;
        }
        catch (HRESULT hr) { return Fail(stage, hr); }
        catch (...) { return Fail(stage, E_FAIL); }
    }
    bool GameScreenCapture::Start() noexcept
    {
        if (!impl_ || closed_ || !evidence_.initialized || evidence_.started) return Fail("fixture_start_invalid", E_UNEXPECTED);
        evidence_.started = true; return CheckTarget();
    }
    bool GameScreenCapture::CheckTarget() noexcept
    {
        if (!impl_ || closed_) return Fail("fixture_closed", E_UNEXPECTED);
#if defined(WISP_CAPTURE_PAUSE_API)
        if (paused_) return false;
#endif
        if (!evidence_.started) return true;
        LARGE_INTEGER now{};
        if (!QueryPerformanceCounter(&now) || now.QuadPart <= 0) return Fail("fixture_clock_failed", E_FAIL);
        auto& v = *impl_;
        const auto ticks = static_cast<std::uint64_t>(now.QuadPart);
        if (!v.began) v.began = ticks;
        const auto index = (ticks - v.began) * 60 / v.frequency;
        if (index == v.lastIndex) return true;
        ContextLock lock(v.multithread.Get());
        const std::array<UINT, 4> values{ static_cast<UINT>(index), DetailCellSize ? 1u : 0u, DetailCellSize, 0 };
        v.context->UpdateSubresource(v.clock.Get(), 0, nullptr, values.data(), 0, 0);
        ID3D11Buffer* buffer = v.clock.Get();
        v.context->PSSetConstantBuffers(0, 1, &buffer);
        v.context->IASetInputLayout(nullptr); v.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        v.context->VSSetShader(v.vertex.Get(), nullptr, 0); v.context->PSSetShader(v.pixel.Get(), nullptr, 0);
        v.context->RSSetState(nullptr); v.context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        ID3D11RenderTargetView* target = v.target.Get();
        v.context->OMSetRenderTargets(1, &target, nullptr);
        const D3D11_VIEWPORT viewport{ 0, 0, static_cast<float>(Width), static_cast<float>(Height), 0, 1 };
        v.context->RSSetViewports(1, &viewport); v.context->Draw(3, 0);
        v.context->OMSetRenderTargets(0, nullptr, nullptr);
        const auto hr = v.device->GetDeviceRemovedReason();
        if (FAILED(hr)) return Fail("fixture_device_removed", hr);
        v.lastIndex = index;
        const auto timestamp = ticks / v.frequency * 10000000 + ticks % v.frequency * 10000000 / v.frequency;
        v.frame = { ++evidence_.copiedFrames, static_cast<LONGLONG>(timestamp), static_cast<LONGLONG>(timestamp), ticks };
        CapturedFrames.fetch_add(1);
        return true;
    }
    ID3D11Device* GameScreenCapture::Device() const noexcept { return impl_ ? impl_->device.Get() : nullptr; }
    const SourceDescription& GameScreenCapture::Source() const noexcept { return source_; }
    HRESULT GameScreenCapture::SubmitLatestLocked(ID3D11Texture2D* destination, FrameConsumer& consumer, FrameInfo& frame) noexcept
    {
#if defined(WISP_CAPTURE_PAUSE_API)
        if (paused_) return S_FALSE;
#endif
        if (!impl_ || closed_ || !evidence_.started || !evidence_.copiedFrames || !destination) return E_UNEXPECTED;
        frame = impl_->frame; return consumer.Submit(impl_->texture.Get(), destination);
    }
    Evidence GameScreenCapture::Result() const noexcept { return evidence_; }
#if defined(WISP_CAPTURE_PAUSE_API)
    bool GameScreenCapture::Pause() noexcept
    {
        if (!impl_ || closed_) return false;
        paused_ = true; impl_->frame = {}; evidence_.reason = "target_focus_lost"; evidence_.hr = S_FALSE;
        return true;
    }
    bool GameScreenCapture::Resume(const TargetIdentity& target) noexcept
    {
        if (!impl_ || closed_ || !paused_ || target.window != impl_->identity.window ||
            target.processId != impl_->identity.processId || target.creationTime != impl_->identity.creationTime) return false;
        paused_ = false; impl_->lastIndex = UINT64_MAX; evidence_.reason = "fixture_resumed"; evidence_.hr = S_OK;
        return true;
    }
    bool GameScreenCapture::HasFrame() const noexcept
    { return impl_ && !closed_ && !paused_ && impl_->frame.version != 0; }
    bool GameScreenCapture::CheckIdentity() noexcept
    { return impl_ && !closed_ && impl_->identity.processId == GetCurrentProcessId(); }
    bool GameScreenCapture::IsForeground() const noexcept
    { return impl_ && !closed_ && !paused_; }
    HWND GameScreenCapture::TargetWindow() const noexcept { return nullptr; } // Explicit no-window/self-audio fixture.
#endif
    HRESULT GameScreenCapture::Close() noexcept
    {
        if (closed_) return evidence_.cleanupHr;
        closed_ = true; evidence_.stopped = true; evidence_.callbacksDrained = true;
        impl_.reset(); return S_OK;
    }

    // Compiler-only one-variable regression. No device, capture, file or audio
    // initialization. Error blobs are discarded; only HRESULTs are reported.
    int RunSyntheticShaderCheck() noexcept
    {
        try
        {
            std::string previous = Shader;
            std::size_t replacements = 0;
            for (auto at = previous.find("detail"); at != std::string::npos; at = previous.find("detail", at + 7))
            { previous.replace(at, 6, "texture"); ++replacements; }
            const auto compile = [](const char* source, std::size_t length, const char* entry, const char* profile)
            {
                ComPtr<ID3DBlob> code, errors;
                return D3DCompile(source, length, nullptr, nullptr, nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
            };
            const HRESULT beforeVs = compile(previous.data(), previous.size(), "VS", "vs_5_0");
            const HRESULT beforePs = compile(previous.data(), previous.size(), "PS", "ps_5_0");
            const HRESULT afterVs = compile(Shader, sizeof(Shader) - 1, "VS", "vs_5_0");
            const HRESULT afterPs = compile(Shader, sizeof(Shader) - 1, "PS", "ps_5_0");
            const bool confirmed = replacements == 2 && FAILED(beforeVs) && FAILED(beforePs) && SUCCEEDED(afterVs) && SUCCEEDED(afterPs);
            std::printf("{\"mode\":\"synthetic_shader_check\",\"identifierOnly\":%s,\"beforeVertexHr\":%lu,\"beforePixelHr\":%lu,\"afterVertexHr\":%lu,\"afterPixelHr\":%lu,\"confirmed\":%s,\"graphicsActivated\":false,\"audioActivated\":false}\n",
                replacements == 2 ? "true" : "false", static_cast<unsigned long>(beforeVs), static_cast<unsigned long>(beforePs),
                static_cast<unsigned long>(afterVs), static_cast<unsigned long>(afterPs), confirmed ? "true" : "false");
            return confirmed ? 0 : 3;
        }
        catch (...) { std::puts("{\"mode\":\"synthetic_shader_check\",\"confirmed\":false,\"reason\":\"fixture_shader_check_failed\"}"); return 3; }
    }
}
