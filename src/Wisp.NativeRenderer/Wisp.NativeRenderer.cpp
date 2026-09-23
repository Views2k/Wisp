#include "Wisp.NativeRenderer.h"
#include "FramePacer.h"
#include "TearingPolicy.h"
#include <d3d11.h>
#include <dxgi1_3.h>
#include <dcomp.h>
#include <winternl.h>
#include <d3dkmthk.h>
#include <wrl/client.h>
#include <array>
#include <cmath>
#include <cstring>
#include <memory>
#include <atomic>
#include <algorithm>
#include <new>
#include <unordered_map>
#include <cfloat>
#include "QuadVertex.h"
#include "ImagePixel.h"
#include "DialPixel.h"
#include "NeedlePixel.h"
#include "ElectricNeedlePixel.h"
#include "ImageSectorPixel.h"
#include "DigitalGaugePixel.h"

using Microsoft::WRL::ComPtr;
#define CHECK_HR(expression) do { const HRESULT checkedResult = (expression); if (FAILED(checkedResult)) return checkedResult; } while (false)

namespace
{
    // Keep one additional frame in flight so drawing can overlap presentation.
    // A separate display-rate gate bounds work when queued frames are superseded.
    constexpr UINT SwapChainBufferCount = 3;
    constexpr UINT MaximumFrameLatency = 2;
    constexpr uint64_t MaximumDialCacheBytes = 16ull * 1024 * 1024;

    constexpr WispGpuPriorityStatus UnattemptedGpuPriorityStatus() noexcept
    {
        return {0, D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH, INT32_MIN, INT32_MIN, -1,
            1, S_FALSE, S_FALSE, INT32_MIN};
    }

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

    HRESULT WaitForInitialFrame(HANDLE latency) noexcept
    {
        // The transparent primer consumes a queue slot just like a visible
        // frame. Pair its first Present with one wait on the new swapchain.
        const DWORD ready = WaitForSingleObjectEx(latency, 100, FALSE);
        if (ready == WAIT_OBJECT_0) return S_OK;
        if (ready == WAIT_TIMEOUT) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        return ready == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError()) : E_UNEXPECTED;
    }

    HRESULT InitialPresentationResult(HRESULT result) noexcept
    {
        // A failed or occluded primer must discard the new chain; continuing
        // would lose the acquired readiness without submitting its frame.
        return result == S_OK || FAILED(result) ? result : HRESULT_FROM_WIN32(ERROR_NOT_READY);
    }

    struct AcceptedMotionGeometry
    {
        WispCompositorNeedleGeometry geometry;
        uint64_t generation, clearEpoch;
    };

    struct MotionChannel final
    {
        std::atomic<DWORD> threadId{0};
        std::atomic<bool> rendererAlive{true};
        std::atomic<uint64_t> requestedGeneration{0}, committedGeneration{0}, clearEpoch{0}, bitmapDraws{0};
        std::shared_ptr<const AcceptedMotionGeometry> accepted;
        ComPtr<IDCompositionDevice> composition, contentComposition;
        ComPtr<IDCompositionVisual> visual, bitmapVisual;
        ComPtr<IDCompositionRotateTransform> rotation;
        ComPtr<IDCompositionEffectGroup> opacity;
        int64_t frequency = 0;
        uint64_t commits = 0;

        HRESULT CheckThread() noexcept
        {
            DWORD expected = 0;
            const DWORD current = GetCurrentThreadId();
            if (threadId.compare_exchange_strong(expected, current) || expected == current) return S_OK;
            return RPC_E_WRONG_THREAD;
        }

        HRESULT Clear()
        {
            CHECK_HR(CheckThread());
            requestedGeneration.store(0);
            clearEpoch.fetch_add(1);
            committedGeneration.store(0);
            std::atomic_store(&accepted, std::shared_ptr<const AcceptedMotionGeometry>{});
            CHECK_HR(opacity->SetOpacity(0.0f));
            CHECK_HR(composition->Commit());
            ++commits;
            return S_OK;
        }

        HRESULT Update(const WispCompositorNeedleGeometry* geometry, int64_t begin, int64_t freshUntil,
            const WispCompositorNeedlePoint* points, uint32_t count, uint64_t generation, WispMotionStatus* status)
        {
            CHECK_HR(CheckThread());
            if (!rendererAlive.load()) return S_FALSE;
            if (!geometry || !points || !count || count > 256 || !generation || begin <= 0 || freshUntil <= begin ||
                frequency <= 0 || geometry->reserved || !std::isfinite(geometry->pivotX) || !std::isfinite(geometry->pivotY) ||
                !std::isfinite(geometry->opacity) || geometry->opacity < 0 || geometry->opacity > 1) return E_INVALIDARG;
            for (uint32_t index = 0; index < count; ++index)
            {
                const auto& point = points[index];
                if (!std::isfinite(point.offsetSeconds) || !std::isfinite(point.angle) || std::abs(point.angle) > FLT_MAX ||
                    (index == 0 ? point.offsetSeconds != 0 : point.offsetSeconds <= points[index - 1].offsetSeconds)) return E_INVALIDARG;
            }
            const double endTicks = points[count - 1].offsetSeconds * frequency;
            if (!std::isfinite(endTicks) || endTicks < 0 || endTicks >= static_cast<double>(INT64_MAX - begin)) return E_INVALIDARG;
            requestedGeneration.store(generation);
            auto normalized = *geometry;
            normalized.command.parameterX = 0;
            const auto prepared = std::atomic_load(&accepted);
            const bool matches = prepared && prepared->clearEpoch == clearEpoch.load() && prepared->generation == generation &&
                std::memcmp(&normalized, &prepared->geometry, sizeof(normalized)) == 0;
            ComPtr<IDCompositionAnimation> angle, freshness;
            LARGE_INTEGER start{}; start.QuadPart = begin;
            if (count > 1)
            {
                CHECK_HR(composition->CreateAnimation(&angle));
                CHECK_HR(angle->SetAbsoluteBeginTime(start));
                for (uint32_t index = 0; index + 1 < count; ++index)
                {
                    const double slope = (points[index + 1].angle - points[index].angle) /
                        (points[index + 1].offsetSeconds - points[index].offsetSeconds);
                    if (!std::isfinite(slope) || std::abs(slope) > FLT_MAX) return E_INVALIDARG;
                    CHECK_HR(angle->AddCubic(points[index].offsetSeconds, static_cast<float>(points[index].angle),
                        static_cast<float>(slope), 0, 0));
                }
                CHECK_HR(angle->End(points[count - 1].offsetSeconds, static_cast<float>(points[count - 1].angle)));
            }
            CHECK_HR(rotation->SetCenterX(geometry->pivotX));
            CHECK_HR(rotation->SetCenterY(geometry->pivotY));
            if (angle) { CHECK_HR(rotation->SetAngle(angle.Get())); }
            else { CHECK_HR(rotation->SetAngle(static_cast<float>(points[0].angle))); }
            if (matches)
            {
                CHECK_HR(composition->CreateAnimation(&freshness));
                CHECK_HR(freshness->SetAbsoluteBeginTime(start));
                CHECK_HR(freshness->AddCubic(0, geometry->opacity, 0, 0, 0));
                CHECK_HR(freshness->End(static_cast<double>(freshUntil - begin) / frequency, 0));
                CHECK_HR(opacity->SetOpacity(freshness.Get()));
            }
            else { CHECK_HR(opacity->SetOpacity(0.0f)); }
            CHECK_HR(composition->Commit());
            committedGeneration.store(generation);
            ++commits;
            if (status) *status = {begin, begin + static_cast<int64_t>(endTicks), freshUntil, Counter(), commits,
                bitmapDraws.load(), count, matches ? 1u : 0u};
            return S_OK;
        }
    };

    struct MotionHandle { std::shared_ptr<MotionChannel> channel; };

    class Renderer final
    {
    public:
        DWORD threadId = GetCurrentThreadId();
        HWND hwnd = nullptr;
        uint32_t width = 0, height = 0;
        uint32_t swapChainGeneration = 1;
        HANDLE latency = nullptr;
        FramePacer pacer;
        FrameWaitBudget waitBudget;
        HMONITOR pacingMonitor = nullptr;
        int64_t lastRefreshCheck = 0;
        UINT presentationSyncInterval = 1;
        UINT presentationRefreshRate = 0;
        HRESULT pacingStatus = E_PENDING;
        UINT swapChainFlags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        bool tearingSupported = false;
        HRESULT tearingSupportResult = S_FALSE;
        UINT lastPresentSyncInterval = UINT32_MAX, lastPresentFlags = 0;
        HRESULT lastPresentResult = E_PENDING;
        bool compositorNeedleSupported = false, compositorNeedleEnabled = false, splitNeedleFrame = false, pendingNeedleSplit = false;
        bool needleVisible = false;
        bool needleBitmapValid = false;
        uint32_t needleWidth = 0, needleHeight = 0, needlePointCount = 0;
        uint64_t needleUpdates = 0, presentCalls = 0;
        uint64_t needleBitmapDraws = 0;
        uint64_t needleGeometryGeneration = 0, pendingNeedleGeometryGeneration = 0, presentedNeedleGeometryGeneration = 0;
        int64_t needleBegin = 0, needleEnd = 0, needleFreshUntil = 0;
        WispCompositorNeedleGeometry needleGeometry{};
        WispDrawCommand needleBitmapCommand{};
        ComPtr<IDCompositionVisual> underlayVisual, needleVisual, foregroundVisual;
        ComPtr<IDCompositionSurface> needleSurface;
        ComPtr<IDCompositionScaleTransform> needleScale;
        ComPtr<IDCompositionRotateTransform> needleRotation;
        ComPtr<IDCompositionMatrixTransform> needleParent;
        ComPtr<IDCompositionAnimation> needleOpacityAnimation;
        ComPtr<IDCompositionEffectGroup> needleOpacityEffect;
        std::shared_ptr<MotionChannel> motion;
        uint64_t motionPreparationGeneration = 0, motionPreparationClearEpoch = 0;
        bool captureReady = false;
        bool visible = false;
        float opacity = 1.0f;
        bool hasDrawn = false;
        bool pendingPresentation = false;
        bool cacheDialOnCpu = false;
        bool dialCacheValid = false;
        bool dialCacheUnavailable = false;
        WispDrawCommand cachedDial{};
        WispGpuPriorityStatus gpuPriorityStatus = UnattemptedGpuPriorityStatus();
        ComPtr<ID3D11Texture2D> dialCache;
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
        std::array<ComPtr<ID3D11PixelShader>, 6> pixelShaders;
        ComPtr<ID3D11InputLayout> inputLayout;
        ComPtr<ID3D11SamplerState> sampler;
        ComPtr<ID3D11BlendState> blend;
        ComPtr<ID3D11RasterizerState> rasterizer;
        ComPtr<ID3D11RasterizerState> atlasRasterizer;
        std::unordered_map<uint32_t, ComPtr<ID3D11ShaderResourceView>> textures;

        ~Renderer()
        {
            if (motion) { motion->rendererAlive.store(false); CloseMotionGate(); }
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

        void CloseMotionGate() noexcept
        {
            if (motion) std::atomic_store(&motion->accepted, std::shared_ptr<const AcceptedMotionGeometry>{});
        }

        void PublishMotionGate(bool enabled)
        {
            if (!motion) return;
            if (!enabled || !motionPreparationGeneration || motionPreparationClearEpoch != motion->clearEpoch.load())
            { CloseMotionGate(); return; }
            auto normalized = needleGeometry;
            normalized.command.parameterX = 0;
            std::shared_ptr<const AcceptedMotionGeometry> snapshot =
                std::make_shared<AcceptedMotionGeometry>(AcceptedMotionGeometry{normalized, motionPreparationGeneration, motionPreparationClearEpoch});
            std::atomic_store(&motion->accepted, std::move(snapshot));
        }

        HRESULT CreateMotionChannel(void** output)
        {
            CHECK_HR(CheckThread());
            if (!output) return E_POINTER;
            *output = nullptr;
            if (!compositorNeedleSupported) return S_FALSE;
            if (motion) return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
            auto channel = std::make_shared<MotionChannel>();
            channel->frequency = pacer.Frequency();
            channel->contentComposition = composition;
            CHECK_HR(DCompositionCreateDevice2(nullptr, IID_PPV_ARGS(&channel->composition)));
            CHECK_HR(channel->composition->CreateVisual(&channel->visual));
            CHECK_HR(channel->composition->CreateRotateTransform(&channel->rotation));
            CHECK_HR(channel->composition->CreateEffectGroup(&channel->opacity));
            CHECK_HR(channel->visual->SetTransform(channel->rotation.Get()));
            CHECK_HR(channel->visual->SetEffect(channel->opacity.Get()));
            CHECK_HR(channel->opacity->SetOpacity(0.0f));
            CHECK_HR(composition->CreateVisual(&channel->bitmapVisual));
            CHECK_HR(channel->bitmapVisual->SetTransform(needleScale.Get()));
            CHECK_HR(channel->bitmapVisual->SetContent(needleSurface.Get()));
            CHECK_HR(channel->visual->AddVisual(channel->bitmapVisual.Get(), FALSE, nullptr));
            CHECK_HR(needleVisual->SetContent(nullptr));
            CHECK_HR(needleVisual->SetTransform(needleParent.Get()));
            CHECK_HR(needleVisual->AddVisual(channel->visual.Get(), FALSE, nullptr));
            CHECK_HR(needleOpacityEffect->SetOpacity(0.0f));
            CHECK_HR(channel->composition->Commit());
            CHECK_HR(composition->Commit());
            motion = channel;
            compositorNeedleEnabled = false;
            needleVisible = false;
            *output = new MotionHandle{std::move(channel)};
            return S_OK;
        }

        HRESULT PresentChain(IDXGISwapChain2* chain, UINT chainFlags, UINT syncInterval) noexcept
        {
            ++presentCalls;
            lastPresentSyncInterval = syncInterval;
            lastPresentFlags = WispTearingPolicy::PresentFlags(chainFlags, syncInterval);
            lastPresentResult = chain->Present(syncInterval, lastPresentFlags);
            return lastPresentResult;
        }

        uint32_t AtlasHeight() const noexcept { return compositorNeedleSupported ? height * 2u : height; }

        HRESULT SetAtlasClips()
        {
            if (!compositorNeedleSupported) return S_OK;
            const D2D_RECT_F top{0, 0, static_cast<float>(width), static_cast<float>(height)};
            const D2D_RECT_F bottom{0, static_cast<float>(height), static_cast<float>(width), static_cast<float>(height * 2u)};
            CHECK_HR(visual->SetClip(top));
            CHECK_HR(underlayVisual->SetClip(top));
            CHECK_HR(foregroundVisual->SetClip(bottom));
            return foregroundVisual->SetOffsetY(-static_cast<float>(height));
        }

        HRESULT BindCompositionContent(IUnknown* content)
        {
            if (!compositorNeedleSupported) return visual->SetContent(content);
            CHECK_HR(underlayVisual->SetContent(content));
            return foregroundVisual->SetContent(content);
        }

        void BindPipeline(ID3D11RenderTargetView* view, float targetWidth, float targetHeight,
            float offsetX = 0, float offsetY = 0)
        {
            context->OMSetRenderTargets(1, &view, nullptr);
            const float factors[4]{};
            context->OMSetBlendState(blend.Get(), factors, 0xffffffff);
            D3D11_VIEWPORT viewport{offsetX, offsetY, targetWidth, targetHeight, 0, 1};
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
        }

        HRESULT DrawCommand(const WispDrawCommand& c, float targetWidth, float targetHeight, WispDrawMetrics* metrics)
        {
            const Constants data{
                {c.axisXX,c.axisYX,c.originX,targetWidth}, {c.axisXY,c.axisYY,c.originY,targetHeight},
                {c.uvLeft,c.uvTop,c.uvRight,c.uvBottom}, {c.tintR,c.tintG,c.tintB,c.tintA},
                {c.parameterX,c.parameterY,c.parameterZ,c.parameterW}};
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
            return S_OK;
        }

        HRESULT ReadRegion(ID3D11Texture2D* source, UINT x, UINT y, UINT regionWidth, UINT regionHeight,
            uint8_t* pixels, uint32_t bytes, uint32_t stride)
        {
            if (!pixels || stride < regionWidth * 4u || static_cast<uint64_t>(stride) * regionHeight > bytes)
                return E_INVALIDARG;
            D3D11_TEXTURE2D_DESC description{};
            source->GetDesc(&description);
            if (x > description.Width || regionWidth > description.Width - x ||
                y > description.Height || regionHeight > description.Height - y) return E_INVALIDARG;
            description.Width = regionWidth; description.Height = regionHeight;
            description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0;
            description.CPUAccessFlags = D3D11_CPU_ACCESS_READ; description.MiscFlags = 0;
            ComPtr<ID3D11Texture2D> staging;
            CHECK_HR(device->CreateTexture2D(&description, nullptr, &staging));
            const D3D11_BOX box{x, y, 0, x + regionWidth, y + regionHeight, 1};
            context->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, source, 0, &box);
            D3D11_MAPPED_SUBRESOURCE mapping{};
            CHECK_HR(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapping));
            for (UINT row = 0; row < regionHeight; ++row)
                std::memcpy(pixels + static_cast<size_t>(row) * stride,
                    static_cast<const uint8_t*>(mapping.pData) + static_cast<size_t>(row) * mapping.RowPitch, regionWidth * 4u);
            context->Unmap(staging.Get(), 0);
            return S_OK;
        }

        HRESULT DrawNeedleBitmap(uint8_t* pixels = nullptr, uint32_t bytes = 0, uint32_t stride = 0)
        {
            needleBitmapValid = false;
            ComPtr<IDXGISurface> surface;
            POINT updateOffset{};
            CHECK_HR(needleSurface->BeginDraw(nullptr, IID_PPV_ARGS(&surface), &updateOffset));
            ComPtr<ID3D11Texture2D> texture;
            ComPtr<ID3D11RenderTargetView> targetView;
            HRESULT result = surface.As(&texture);
            if (SUCCEEDED(result)) result = device->CreateRenderTargetView(texture.Get(), nullptr, &targetView);
            if (SUCCEEDED(result))
            {
                BindPipeline(targetView.Get(), static_cast<float>(needleWidth), static_cast<float>(needleHeight),
                    static_cast<float>(updateOffset.x), static_cast<float>(updateOffset.y));
                // BeginDraw may give a shared atlas. Clear only our update rectangle,
                // never ClearRenderTargetView on the whole returned resource.
                WispDrawCommand clear{};
                clear.axisXX = static_cast<float>(needleWidth); clear.axisYY = static_cast<float>(needleHeight);
                clear.uvRight = clear.uvBottom = 1;
                context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
                result = DrawCommand(clear, clear.axisXX, clear.axisYY, nullptr);
                context->OMSetBlendState(blend.Get(), nullptr, 0xffffffff);
                auto command = needleGeometry.command;
                command.axisXX = clear.axisXX; command.axisYY = clear.axisYY;
                if (SUCCEEDED(result)) result = DrawCommand(command, clear.axisXX, clear.axisYY, nullptr);
                context->OMSetRenderTargets(0, nullptr, nullptr);
                if (SUCCEEDED(result) && pixels)
                    result = ReadRegion(texture.Get(), static_cast<UINT>(updateOffset.x), static_cast<UINT>(updateOffset.y),
                        needleWidth, needleHeight, pixels, bytes, stride);
            }
            context->OMSetRenderTargets(0, nullptr, nullptr);
            targetView.Reset(); texture.Reset(); surface.Reset();
            const HRESULT ended = needleSurface->EndDraw();
            if (FAILED(result)) return result;
            CHECK_HR(ended);
            needleBitmapCommand = needleGeometry.command;
            needleBitmapCommand.axisXX = static_cast<float>(needleWidth);
            needleBitmapCommand.axisYY = static_cast<float>(needleHeight);
            needleBitmapValid = true;
            ++needleBitmapDraws;
            if (motion) motion->bitmapDraws.store(needleBitmapDraws);
            return S_OK;
        }

        HRESULT UpdateCompositorNeedle(const WispCompositorNeedleGeometry* geometry, int64_t begin, int64_t freshUntil,
            const WispCompositorNeedlePoint* points, uint32_t count)
        {
            CHECK_HR(CheckThread());
            if (!compositorNeedleSupported) return S_FALSE;
            if (!geometry && !count)
            {
                CloseMotionGate();
                compositorNeedleEnabled = false;
                needleVisible = false;
                pendingNeedleGeometryGeneration = presentedNeedleGeometryGeneration = 0;
                needleOpacityAnimation.Reset();
                CHECK_HR(needleRotation->SetAngle(0.0f));
                CHECK_HR(needleOpacityEffect->SetOpacity(0.0f));
                return composition->Commit();
            }
            if (!geometry || !points || !count || count > 256 || begin <= 0 || freshUntil <= begin || geometry->reserved) return E_INVALIDARG;
            const auto& command = geometry->command;
            float values[18]; std::memcpy(values, &command.originX, sizeof(values));
            for (float value : values) if (!std::isfinite(value)) return E_INVALIDARG;
            float placement[9]; std::memcpy(placement, &geometry->pivotX, sizeof(placement));
            for (float value : placement) if (!std::isfinite(value)) return E_INVALIDARG;
            if (command.shader != 2 || command.textureId != 0 || command.originX != 0 || command.originY != 0 ||
                command.axisXY != 0 || command.axisYX != 0 || command.axisXX <= 0 || command.axisYY <= 0 ||
                command.tintA < 0 || command.tintA > 1 || command.tintR < 0 || command.tintR > 1 ||
                command.tintG < 0 || command.tintG > 1 || command.tintB < 0 || command.tintB > 1 ||
                geometry->opacity < 0 || geometry->opacity > 1) return E_INVALIDARG;
            const double pixelWidth = std::ceil(command.axisXX * std::hypot(geometry->parentM11, geometry->parentM12));
            const double pixelHeight = std::ceil(command.axisYY * std::hypot(geometry->parentM21, geometry->parentM22));
            if (!std::isfinite(pixelWidth) || !std::isfinite(pixelHeight) || pixelWidth < 1 || pixelHeight < 1 ||
                pixelWidth > 8192 || pixelHeight > 8192) return E_INVALIDARG;
            for (uint32_t index = 0; index < count; ++index)
            {
                const auto& point = points[index];
                if (!std::isfinite(point.offsetSeconds) || !std::isfinite(point.angle) || !std::isfinite(point.blur) ||
                    std::abs(point.angle) > FLT_MAX || std::abs(point.blur) > FLT_MAX ||
                    (index == 0 ? point.offsetSeconds != 0 : point.offsetSeconds <= points[index - 1].offsetSeconds)) return E_INVALIDARG;
            }
            const double endTicks = points[count - 1].offsetSeconds * pacer.Frequency();
            if (pacer.Frequency() <= 0 || !std::isfinite(endTicks) || endTicks < 0 ||
                endTicks >= static_cast<double>(INT64_MAX - begin)) return E_INVALIDARG;
            ComPtr<IDCompositionAnimation> animation;
            ComPtr<IDCompositionAnimation> opacityAnimation;
            if (!motion)
            {
                CHECK_HR(composition->CreateAnimation(&opacityAnimation));
                LARGE_INTEGER opacityStart{}; opacityStart.QuadPart = begin;
                CHECK_HR(opacityAnimation->SetAbsoluteBeginTime(opacityStart));
                CHECK_HR(opacityAnimation->AddCubic(0, geometry->opacity, 0, 0, 0));
                CHECK_HR(opacityAnimation->End(static_cast<double>(freshUntil - begin) / pacer.Frequency(), 0));
                if (count > 1)
                {
                    CHECK_HR(composition->CreateAnimation(&animation));
                    LARGE_INTEGER start{}; start.QuadPart = begin;
                    CHECK_HR(animation->SetAbsoluteBeginTime(start));
                    for (uint32_t index = 0; index + 1 < count; ++index)
                    {
                        const double slope = (points[index + 1].angle - points[index].angle) /
                            (points[index + 1].offsetSeconds - points[index].offsetSeconds);
                        if (!std::isfinite(slope) || std::abs(slope) > FLT_MAX) return E_INVALIDARG;
                        CHECK_HR(animation->AddCubic(points[index].offsetSeconds, static_cast<float>(points[index].angle),
                            static_cast<float>(slope), 0, 0));
                    }
                    CHECK_HR(animation->End(points[count - 1].offsetSeconds, static_cast<float>(points[count - 1].angle)));
                }
            }
            const UINT targetWidth = static_cast<UINT>(pixelWidth), targetHeight = static_cast<UINT>(pixelHeight);
            if (!needleSurface || needleWidth != targetWidth || needleHeight != targetHeight)
            {
                ComPtr<IDCompositionSurface> replacement;
                CHECK_HR(composition->CreateSurface(targetWidth, targetHeight, DXGI_FORMAT_B8G8R8A8_UNORM,
                    DXGI_ALPHA_MODE_PREMULTIPLIED, &replacement));
                needleSurface = std::move(replacement); needleWidth = targetWidth; needleHeight = targetHeight;
                needleBitmapValid = false;
                CHECK_HR((motion ? motion->bitmapVisual.Get() : needleVisual.Get())->SetContent(needleSurface.Get()));
            }
            // Blur changes only the independent bitmap. Placement/artwork changes
            // must wait for an atlas drawn with this geometry, even split-to-split.
            auto previousGeometry = needleGeometry;
            previousGeometry.command.parameterX = command.parameterX;
            if (!compositorNeedleEnabled || std::memcmp(&previousGeometry, geometry, sizeof(*geometry)) != 0)
            {
                ++needleGeometryGeneration;
                CloseMotionGate();
            }
            needleGeometry = *geometry;
            needleGeometry.command.parameterX = static_cast<float>(points[0].blur);
            auto bitmapCommand = needleGeometry.command;
            bitmapCommand.axisXX = static_cast<float>(needleWidth);
            bitmapCommand.axisYY = static_cast<float>(needleHeight);
            // Angle, pivot and placement belong to the composition transforms.
            // Preserve exact material values while reusing unchanged bitmap pixels.
            if (!needleBitmapValid || std::memcmp(&bitmapCommand, &needleBitmapCommand, sizeof(bitmapCommand)) != 0)
                CHECK_HR(DrawNeedleBitmap());
            CHECK_HR(needleScale->SetScaleX(command.axisXX / needleWidth));
            CHECK_HR(needleScale->SetScaleY(command.axisYY / needleHeight));
            if (!motion)
            {
                CHECK_HR(needleRotation->SetCenterX(geometry->pivotX));
                CHECK_HR(needleRotation->SetCenterY(geometry->pivotY));
                if (animation) { CHECK_HR(needleRotation->SetAngle(animation.Get())); }
                else { CHECK_HR(needleRotation->SetAngle(static_cast<float>(points[0].angle))); }
            }
            const D2D_MATRIX_3X2_F parent{geometry->parentM11,geometry->parentM12,geometry->parentM21,
                geometry->parentM22,geometry->parentOffsetX,geometry->parentOffsetY};
            CHECK_HR(needleParent->SetMatrix(parent));
            const bool showNeedle = splitNeedleFrame && presentedNeedleGeometryGeneration == needleGeometryGeneration &&
                (!motion || motion->committedGeneration.load() == motionPreparationGeneration);
            if (showNeedle && motion) { CHECK_HR(needleOpacityEffect->SetOpacity(1.0f)); }
            else if (showNeedle) { CHECK_HR(needleOpacityEffect->SetOpacity(opacityAnimation.Get())); }
            else { CHECK_HR(needleOpacityEffect->SetOpacity(0.0f)); }
            CHECK_HR(composition->Commit());
            PublishMotionGate(showNeedle);
            needleVisible = showNeedle;
            needleOpacityAnimation = std::move(opacityAnimation);
            compositorNeedleEnabled = true; needleBegin = begin;
            needleFreshUntil = freshUntil;
            needleEnd = begin + static_cast<int64_t>(endTicks); needlePointCount = count; ++needleUpdates;
            return S_OK;
        }

        void DisablePacing(HRESULT reason) noexcept
        {
            presentationSyncInterval = 1;
            pacingStatus = FAILED(reason) ? reason : E_FAIL;
            pacer.Reset();
        }

        void RefreshPresentationRate(bool force = false) noexcept
        {
            const int64_t now = Counter();
            const HMONITOR monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            // Same-size monitor moves are independent of Resize. Recheck the
            // mode at most once a second to catch changes on the same monitor.
            if (!force && monitor == pacingMonitor && pacer.Frequency() > 0 &&
                now >= lastRefreshCheck && now - lastRefreshCheck < pacer.Frequency()) return;
            const bool monitorChanged = monitor != pacingMonitor;
            pacingMonitor = monitor;
            lastRefreshCheck = now;
            if (monitorChanged || force) pacer.Reset();
            MONITORINFOEXW info{};
            info.cbSize = sizeof(info);
            DEVMODEW mode{};
            mode.dmSize = sizeof(mode);
            if (!monitor || !GetMonitorInfoW(monitor, &info) ||
                !EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode) ||
                mode.dmDisplayFrequency <= 1)
            {
                presentationRefreshRate = 0;
                DisablePacing(HRESULT_FROM_WIN32(ERROR_NOT_READY));
                return;
            }
            presentationRefreshRate = mode.dmDisplayFrequency;
            if (!pacer.IsValid())
            {
                DisablePacing(HRESULT_FROM_WIN32(pacer.InitializationError()));
                return;
            }
            if (!pacer.Configure(presentationRefreshRate))
            {
                DisablePacing(HRESULT_FROM_WIN32(GetLastError()));
                return;
            }
            presentationSyncInterval = 0;
            pacingStatus = S_OK;
        }

        void ConfigureGpuPriority(IDXGIDevice* dxgiDevice) noexcept
        {
            gpuPriorityStatus.attempted = 1;
            // Best-effort test: neither failure prevents rendering, and no
            // elevation, absolute priority or realtime class is requested.
            gpuPriorityStatus.processSetStatus = D3DKMTSetProcessSchedulingPriorityClass(
                GetCurrentProcess(), D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
            auto processClass = D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL;
            gpuPriorityStatus.processReadStatus = D3DKMTGetProcessSchedulingPriorityClass(
                GetCurrentProcess(), &processClass);
            if (gpuPriorityStatus.processReadStatus >= 0)
                gpuPriorityStatus.effectiveProcessClass = static_cast<int32_t>(processClass);
            gpuPriorityStatus.deviceSetHResult = dxgiDevice->SetGPUThreadPriority(1);
            INT devicePriority = INT32_MIN;
            gpuPriorityStatus.deviceReadHResult = dxgiDevice->GetGPUThreadPriority(&devicePriority);
            if (gpuPriorityStatus.deviceReadHResult == S_OK)
                gpuPriorityStatus.effectiveDevicePriority = devicePriority;
        }

        HRESULT CreateTarget()
        {
            ComPtr<ID3D11Texture2D> buffer;
            CHECK_HR(swapChain->GetBuffer(0, IID_PPV_ARGS(&buffer)));
            return device->CreateRenderTargetView(buffer.Get(), nullptr, &renderTarget);
        }

        bool PrepareDialCache(const WispDrawCommand* commands, uint32_t count)
        {
            if (!cacheDialOnCpu || count == 0 || commands[0].shader != 1 || dialCacheUnavailable ||
                static_cast<uint64_t>(width) * height * 4 > MaximumDialCacheBytes) return false;
            if (dialCache) return true;
            D3D11_TEXTURE2D_DESC description{};
            description.Width = width; description.Height = height;
            description.MipLevels = description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            if (FAILED(device->CreateTexture2D(&description, nullptr, &dialCache)))
            {
                // An optional cache must not turn memory pressure into a HUD failure.
                dialCacheUnavailable = true;
                return false;
            }
            return true;
        }

        HRESULT Initialize(HWND window, uint32_t targetWidth, uint32_t targetHeight, bool cpuRendering, bool independentNeedle = false)
        {
            DWORD process = 0;
            if (!IsWindow(window) || !GetWindowThreadProcessId(window, &process) || process != GetCurrentProcessId()
                || !ValidSize(targetWidth, targetHeight)) return E_INVALIDARG;
            hwnd = window; width = targetWidth; height = targetHeight;
            compositorNeedleSupported = independentNeedle && !cpuRendering;
            RefreshPresentationRate(true);
            cacheDialOnCpu = cpuRendering;
            const D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 };
            D3D_FEATURE_LEVEL obtained{};
            // Use only the explicitly selected driver; failure must not silently change modes.
            CHECK_HR(D3D11CreateDevice(nullptr, cpuRendering ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                requested, ARRAYSIZE(requested), D3D11_SDK_VERSION, &device, &obtained, &context));
            ComPtr<IDXGIDevice> dxgiDevice;
            ComPtr<IDXGIAdapter> adapter;
            ComPtr<IDXGIFactory2> factory;
            CHECK_HR(device.As(&dxgiDevice));
            CHECK_HR(dxgiDevice->GetAdapter(&adapter));
            CHECK_HR(adapter->GetParent(IID_PPV_ARGS(&factory)));
            if (!cpuRendering)
            {
                ComPtr<IDXGIFactory5> factory5;
                tearingSupportResult = factory.As(&factory5);
                if (SUCCEEDED(tearingSupportResult))
                {
                    BOOL supported = FALSE;
                    tearingSupportResult = factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING,
                        &supported, sizeof(supported));
                    tearingSupported = tearingSupportResult == S_OK && supported != FALSE;
                }
            }
            swapChainFlags = WispTearingPolicy::SwapChainFlags(cpuRendering, tearingSupportResult, tearingSupported);
            DXGI_SWAP_CHAIN_DESC1 description{};
            description.Width = width; description.Height = AtlasHeight();
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            description.BufferCount = SwapChainBufferCount;
            description.Scaling = DXGI_SCALING_STRETCH;
            description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            description.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
            description.Flags = swapChainFlags;
            ComPtr<IDXGISwapChain1> chain;
            CHECK_HR(factory->CreateSwapChainForComposition(device.Get(), &description, nullptr, &chain));
            CHECK_HR(chain.As(&swapChain));
            CHECK_HR(swapChain->SetMaximumFrameLatency(MaximumFrameLatency));
            latency = swapChain->GetFrameLatencyWaitableObject();
            if (!latency) return HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE);
            CHECK_HR(CreateTarget());
            CHECK_HR(DCompositionCreateDevice2(dxgiDevice.Get(), IID_PPV_ARGS(&composition)));
            CHECK_HR(composition->CreateTargetForHwnd(hwnd, TRUE, &target));
            CHECK_HR(composition->CreateVisual(&visual));
            ComPtr<IDCompositionVisual2> visual2;
            CHECK_HR(visual.As(&visual2));
            // Split visuals must flatten before applying group opacity, preserving
            // the old flattened-HUD result where the needle overlaps other artwork.
            CHECK_HR(visual2->SetOpacityMode(compositorNeedleSupported ?
                DCOMPOSITION_OPACITY_MODE_LAYER : DCOMPOSITION_OPACITY_MODE_MULTIPLY));
            CHECK_HR(composition->CreateEffectGroup(&opacityEffect));
            CHECK_HR(opacityEffect->SetOpacity(0.0f));
            CHECK_HR(visual->SetEffect(opacityEffect.Get()));
            if (compositorNeedleSupported)
            {
                CHECK_HR(composition->CreateVisual(&underlayVisual));
                CHECK_HR(composition->CreateVisual(&needleVisual));
                CHECK_HR(composition->CreateVisual(&foregroundVisual));
                CHECK_HR(composition->CreateEffectGroup(&needleOpacityEffect));
                CHECK_HR(needleVisual->SetEffect(needleOpacityEffect.Get()));
                CHECK_HR(composition->CreateScaleTransform(&needleScale));
                CHECK_HR(composition->CreateRotateTransform(&needleRotation));
                CHECK_HR(composition->CreateMatrixTransform(&needleParent));
                IDCompositionTransform* transforms[]{needleScale.Get(), needleRotation.Get(), needleParent.Get()};
                ComPtr<IDCompositionTransform> group;
                CHECK_HR(composition->CreateTransformGroup(transforms, ARRAYSIZE(transforms), &group));
                CHECK_HR(needleVisual->SetTransform(group.Get()));
                CHECK_HR(needleOpacityEffect->SetOpacity(0.0f));
                CHECK_HR(visual->AddVisual(underlayVisual.Get(), FALSE, nullptr));
                CHECK_HR(visual->AddVisual(needleVisual.Get(), TRUE, underlayVisual.Get()));
                CHECK_HR(visual->AddVisual(foregroundVisual.Get(), TRUE, needleVisual.Get()));
                CHECK_HR(SetAtlasClips());
            }
            CHECK_HR(BindCompositionContent(swapChain.Get()));
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
            CHECK_HR(device->CreatePixelShader(ElectricNeedlePixel, sizeof(ElectricNeedlePixel), nullptr, &pixelShaders[3]));
            CHECK_HR(device->CreatePixelShader(ImageSectorPixel, sizeof(ImageSectorPixel), nullptr, &pixelShaders[4]));
            CHECK_HR(device->CreatePixelShader(DigitalGaugePixel, sizeof(DigitalGaugePixel), nullptr, &pixelShaders[5]));
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
            if (compositorNeedleSupported)
            {
                rasterDescription.ScissorEnable = TRUE;
                CHECK_HR(device->CreateRasterizerState(&rasterDescription, &atlasRasterizer));
            }
            const uint8_t white[] = {255,255,255,255};
            CHECK_HR(Upload(0, 1, 1, 4, white, sizeof(white)));
            CHECK_HR(WaitForInitialFrame(latency));
            const float clear[4]{};
            context->ClearRenderTargetView(renderTarget.Get(), clear);
            CHECK_HR(InitialPresentationResult(PresentChain(swapChain.Get(), swapChainFlags, 0)));
            CHECK_HR(composition->Commit());
            if (!cpuRendering) ConfigureGpuPriority(dxgiDevice.Get());
            return S_OK;
        }

        HRESULT PrepareForResume()
        {
            CHECK_HR(CheckThread());
            CloseMotionGate();
            if (visible) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
            pacer.Reset();
            RefreshPresentationRate(true);
            pendingPresentation = false;
            splitNeedleFrame = false;
            compositorNeedleEnabled = pendingNeedleSplit = false;
            needleVisible = false;
            pendingNeedleGeometryGeneration = presentedNeedleGeometryGeneration = 0;
            needleOpacityAnimation.Reset();
            if (needleVisual) { CHECK_HR(needleOpacityEffect->SetOpacity(0.0f)); }
            dialCacheValid = false;
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
            status = WaitForInitialFrame(replacementLatency);
            if (FAILED(status)) { CloseHandle(replacementLatency); return status; }
            const float clear[4]{};
            context->ClearRenderTargetView(replacementTarget.Get(), clear);
            status = InitialPresentationResult(PresentChain(replacement.Get(), description.Flags, 0));
            if (FAILED(status)) { CloseHandle(replacementLatency); return status; }
            status = BindCompositionContent(replacement.Get());
            if (SUCCEEDED(status)) status = composition->Commit();
            if (FAILED(status)) { CloseHandle(replacementLatency); return status; }
            context->OMSetRenderTargets(0, nullptr, nullptr);
            renderTarget = std::move(replacementTarget);
            swapChain = std::move(replacement);
            swapChainFlags = description.Flags;
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
            CloseMotionGate();
            if (!ValidSize(targetWidth, targetHeight) || !std::isfinite(offsetX) || !std::isfinite(offsetY)) return E_INVALIDARG;
            captureReady = false;
            pendingPresentation = false;
            compositorNeedleEnabled = splitNeedleFrame = pendingNeedleSplit = false;
            needleVisible = false;
            pendingNeedleGeometryGeneration = presentedNeedleGeometryGeneration = 0;
            needleOpacityAnimation.Reset();
            if (needleVisual) { CHECK_HR(needleOpacityEffect->SetOpacity(0.0f)); }
            dialCacheValid = false;
            if (width != targetWidth || height != targetHeight)
            {
                dialCache.Reset();
                dialCacheUnavailable = false;
                context->OMSetRenderTargets(0, nullptr, nullptr);
                renderTarget.Reset();
                CHECK_HR(swapChain->ResizeBuffers(SwapChainBufferCount, targetWidth,
                    compositorNeedleSupported ? targetHeight * 2u : targetHeight, DXGI_FORMAT_B8G8R8A8_UNORM,
                    swapChainFlags));
                width = targetWidth; height = targetHeight;
                splitNeedleFrame = false;
                if (needleVisual) { CHECK_HR(needleOpacityEffect->SetOpacity(0.0f)); }
                CHECK_HR(SetAtlasClips());
                CHECK_HR(CreateTarget());
                RefreshPresentationRate(true);
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
            if (id == 0) needleBitmapValid = false;
            dialCacheValid = false;
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
                if (command.shader >= pixelShaders.size() || textures.find(command.textureId) == textures.end()
                    || command.tintA < 0 || command.tintA > 1 || command.tintR < 0 || command.tintR > 1
                    || command.tintG < 0 || command.tintG > 1 || command.tintB < 0 || command.tintB > 1
                    || (command.shader == 1 && command.parameterY <= 0)) return E_INVALIDARG;
            }
            captureReady = false;
            uint32_t needleIndex = count, needles = 0;
            if (forPresentation && compositorNeedleSupported && compositorNeedleEnabled &&
                (motion || Counter() < needleFreshUntil))
                for (uint32_t index = 0; index < count; ++index)
                    if (commands[index].shader == 2 && commands[index].parameterW == 1) { needleIndex = index; ++needles; }
            pendingNeedleSplit = needles == 1;
            pendingNeedleGeometryGeneration = pendingNeedleSplit ? needleGeometryGeneration : 0;
            const float drawHeight = static_cast<float>(forPresentation ? AtlasHeight() : height);
            const bool cacheDial = PrepareDialCache(commands, count);
            const bool reuseDial = cacheDial && dialCacheValid &&
                std::memcmp(&cachedDial, commands, sizeof(cachedDial)) == 0;
            ComPtr<ID3D11Resource> frameBuffer;
            if (cacheDial) renderTarget->GetResource(&frameBuffer);
            const float clear[4]{};
            if (reuseDial)
            {
                // Copy exact target pixels: no second sampling pass or changed AA.
                context->OMSetRenderTargets(0, nullptr, nullptr);
                context->CopyResource(frameBuffer.Get(), dialCache.Get());
            }
            else
            {
                context->ClearRenderTargetView(renderTarget.Get(), clear);
                if (cacheDial) dialCacheValid = false;
            }
            ID3D11RenderTargetView* targetView = renderTarget.Get();
            BindPipeline(targetView, static_cast<float>(width), drawHeight);
            if (forPresentation && compositorNeedleSupported) context->RSSetState(atlasRasterizer.Get());
            if (metrics) metrics->setupTicks = Counter() - drawTimer.Started();
            for (uint32_t index = reuseDial ? 1u : 0u; index < count; ++index)
            {
                if (pendingNeedleSplit && index == needleIndex) continue;
                auto c = commands[index];
                const LONG top = pendingNeedleSplit && index > needleIndex ? static_cast<LONG>(height) : 0;
                if (forPresentation && compositorNeedleSupported)
                {
                    const D3D11_RECT clip{0, top, static_cast<LONG>(width), top + static_cast<LONG>(height)};
                    context->RSSetScissorRects(1, &clip);
                    c.originY += static_cast<float>(top);
                }
                CHECK_HR(DrawCommand(c, static_cast<float>(width), drawHeight, metrics));
                if (cacheDial && index == 0)
                {
                    // Only the first dial is static; every later quad remains live.
                    context->OMSetRenderTargets(0, nullptr, nullptr);
                    context->CopyResource(dialCache.Get(), frameBuffer.Get());
                    context->OMSetRenderTargets(1, &targetView, nullptr);
                    cachedDial = commands[0];
                    dialCacheValid = true;
                }
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
            const HRESULT result = PresentChain(swapChain.Get(), swapChainFlags, presentationSyncInterval);
            if (result == S_OK)
            {
                pendingPresentation = false;
                if (presentationSyncInterval == 0 && !pacer.AdvanceAfterPresent(Counter()))
                    DisablePacing(HRESULT_FROM_WIN32(GetLastError()));
                if (compositorNeedleSupported)
                {
                    const bool previousSplit = splitNeedleFrame;
                    splitNeedleFrame = pendingNeedleSplit;
                    presentedNeedleGeometryGeneration = pendingNeedleGeometryGeneration;
                    const bool showNeedle = splitNeedleFrame && compositorNeedleEnabled && (motion || needleOpacityAnimation) &&
                        presentedNeedleGeometryGeneration == needleGeometryGeneration &&
                        (!motion || motion->committedGeneration.load() == motionPreparationGeneration);
                    if (previousSplit != splitNeedleFrame || needleVisible != showNeedle)
                    {
                        if (showNeedle && motion) { CHECK_HR(needleOpacityEffect->SetOpacity(1.0f)); }
                        else if (showNeedle) { CHECK_HR(needleOpacityEffect->SetOpacity(needleOpacityAnimation.Get())); }
                        else { CHECK_HR(needleOpacityEffect->SetOpacity(0.0f)); }
                        CHECK_HR(composition->Commit());
                        needleVisible = showNeedle;
                    }
                    PublishMotionGate(showNeedle);
                }
            }
            else if (result == DXGI_STATUS_OCCLUDED) pacer.Reset();
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

    template<class Operation> HRESULT InvokeMotion(void* pointer, Operation operation) noexcept
    {
        if (!pointer) return E_POINTER;
        try { return operation(*static_cast<MotionHandle*>(pointer)->channel); }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }

    HRESULT WaitForFrame(void* renderer, uint32_t timeout, HANDLE cancellation,
        uint32_t* result, WispWaitMetrics* metrics, WispWaitMetricsV2* pacedMetrics = nullptr) noexcept
    {
        if (metrics)
        {
            metrics->cpuTime100ns = -1;
            metrics->waitResult = WAIT_FAILED;
        }
        if (pacedMetrics)
        {
            pacedMetrics->syncInterval = 1;
            pacedMetrics->pacingHResult = E_PENDING;
            pacedMetrics->pacingWaitResult = WAIT_FAILED;
        }
        CpuTimer totalTimer(metrics ? &metrics->totalTicks : nullptr);
        ThreadCpuTimer cpuTimer(metrics ? &metrics->cpuTime100ns : nullptr);
        if (!result || timeout > 1000) return E_INVALIDARG;
        const int64_t started = Counter();
        return Invoke(renderer, [&](Renderer& r) {
            const auto recordPacingState = [&] {
                if (!pacedMetrics) return;
                pacedMetrics->syncInterval = r.presentationSyncInterval;
                pacedMetrics->refreshRate = r.presentationRefreshRate;
                pacedMetrics->pacingHResult = r.pacingStatus;
            };
            const auto remainingTimeout = [&](DWORD& remaining) -> HRESULT {
                if (r.pacer.Frequency() <= 0) { remaining = timeout; return S_OK; }
                return FramePacerMath::RemainingMilliseconds(started, Counter(), r.pacer.Frequency(), timeout, remaining)
                    ? S_OK : HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            };
            {
                CpuTimer precheckTimer(metrics ? &metrics->precheckTicks : nullptr);
                CHECK_HR(r.CheckThread());
                recordPacingState();
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
                r.RefreshPresentationRate();
                recordPacingState();
            }
            FrameWaitBudget::Scope budgetScope{r.waitBudget};
            CHECK_HR(r.waitBudget.Arm(started, r.pacer.Frequency(), timeout));
            const HANDLE budgetDeadline = r.waitBudget.Handle();
            // Do not consume the DXGI readiness permit until the rate gate is
            // open. Both waits share the original finite timeout budget.
            if (r.presentationSyncInterval == 0)
            {
                DWORD paced;
                DWORD pacingError = ERROR_SUCCESS;
                {
                    CpuTimer pacingTimer(pacedMetrics ? &pacedMetrics->pacingTicks : nullptr);
                    DWORD remaining = 0;
                    CHECK_HR(remainingTimeout(remaining));
                    paced = r.pacer.Wait(remaining, cancellation, budgetDeadline);
                    if (paced == WAIT_FAILED) pacingError = GetLastError();
                }
                if (pacedMetrics) pacedMetrics->pacingWaitResult = paced;
                if (paced == WAIT_OBJECT_0 + 1) { *result = 2; return S_OK; }
                if (paced == WAIT_TIMEOUT) { *result = 1; return S_OK; }
                if (paced != WAIT_OBJECT_0)
                {
                    r.DisablePacing(paced == WAIT_FAILED ? HRESULT_FROM_WIN32(pacingError) : E_UNEXPECTED);
                    recordPacingState();
                }
            }
            HANDLE handles[3]{};
            DWORD handleCount = 0;
            if (cancellation) handles[handleCount++] = cancellation;
            handles[handleCount++] = r.latency;
            const DWORD budgetIndex = handleCount;
            if (budgetDeadline) handles[handleCount++] = budgetDeadline;
            DWORD waited;
            DWORD waitError = ERROR_SUCCESS;
            {
                CpuTimer waitTimer(metrics ? &metrics->waitCallTicks : nullptr);
                DWORD remaining = 0;
                CHECK_HR(remainingTimeout(remaining));
                waited = WaitForMultipleObjects(handleCount, handles, FALSE, remaining);
                // Preserve the original failure before diagnostic clock calls run.
                if (waited == WAIT_FAILED) waitError = GetLastError();
                else if (budgetDeadline && waited == WAIT_OBJECT_0 + budgetIndex) waited = WAIT_TIMEOUT;
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
    return WispRendererCreateWithMode(hwnd, width, height, 0, output);
}
HRESULT __cdecl WispRendererCreateWithMode(HWND hwnd, uint32_t width, uint32_t height, uint32_t cpuRendering, void** output) noexcept
{
    if (!output) return E_POINTER;
    *output = nullptr;
    if (cpuRendering > 1) return E_INVALIDARG;
    try {
        auto renderer = std::make_unique<Renderer>();
        CHECK_HR(renderer->Initialize(hwnd, width, height, cpuRendering != 0));
        *output = renderer.release();
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
HRESULT __cdecl WispRendererCreateWithCompositorNeedle(HWND hwnd, uint32_t width, uint32_t height,
    uint32_t cpuRendering, void** output) noexcept
{
    if (!output) return E_POINTER;
    *output = nullptr;
    if (cpuRendering > 1) return E_INVALIDARG;
    try {
        auto renderer = std::make_unique<Renderer>();
        CHECK_HR(renderer->Initialize(hwnd, width, height, cpuRendering != 0, true));
        *output = renderer.release();
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
HRESULT __cdecl WispRendererUpdateCompositorNeedle(void* renderer,
    const WispCompositorNeedleGeometry* geometry, int64_t begin, int64_t freshUntil,
    const WispCompositorNeedlePoint* points, uint32_t count) noexcept
{
    return Invoke(renderer, [&](Renderer& r) { return r.UpdateCompositorNeedle(geometry, begin, freshUntil, points, count); });
}
HRESULT __cdecl WispRendererCreateMotionChannel(void* renderer, void** channel) noexcept
{
    if (!channel) return E_POINTER;
    *channel = nullptr;
    return Invoke(renderer, [&](Renderer& r) { return r.CreateMotionChannel(channel); });
}
HRESULT __cdecl WispRendererPrepareCompositorNeedleMotion(void* renderer,
    const WispCompositorNeedleGeometry* geometry, int64_t begin, int64_t freshUntil,
    const WispCompositorNeedlePoint* points, uint32_t count, uint64_t generation) noexcept
{
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        if (!r.motion) return S_FALSE;
        if (!geometry || !generation) return E_INVALIDARG;
        auto previous = r.needleGeometry;
        auto next = *geometry;
        previous.command.parameterX = next.command.parameterX = 0;
        const uint64_t clearEpoch = r.motion->clearEpoch.load();
        if (r.motionPreparationGeneration != generation || r.motionPreparationClearEpoch != clearEpoch ||
            std::memcmp(&previous, &next, sizeof(next)) != 0)
        {
            // Hide on the content device before potentially blocking bitmap work.
            // An in-flight motion commit cannot override this parent gate.
            r.CloseMotionGate();
            CHECK_HR(r.needleOpacityEffect->SetOpacity(0.0f));
            CHECK_HR(r.composition->Commit());
            r.needleVisible = false;
            r.compositorNeedleEnabled = false;
            r.motionPreparationGeneration = generation;
            r.motionPreparationClearEpoch = clearEpoch;
        }
        return r.UpdateCompositorNeedle(geometry, begin, freshUntil, points, count);
    });
}
HRESULT __cdecl WispMotionChannelUpdate(void* channel,
    const WispCompositorNeedleGeometry* geometry, int64_t begin, int64_t freshUntil,
    const WispCompositorNeedlePoint* points, uint32_t count, uint64_t generation, WispMotionStatus* status) noexcept
{
    if (status) *status = {};
    return InvokeMotion(channel, [&](MotionChannel& motion) {
        return motion.Update(geometry, begin, freshUntil, points, count, generation, status);
    });
}
HRESULT __cdecl WispMotionChannelClear(void* channel) noexcept
{ return InvokeMotion(channel, [&](MotionChannel& motion) { return motion.Clear(); }); }
void __cdecl WispMotionChannelDestroy(void* channel) noexcept
{ delete static_cast<MotionHandle*>(channel); }
HRESULT __cdecl WispRendererGetCompositorNeedleStatus(void* renderer, WispCompositorNeedleStatus* status) noexcept
{
    if (!status) return E_POINTER;
    *status = {};
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        *status = {r.compositorNeedleSupported ? 1u : 0u, r.compositorNeedleEnabled ? 1u : 0u,
            r.splitNeedleFrame ? 1u : 0u, r.AtlasHeight(), r.needleWidth, r.needleHeight, r.needlePointCount, r.needleVisible ? 1u : 0u,
            r.needleUpdates, r.presentCalls, r.needleBegin, r.needleEnd};
        return S_OK;
    });
}
HRESULT __cdecl WispRendererCaptureCompositorLayer(void* renderer, uint32_t layer,
    uint8_t* pixels, uint32_t bytes, uint32_t stride) noexcept
{
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        if (!r.compositorNeedleSupported || layer > 2 || !pixels) return E_INVALIDARG;
        if (layer == 1)
        {
            if (!r.compositorNeedleEnabled || !r.needleSurface) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
            CHECK_HR(r.DrawNeedleBitmap(pixels, bytes, stride));
            return r.composition->Commit();
        }
        if (!r.pendingPresentation && !r.captureReady) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        ComPtr<ID3D11Texture2D> source;
        CHECK_HR(r.swapChain->GetBuffer(0, IID_PPV_ARGS(&source)));
        r.context->OMSetRenderTargets(0, nullptr, nullptr);
        return r.ReadRegion(source.Get(), 0, layer == 2 ? r.height : 0, r.width, r.height, pixels, bytes, stride);
    });
}
HRESULT __cdecl WispRendererGetCompositorNeedleBitmapDrawCount(void* renderer, uint64_t* count) noexcept
{
    if (!count) return E_POINTER;
    *count = 0;
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        *count = r.needleBitmapDraws;
        return S_OK;
    });
}
void __cdecl WispRendererDestroy(void* renderer) noexcept { delete static_cast<Renderer*>(renderer); }
HRESULT __cdecl WispRendererGetGpuPriorityStatus(void* renderer, WispGpuPriorityStatus* status) noexcept
{
    if (!status) return E_POINTER;
    *status = UnattemptedGpuPriorityStatus();
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        *status = r.gpuPriorityStatus;
        return S_OK;
    });
}
HRESULT __cdecl WispRendererGetPresentationStatus(void* renderer, WispPresentationStatus* status) noexcept
{
    if (!status) return E_POINTER;
    *status = {};
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        DXGI_SWAP_CHAIN_DESC1 description{};
        UINT maximumLatency = 0;
        CHECK_HR(r.swapChain->GetDesc1(&description));
        CHECK_HR(r.swapChain->GetMaximumFrameLatency(&maximumLatency));
        *status = {r.tearingSupported ? 1u : 0u, r.tearingSupportResult,
            description.Flags, description.BufferCount, maximumLatency,
            r.presentationSyncInterval, WispTearingPolicy::PresentFlags(description.Flags, r.presentationSyncInterval),
            r.lastPresentSyncInterval, r.lastPresentFlags, r.lastPresentResult};
        return S_OK;
    });
}
HRESULT __cdecl WispRendererPrepareForResume(void* renderer) noexcept
{ return Invoke(renderer, [&](Renderer& r) { return r.PrepareForResume(); }); }
HRESULT __cdecl WispRendererSetOpacity(void* renderer, float opacity) noexcept
{ return Invoke(renderer, [&](Renderer& r) { CHECK_HR(r.CheckThread()); if (!std::isfinite(opacity) || opacity < 0 || opacity > 1) return E_INVALIDARG; if (r.opacity == opacity) return S_OK; if (r.visible) { CHECK_HR(r.opacityEffect->SetOpacity(opacity)); CHECK_HR(r.composition->Commit()); } r.opacity = opacity; return S_OK; }); }
HRESULT __cdecl WispRendererSetVisible(void* renderer, int visible) noexcept
{
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        const bool show = visible != 0;
        if (!show)
        {
            r.CloseMotionGate();
            r.pendingPresentation = false; r.pacer.Reset();
            r.compositorNeedleEnabled = r.splitNeedleFrame = r.pendingNeedleSplit = false;
            r.needleVisible = false;
            r.pendingNeedleGeometryGeneration = r.presentedNeedleGeometryGeneration = 0;
            r.needleOpacityAnimation.Reset();
            if (r.needleVisual) { CHECK_HR(r.needleOpacityEffect->SetOpacity(0.0f)); }
        }
        if (r.visible == show) return S_OK;
        CHECK_HR(r.opacityEffect->SetOpacity(show ? r.opacity : 0.0f));
        CHECK_HR(r.composition->Commit());
        r.visible = show;
        if (show) r.RefreshPresentationRate(true);
        return S_OK;
    });
}
HRESULT __cdecl WispRendererResize(void* renderer, uint32_t width, uint32_t height, float x, float y) noexcept
{ return Invoke(renderer, [&](Renderer& r) { return r.Resize(width,height,x,y); }); }
HRESULT __cdecl WispRendererSetOffset(void* renderer, float x, float y) noexcept
{
    return Invoke(renderer, [&](Renderer& r) {
        CHECK_HR(r.CheckThread());
        if (!std::isfinite(x) || !std::isfinite(y)) return E_INVALIDARG;
        CHECK_HR(r.visual->SetOffsetX(x));
        CHECK_HR(r.visual->SetOffsetY(y));
        return r.composition->Commit();
    });
}
HRESULT __cdecl WispRendererUploadTexture(void* renderer, uint32_t id, uint32_t width, uint32_t height,
    uint32_t stride, const uint8_t* pixels, uint32_t bytes) noexcept
{ if (!id) return E_INVALIDARG; return Invoke(renderer, [&](Renderer& r) { return r.Upload(id,width,height,stride,pixels,bytes); }); }
HRESULT __cdecl WispRendererRemoveTexture(void* renderer, uint32_t id) noexcept
{ return Invoke(renderer, [&](Renderer& r) { CHECK_HR(r.CheckThread()); if (!id) return E_INVALIDARG; r.textures.erase(id); r.dialCacheValid = false; return S_OK; }); }
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
HRESULT __cdecl WispRendererWaitForFrameMeasuredV2(void* renderer, uint32_t timeout, HANDLE cancellation,
    int measure, uint32_t* result, WispWaitMetricsV2* metrics) noexcept
{
    if (!metrics) return E_POINTER;
    *metrics = {};
    return WaitForFrame(renderer, timeout, cancellation, result,
        measure ? &metrics->wait : nullptr, measure ? metrics : nullptr);
}
HRESULT __cdecl WispRendererCapture(void* renderer, uint8_t* pixels, uint32_t bytes, uint32_t stride) noexcept
{ return Invoke(renderer, [&](Renderer& r) { return r.Capture(pixels,bytes,stride); }); }
HRESULT __cdecl WispRendererDeviceRemovedReason(void* renderer) noexcept
{ return Invoke(renderer, [&](Renderer& r) { CHECK_HR(r.CheckThread()); return r.device->GetDeviceRemovedReason(); }); }
