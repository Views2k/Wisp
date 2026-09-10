#include "Wisp.NativeRenderer.h"
#include <d3d11.h>
#include <dxgi1_3.h>
#include <dcomp.h>
#include <wrl/client.h>
#include <array>
#include <cmath>
#include <cstring>
#include <memory>
#include <new>
#include <unordered_map>
#include "QuadVertex.h"
#include "ImagePixel.h"
#include "DialPixel.h"
#include "NeedlePixel.h"

using Microsoft::WRL::ComPtr;
#define CHECK_HR(expression) do { const HRESULT checkedResult = (expression); if (FAILED(checkedResult)) return checkedResult; } while (false)

namespace
{
    // Keep one additional frame in flight so drawing can overlap presentation.
    // Present remains synchronized, and the waitable queue still bounds the work.
    constexpr UINT SwapChainBufferCount = 3;
    constexpr UINT MaximumFrameLatency = 2;

    bool ValidSize(uint32_t width, uint32_t height) noexcept
    {
        return width > 0 && height > 0 && width <= 8192 && height <= 8192;
    }

    struct Constants
    {
        float transformX[4], transformY[4], uv[4], tint[4], material[4];
    };
    static_assert(sizeof(Constants) == 80, "Shader constant layout mismatch.");

    int64_t Counter() noexcept
    {
        LARGE_INTEGER value{};
        QueryPerformanceCounter(&value);
        return value.QuadPart;
    }

    class CpuTimer final
    {
        int64_t* destination;
        int64_t started;
    public:
        explicit CpuTimer(int64_t* ticks) noexcept : destination(ticks), started(ticks ? Counter() : 0) { }
        ~CpuTimer() { if (destination) *destination = Counter() - started; }
        int64_t Started() const noexcept { return started; }
    };

    int64_t ThreadCpuTime100ns() noexcept
    {
        FILETIME created{}, exited{}, kernel{}, user{};
        if (!GetThreadTimes(GetCurrentThread(), &created, &exited, &kernel, &user)) return -1;
        const uint64_t kernelTime = (static_cast<uint64_t>(kernel.dwHighDateTime) << 32) | kernel.dwLowDateTime;
        const uint64_t userTime = (static_cast<uint64_t>(user.dwHighDateTime) << 32) | user.dwLowDateTime;
        return static_cast<int64_t>(kernelTime + userTime);
    }

    class ThreadCpuTimer final
    {
        int64_t* destination;
        int64_t started;
    public:
        explicit ThreadCpuTimer(int64_t* time) noexcept
            : destination(time), started(time ? ThreadCpuTime100ns() : -1) { }
        ~ThreadCpuTimer()
        {
            if (!destination || started < 0) return;
            const int64_t ended = ThreadCpuTime100ns();
            if (ended >= started) *destination = ended - started;
        }
    };

    HRESULT PresentationResult(HRESULT result) noexcept
    {
        if (result == DXGI_ERROR_WAS_STILL_DRAWING) return S_FALSE;
        return result == DXGI_STATUS_OCCLUDED ? static_cast<HRESULT>(2) : result;
    }

    class Renderer final
    {
    public:
        DWORD threadId = GetCurrentThreadId();
        HWND hwnd = nullptr;
        uint32_t width = 0, height = 0;
        uint32_t swapChainGeneration = 1;
        HANDLE latency = nullptr;
        bool captureReady = false;
        bool visible = false;
        float opacity = 1.0f;
        bool hasDrawn = false;
        bool pendingPresentation = false;
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<IDXGISwapChain2> swapChain;
        ComPtr<IDCompositionDevice> composition;
        ComPtr<IDCompositionTarget> target;
        ComPtr<IDCompositionVisual> visual;
        ComPtr<IDCompositionEffectGroup> opacityEffect;
        ComPtr<ID3D11RenderTargetView> renderTarget;
        ComPtr<ID3D11Buffer> vertices, constants;
        ComPtr<ID3D11VertexShader> vertexShader;
        std::array<ComPtr<ID3D11PixelShader>, 3> pixelShaders;
        ComPtr<ID3D11InputLayout> inputLayout;
        ComPtr<ID3D11SamplerState> sampler;
        ComPtr<ID3D11BlendState> blend;
        ComPtr<ID3D11RasterizerState> rasterizer;
        std::unordered_map<uint32_t, ComPtr<ID3D11ShaderResourceView>> textures;

        ~Renderer()
        {
            if (target) { target->SetRoot(nullptr); }
            if (visual) { visual->SetContent(nullptr); }
            if (composition) { composition->Commit(); }
            if (context) { context->ClearState(); }
            if (latency) { CloseHandle(latency); }
        }

        HRESULT CheckThread() const noexcept
        {
            return threadId == GetCurrentThreadId() ? S_OK : RPC_E_WRONG_THREAD;
        }

        HRESULT CreateTarget()
        {
            ComPtr<ID3D11Texture2D> buffer;
            CHECK_HR(swapChain->GetBuffer(0, IID_PPV_ARGS(&buffer)));
            return device->CreateRenderTargetView(buffer.Get(), nullptr, &renderTarget);
        }

        HRESULT Initialize(HWND window, uint32_t targetWidth, uint32_t targetHeight)
        {
            DWORD process = 0;
            if (!IsWindow(window) || !GetWindowThreadProcessId(window, &process) || process != GetCurrentProcessId()
                || !ValidSize(targetWidth, targetHeight)) return E_INVALIDARG;
            hwnd = window; width = targetWidth; height = targetHeight;
            const D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 };
            D3D_FEATURE_LEVEL obtained{};
            // Hardware failure is returned to Wisp; silently switching to WARP would hide a regression.
            CHECK_HR(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                requested, ARRAYSIZE(requested), D3D11_SDK_VERSION, &device, &obtained, &context));
            ComPtr<IDXGIDevice> dxgiDevice;
            ComPtr<IDXGIAdapter> adapter;
            ComPtr<IDXGIFactory2> factory;
            CHECK_HR(device.As(&dxgiDevice));
            CHECK_HR(dxgiDevice->GetAdapter(&adapter));
            CHECK_HR(adapter->GetParent(IID_PPV_ARGS(&factory)));
            DXGI_SWAP_CHAIN_DESC1 description{};
            description.Width = width; description.Height = height;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            description.BufferCount = SwapChainBufferCount;
            description.Scaling = DXGI_SCALING_STRETCH;
            description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            description.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
            description.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
            ComPtr<IDXGISwapChain1> chain;
            CHECK_HR(factory->CreateSwapChainForComposition(device.Get(), &description, nullptr, &chain));
            CHECK_HR(chain.As(&swapChain));
            CHECK_HR(swapChain->SetMaximumFrameLatency(MaximumFrameLatency));
            latency = swapChain->GetFrameLatencyWaitableObject();
            if (!latency) return HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE);
            CHECK_HR(CreateTarget());
            CHECK_HR(DCompositionCreateDevice(dxgiDevice.Get(), IID_PPV_ARGS(&composition)));
            CHECK_HR(composition->CreateTargetForHwnd(hwnd, TRUE, &target));
            CHECK_HR(composition->CreateVisual(&visual));
            CHECK_HR(composition->CreateEffectGroup(&opacityEffect));
            CHECK_HR(opacityEffect->SetOpacity(0.0f));
            CHECK_HR(visual->SetEffect(opacityEffect.Get()));
            CHECK_HR(visual->SetContent(swapChain.Get()));
            CHECK_HR(target->SetRoot(visual.Get()));

            const float quad[][2] = { {0,0}, {1,0}, {0,1}, {0,1}, {1,0}, {1,1} };
            D3D11_BUFFER_DESC vertexDescription{};
            vertexDescription.ByteWidth = sizeof(quad);
            vertexDescription.Usage = D3D11_USAGE_IMMUTABLE;
            vertexDescription.BindFlags = D3D11_BIND_VERTEX_BUFFER;
            D3D11_SUBRESOURCE_DATA initial{}; initial.pSysMem = quad;
            CHECK_HR(device->CreateBuffer(&vertexDescription, &initial, &vertices));
            D3D11_BUFFER_DESC constantDescription{};
            constantDescription.ByteWidth = sizeof(Constants);
            constantDescription.Usage = D3D11_USAGE_DYNAMIC;
            constantDescription.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            constantDescription.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            CHECK_HR(device->CreateBuffer(&constantDescription, nullptr, &constants));
            CHECK_HR(device->CreateVertexShader(QuadVertex, sizeof(QuadVertex), nullptr, &vertexShader));
            const D3D11_INPUT_ELEMENT_DESC element{ "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 };
            CHECK_HR(device->CreateInputLayout(&element, 1, QuadVertex, sizeof(QuadVertex), &inputLayout));
            CHECK_HR(device->CreatePixelShader(ImagePixel, sizeof(ImagePixel), nullptr, &pixelShaders[0]));
            CHECK_HR(device->CreatePixelShader(DialPixel, sizeof(DialPixel), nullptr, &pixelShaders[1]));
            CHECK_HR(device->CreatePixelShader(NeedlePixel, sizeof(NeedlePixel), nullptr, &pixelShaders[2]));
            D3D11_SAMPLER_DESC sample{};
            sample.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
            sample.AddressU = sample.AddressV = sample.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            sample.MaxLOD = D3D11_FLOAT32_MAX;
            CHECK_HR(device->CreateSamplerState(&sample, &sampler));
            D3D11_BLEND_DESC blendDescription{};
            auto& blending = blendDescription.RenderTarget[0];
            blending.BlendEnable = TRUE;
            blending.SrcBlend = blending.SrcBlendAlpha = D3D11_BLEND_ONE;
            blending.DestBlend = blending.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
            blending.BlendOp = blending.BlendOpAlpha = D3D11_BLEND_OP_ADD;
            blending.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
            CHECK_HR(device->CreateBlendState(&blendDescription, &blend));
            D3D11_RASTERIZER_DESC rasterDescription{};
            rasterDescription.FillMode = D3D11_FILL_SOLID;
            rasterDescription.CullMode = D3D11_CULL_NONE;
            rasterDescription.DepthClipEnable = TRUE;
            CHECK_HR(device->CreateRasterizerState(&rasterDescription, &rasterizer));
            const uint8_t white[] = {255,255,255,255};
            CHECK_HR(Upload(0, 1, 1, 4, white, sizeof(white)));
            const float clear[4]{};
            context->ClearRenderTargetView(renderTarget.Get(), clear);
            const HRESULT presented = swapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
            if (FAILED(presented) && presented != DXGI_ERROR_WAS_STILL_DRAWING) return presented;
            return composition->Commit();
        }

        HRESULT PrepareForResume()
        {
            CHECK_HR(CheckThread());
            if (visible) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
            pendingPresentation = false;
            if (!hasDrawn) return S_FALSE;
            ComPtr<IDXGIDevice> dxgiDevice;
            ComPtr<IDXGIAdapter> adapter;
            ComPtr<IDXGIFactory2> factory;
            CHECK_HR(device.As(&dxgiDevice));
            CHECK_HR(dxgiDevice->GetAdapter(&adapter));
            CHECK_HR(adapter->GetParent(IID_PPV_ARGS(&factory)));
            DXGI_SWAP_CHAIN_DESC1 description{};
            CHECK_HR(swapChain->GetDesc1(&description));
            ComPtr<IDXGISwapChain1> created;
            ComPtr<IDXGISwapChain2> replacement;
            CHECK_HR(factory->CreateSwapChainForComposition(device.Get(), &description, nullptr, &created));
            CHECK_HR(created.As(&replacement));
            CHECK_HR(replacement->SetMaximumFrameLatency(MaximumFrameLatency));
            HANDLE replacementLatency = replacement->GetFrameLatencyWaitableObject();
            if (!replacementLatency) return HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE);
            ComPtr<ID3D11Texture2D> buffer;
            ComPtr<ID3D11RenderTargetView> replacementTarget;
            HRESULT status = replacement->GetBuffer(0, IID_PPV_ARGS(&buffer));
            if (SUCCEEDED(status)) status = device->CreateRenderTargetView(buffer.Get(), nullptr, &replacementTarget);
            if (FAILED(status)) { CloseHandle(replacementLatency); return status; }
            const float clear[4]{};
            context->ClearRenderTargetView(replacementTarget.Get(), clear);
            status = replacement->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
            if (FAILED(status) && status != DXGI_ERROR_WAS_STILL_DRAWING) { CloseHandle(replacementLatency); return status; }
            status = visual->SetContent(replacement.Get());
            if (SUCCEEDED(status)) status = composition->Commit();
            if (FAILED(status)) { CloseHandle(replacementLatency); return status; }
            context->OMSetRenderTargets(0, nullptr, nullptr);
            renderTarget = std::move(replacementTarget);
            swapChain = std::move(replacement);
            CloseHandle(latency);
            latency = replacementLatency;
            captureReady = true;
            hasDrawn = false;
            ++swapChainGeneration;
            return S_OK;
        }

        HRESULT Resize(uint32_t targetWidth, uint32_t targetHeight, float offsetX, float offsetY)
        {
            CHECK_HR(CheckThread());
            if (!ValidSize(targetWidth, targetHeight) || !std::isfinite(offsetX) || !std::isfinite(offsetY)) return E_INVALIDARG;
            captureReady = false;
            pendingPresentation = false;
            if (width != targetWidth || height != targetHeight)
            {
                context->OMSetRenderTargets(0, nullptr, nullptr);
                renderTarget.Reset();
                CHECK_HR(swapChain->ResizeBuffers(SwapChainBufferCount, targetWidth, targetHeight, DXGI_FORMAT_B8G8R8A8_UNORM,
                    DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT));
                width = targetWidth; height = targetHeight;
                CHECK_HR(CreateTarget());
            }
            CHECK_HR(visual->SetOffsetX(offsetX));
            CHECK_HR(visual->SetOffsetY(offsetY));
            return composition->Commit();
        }

        HRESULT Upload(uint32_t id, uint32_t textureWidth, uint32_t textureHeight, uint32_t stride,
            const uint8_t* pixels, uint32_t byteCount)
        {
            CHECK_HR(CheckThread());
            if (!pixels || !ValidSize(textureWidth, textureHeight) || stride < textureWidth * 4
                || static_cast<uint64_t>(stride) * textureHeight > byteCount
                || (textures.find(id) == textures.end() && textures.size() >= 4096)) return E_INVALIDARG;
            D3D11_TEXTURE2D_DESC description{};
            description.Width = textureWidth; description.Height = textureHeight;
            description.MipLevels = description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_IMMUTABLE;
            description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA initial{}; initial.pSysMem = pixels; initial.SysMemPitch = stride;
            ComPtr<ID3D11Texture2D> texture;
            ComPtr<ID3D11ShaderResourceView> view;
            CHECK_HR(device->CreateTexture2D(&description, &initial, &texture));
            CHECK_HR(device->CreateShaderResourceView(texture.Get(), nullptr, &view));
            textures[id] = std::move(view);
            return S_OK;
        }

        HRESULT Draw(const WispDrawCommand* commands, uint32_t count, bool forPresentation, WispDrawMetrics* metrics)
        {
            CHECK_HR(CheckThread());
            pendingPresentation = false;
            CpuTimer drawTimer(metrics ? &metrics->totalTicks : nullptr);
            if ((count && !commands) || count > 4096 || !renderTarget) return E_INVALIDARG;
            for (uint32_t index = 0; index < count; ++index)
            {
                const auto& command = commands[index];
                float values[18]; std::memcpy(values, &command.originX, sizeof(values));
                for (float value : values) if (!std::isfinite(value)) return E_INVALIDARG;
                if (command.shader > 2 || textures.find(command.textureId) == textures.end()
                    || command.tintA < 0 || command.tintA > 1 || command.tintR < 0 || command.tintR > 1
                    || command.tintG < 0 || command.tintG > 1 || command.tintB < 0 || command.tintB > 1
                    || (command.shader == 1 && command.parameterY <= 0)) return E_INVALIDARG;
            }
            captureReady = false;
            const float clear[4]{};
            context->ClearRenderTargetView(renderTarget.Get(), clear);
            ID3D11RenderTargetView* targetView = renderTarget.Get();
            context->OMSetRenderTargets(1, &targetView, nullptr);
            const float factors[4]{};
            context->OMSetBlendState(blend.Get(), factors, 0xffffffff);
            D3D11_VIEWPORT viewport{};
            viewport.Width = static_cast<float>(width); viewport.Height = static_cast<float>(height); viewport.MaxDepth = 1;
            context->RSSetViewports(1, &viewport);
            context->RSSetState(rasterizer.Get());
            context->IASetInputLayout(inputLayout.Get());
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ID3D11Buffer* vertexBuffer = vertices.Get();
            const UINT stride = sizeof(float) * 2, offset = 0;
            context->IASetVertexBuffers(0, 1, &vertexBuffer, &stride, &offset);
            context->VSSetShader(vertexShader.Get(), nullptr, 0);
            ID3D11Buffer* constantBuffer = constants.Get();
            context->VSSetConstantBuffers(0, 1, &constantBuffer);
            context->PSSetConstantBuffers(0, 1, &constantBuffer);
            ID3D11SamplerState* sample = sampler.Get();
            context->PSSetSamplers(0, 1, &sample);
            if (metrics) metrics->setupTicks = Counter() - drawTimer.Started();
            for (uint32_t index = 0; index < count; ++index)
            {
                const auto& c = commands[index];
                const Constants data{
                    {c.axisXX,c.axisYX,c.originX,static_cast<float>(width)},
                    {c.axisXY,c.axisYY,c.originY,static_cast<float>(height)},
                    {c.uvLeft,c.uvTop,c.uvRight,c.uvBottom}, {c.tintR,c.tintG,c.tintB,c.tintA},
                    {c.parameterX,c.parameterY,c.parameterZ,c.parameterW}
                };
                D3D11_MAPPED_SUBRESOURCE mapping{};
                const int64_t mapStarted = metrics ? Counter() : 0;
                const HRESULT mapped = context->Map(constants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapping);
                if (metrics)
                {
                    const int64_t elapsed = Counter() - mapStarted;
                    metrics->mapTicks += elapsed;
                    if (elapsed > metrics->maximumMapTicks) metrics->maximumMapTicks = elapsed;
                    ++metrics->mapCount;
                }
                CHECK_HR(mapped);
                std::memcpy(mapping.pData, &data, sizeof(data));
                context->Unmap(constants.Get(), 0);
                ID3D11ShaderResourceView* view = textures.at(c.textureId).Get();
                context->PSSetShaderResources(0, 1, &view);
                context->PSSetShader(pixelShaders[c.shader].Get(), nullptr, 0);
                context->Draw(6, 0);
                if (metrics) ++metrics->drawCount;
            }
            hasDrawn = true;
            CHECK_HR(device->GetDeviceRemovedReason());
            captureReady = !forPresentation;
            pendingPresentation = forPresentation;
            return S_OK;
        }

        HRESULT TryPresent(WispPresentMetrics* metrics)
        {
            CHECK_HR(CheckThread());
            if (!pendingPresentation) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
            CpuTimer presentTimer(metrics ? &metrics->durationTicks : nullptr);
            const HRESULT result = swapChain->Present(1, DXGI_PRESENT_DO_NOT_WAIT);
            if (result == S_OK) pendingPresentation = false;
            return result;
        }

        HRESULT Render(const WispDrawCommand* commands, uint32_t count, bool present)
        {
            CHECK_HR(Draw(commands, count, present, nullptr));
            return present ? PresentationResult(TryPresent(nullptr)) : S_OK;
        }

        HRESULT Capture(uint8_t* pixels, uint32_t byteCount, uint32_t stride)
        {
            CHECK_HR(CheckThread());
            if (!captureReady || !pixels || stride < width * 4 || static_cast<uint64_t>(stride) * height > byteCount) return E_INVALIDARG;
            ComPtr<ID3D11Texture2D> source, staging;
            CHECK_HR(swapChain->GetBuffer(0, IID_PPV_ARGS(&source)));
            D3D11_TEXTURE2D_DESC description{}; source->GetDesc(&description);
            description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0;
            description.CPUAccessFlags = D3D11_CPU_ACCESS_READ; description.MiscFlags = 0;
            CHECK_HR(device->CreateTexture2D(&description, nullptr, &staging));
            context->CopyResource(staging.Get(), source.Get());
            D3D11_MAPPED_SUBRESOURCE mapping{};
            CHECK_HR(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapping));
            for (uint32_t row = 0; row < height; ++row)
                std::memcpy(pixels + static_cast<size_t>(row) * stride,
                    static_cast<const uint8_t*>(mapping.pData) + static_cast<size_t>(row) * mapping.RowPitch, width * 4);
            context->Unmap(staging.Get(), 0);
            return S_OK;
        }
    };

    template<class Operation> HRESULT Invoke(void* pointer, Operation operation) noexcept
    {
        if (!pointer) return E_POINTER;
        try { return operation(*static_cast<Renderer*>(pointer)); }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }

    HRESULT WaitForFrame(void* renderer, uint32_t timeout, HANDLE cancellation,
        uint32_t* result, WispWaitMetrics* metrics) noexcept
    {
        if (metrics)
        {
            metrics->cpuTime100ns = -1;
            metrics->waitResult = WAIT_FAILED;
        }
        CpuTimer totalTimer(metrics ? &metrics->totalTicks : nullptr);
        ThreadCpuTimer cpuTimer(metrics ? &metrics->cpuTime100ns : nullptr);
        if (!result || timeout > 1000) return E_INVALIDARG;
        return Invoke(renderer, [&](Renderer& r) {
            {
                CpuTimer precheckTimer(metrics ? &metrics->precheckTicks : nullptr);
                CHECK_HR(r.CheckThread());
                if (metrics) metrics->swapChainGeneration = r.swapChainGeneration;
                if (cancellation)
                {
                    const DWORD cancelled = WaitForSingleObject(cancellation, 0);
                    if (cancelled == WAIT_OBJECT_0)
                    {
                        if (metrics) metrics->waitResult = cancelled;
                        *result = 2;
                        return S_OK;
                    }
                    if (cancelled == WAIT_FAILED) return HRESULT_FROM_WIN32(GetLastError());
                }
                CHECK_HR(r.device->GetDeviceRemovedReason());
            }
            HANDLE handles[2] = { cancellation ? cancellation : r.latency, r.latency };
            DWORD waited;
            DWORD waitError = ERROR_SUCCESS;
            {
                CpuTimer waitTimer(metrics ? &metrics->waitCallTicks : nullptr);
                waited = WaitForMultipleObjects(cancellation ? 2 : 1, handles, FALSE, timeout);
                // Preserve the original failure before diagnostic clock calls run.
                if (waited == WAIT_FAILED) waitError = GetLastError();
            }
            if (metrics) metrics->waitResult = waited;
            {
                CpuTimer postcheckTimer(metrics ? &metrics->postcheckTicks : nullptr);
                if (cancellation && waited == WAIT_OBJECT_0) { *result = 2; return S_OK; }
                if (waited == WAIT_FAILED) return HRESULT_FROM_WIN32(waitError);
                CHECK_HR(r.device->GetDeviceRemovedReason());
                if (waited == WAIT_TIMEOUT) { *result = 1; return S_OK; }
                if (waited == WAIT_OBJECT_0) { *result = 0; return S_OK; }
                if (cancellation && waited == WAIT_OBJECT_0 + 1) { *result = 0; return S_OK; }
                return E_UNEXPECTED;
            }
        });
    }
}

HRESULT __cdecl WispRendererCreate(HWND hwnd, uint32_t width, uint32_t height, void** output) noexcept
{
    if (!output) return E_POINTER;
    *output = nullptr;
    try {
        auto renderer = std::make_unique<Renderer>();
        CHECK_HR(renderer->Initialize(hwnd, width, height));
        *output = renderer.release();
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
void __cdecl WispRendererDestroy(void* renderer) noexcept { delete static_cast<Renderer*>(renderer); }
HRESULT __cdecl WispRendererPrepareForResume(void* renderer) noexcept
{ return Invoke(renderer, [&](Renderer& r) { return r.PrepareForResume(); }); }
HRESULT __cdecl WispRendererSetOpacity(void* renderer, float opacity) noexcept
{ return Invoke(renderer, [&](Renderer& r) { CHECK_HR(r.CheckThread()); if (!std::isfinite(opacity) || opacity < 0 || opacity > 1) return E_INVALIDARG; if (r.opacity == opacity) return S_OK; if (r.visible) { CHECK_HR(r.opacityEffect->SetOpacity(opacity)); CHECK_HR(r.composition->Commit()); } r.opacity = opacity; return S_OK; }); }
HRESULT __cdecl WispRendererSetVisible(void* renderer, int visible) noexcept
{ return Invoke(renderer, [&](Renderer& r) { CHECK_HR(r.CheckThread()); const bool show = visible != 0; if (!show) r.pendingPresentation = false; if (r.visible == show) return S_OK; CHECK_HR(r.opacityEffect->SetOpacity(show ? r.opacity : 0.0f)); CHECK_HR(r.composition->Commit()); r.visible = show; return S_OK; }); }
HRESULT __cdecl WispRendererResize(void* renderer, uint32_t width, uint32_t height, float x, float y) noexcept
{ return Invoke(renderer, [&](Renderer& r) { return r.Resize(width,height,x,y); }); }
HRESULT __cdecl WispRendererUploadTexture(void* renderer, uint32_t id, uint32_t width, uint32_t height,
    uint32_t stride, const uint8_t* pixels, uint32_t bytes) noexcept
{ if (!id) return E_INVALIDARG; return Invoke(renderer, [&](Renderer& r) { return r.Upload(id,width,height,stride,pixels,bytes); }); }
HRESULT __cdecl WispRendererRemoveTexture(void* renderer, uint32_t id) noexcept
{ return Invoke(renderer, [&](Renderer& r) { CHECK_HR(r.CheckThread()); if (!id) return E_INVALIDARG; r.textures.erase(id); return S_OK; }); }
HRESULT __cdecl WispRendererRender(void* renderer, const WispDrawCommand* commands, uint32_t count, int present) noexcept
{ return Invoke(renderer, [&](Renderer& r) { return r.Render(commands,count,present != 0); }); }
HRESULT __cdecl WispRendererDrawForPresentation(void* renderer, const WispDrawCommand* commands,
    uint32_t count, int measure, WispDrawMetrics* metrics) noexcept
{
    if (!metrics) return E_POINTER;
    *metrics = {};
    return Invoke(renderer, [&](Renderer& r) { return r.Draw(commands, count, true, measure ? metrics : nullptr); });
}
HRESULT __cdecl WispRendererTryPresent(void* renderer, int measure, WispPresentMetrics* metrics) noexcept
{
    if (!metrics) return E_POINTER;
    *metrics = {};
    const HRESULT result = Invoke(renderer, [&](Renderer& r) { return r.TryPresent(measure ? metrics : nullptr); });
    if (measure) metrics->hResult = result;
    return PresentationResult(result);
}
HRESULT __cdecl WispRendererWaitForFrame(void* renderer, uint32_t timeout, HANDLE cancellation, uint32_t* result) noexcept
{
    return WaitForFrame(renderer, timeout, cancellation, result, nullptr);
}
HRESULT __cdecl WispRendererWaitForFrameMeasured(void* renderer, uint32_t timeout, HANDLE cancellation,
    int measure, uint32_t* result, WispWaitMetrics* metrics) noexcept
{
    if (!metrics) return E_POINTER;
    *metrics = {};
    return WaitForFrame(renderer, timeout, cancellation, result, measure ? metrics : nullptr);
}
HRESULT __cdecl WispRendererCapture(void* renderer, uint8_t* pixels, uint32_t bytes, uint32_t stride) noexcept
{ return Invoke(renderer, [&](Renderer& r) { return r.Capture(pixels,bytes,stride); }); }
HRESULT __cdecl WispRendererDeviceRemovedReason(void* renderer) noexcept
{ return Invoke(renderer, [&](Renderer& r) { CHECK_HR(r.CheckThread()); return r.device->GetDeviceRemovedReason(); }); }
