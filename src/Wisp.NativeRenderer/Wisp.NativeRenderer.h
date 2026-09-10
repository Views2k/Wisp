#pragma once
#include <windows.h>
#include <cstdint>

struct WispDrawCommand
{
    uint32_t textureId, shader;
    float originX, originY, axisXX, axisXY, axisYX, axisYY;
    float uvLeft, uvTop, uvRight, uvBottom;
    float tintR, tintG, tintB, tintA;
    float parameterX, parameterY, parameterZ, parameterW;
};
static_assert(sizeof(WispDrawCommand) == 80, "Managed/native draw ABI mismatch.");

struct WispDrawMetrics
{
    int64_t totalTicks, setupTicks, mapTicks, maximumMapTicks;
    uint32_t mapCount, drawCount;
};
struct WispPresentMetrics
{
    int64_t durationTicks;
    int32_t hResult;
    uint32_t reserved;
};
struct WispWaitMetrics
{
    int64_t totalTicks, precheckTicks, waitCallTicks, postcheckTicks, cpuTime100ns;
    uint32_t swapChainGeneration, waitResult;
};
static_assert(sizeof(WispDrawMetrics) == 40, "Managed/native draw metrics ABI mismatch.");
static_assert(sizeof(WispPresentMetrics) == 16, "Managed/native present metrics ABI mismatch.");
static_assert(sizeof(WispWaitMetrics) == 48, "Managed/native wait metrics ABI mismatch.");

#ifdef WISP_RENDERER_IMPORT
#define WISP_API extern "C" __declspec(dllimport)
#else
#define WISP_API extern "C" __declspec(dllexport)
#endif
WISP_API HRESULT __cdecl WispRendererCreate(HWND hwnd, uint32_t width, uint32_t height, void** renderer) noexcept;
WISP_API void __cdecl WispRendererDestroy(void* renderer) noexcept;
// S_OK replaces the swapchain; S_FALSE preserves an already fresh chain.
WISP_API HRESULT __cdecl WispRendererPrepareForResume(void* renderer) noexcept;
WISP_API HRESULT __cdecl WispRendererSetOpacity(void* renderer, float opacity) noexcept;
WISP_API HRESULT __cdecl WispRendererSetVisible(void* renderer, int visible) noexcept;
WISP_API HRESULT __cdecl WispRendererResize(void* renderer, uint32_t width, uint32_t height, float offsetX, float offsetY) noexcept;
WISP_API HRESULT __cdecl WispRendererUploadTexture(void* renderer, uint32_t id, uint32_t width, uint32_t height,
    uint32_t stride, const uint8_t* pixels, uint32_t byteCount) noexcept;
WISP_API HRESULT __cdecl WispRendererRemoveTexture(void* renderer, uint32_t id) noexcept;
WISP_API HRESULT __cdecl WispRendererRender(void* renderer, const WispDrawCommand* commands, uint32_t count, int present) noexcept;
// Optional CPU timings use QPC ticks. Disabled measurement returns zeroed metrics without reading QPC.
WISP_API HRESULT __cdecl WispRendererDrawForPresentation(void* renderer, const WispDrawCommand* commands,
    uint32_t count, int measure, WispDrawMetrics* metrics) noexcept;
// A busy/occluded result retains the drawn frame for retry; successful presentation consumes it.
WISP_API HRESULT __cdecl WispRendererTryPresent(void* renderer, int measure, WispPresentMetrics* metrics) noexcept;
WISP_API HRESULT __cdecl WispRendererWaitForFrame(void* renderer, uint32_t timeoutMilliseconds, HANDLE cancellation, uint32_t* result) noexcept;
// Wall times use QPC ticks; coarse thread execution time uses 100 ns units (-1 if unavailable).
// waitResult is the raw Win32 wait result, or WAIT_FAILED before a wait/error.
WISP_API HRESULT __cdecl WispRendererWaitForFrameMeasured(void* renderer, uint32_t timeoutMilliseconds,
    HANDLE cancellation, int measure, uint32_t* result, WispWaitMetrics* metrics) noexcept;
WISP_API HRESULT __cdecl WispRendererCapture(void* renderer, uint8_t* pixels, uint32_t byteCount, uint32_t stride) noexcept;
WISP_API HRESULT __cdecl WispRendererDeviceRemovedReason(void* renderer) noexcept;
