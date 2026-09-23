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
// Versioned extension: intentional pacing is separate from the DXGI wait.
struct WispWaitMetricsV2
{
    WispWaitMetrics wait;
    int64_t pacingTicks;
    uint32_t syncInterval, refreshRate;
    int32_t pacingHResult;
    uint32_t pacingWaitResult;
};
// Stored initialization results, not a live query. Process statuses are raw
// NTSTATUS values; INT32_MIN means not attempted. Device statuses are HRESULTs
// (S_FALSE when not attempted). Effective values stay -1/INT32_MIN if unread.
// attempted is zero for WARP; requested values describe the hardware test only.
struct WispGpuPriorityStatus
{
    uint32_t attempted;
    int32_t requestedProcessClass, processSetStatus, processReadStatus, effectiveProcessClass;
    int32_t requestedDevicePriority, deviceSetHResult, deviceReadHResult, effectiveDevicePriority;
};
// Owner-thread diagnostic snapshot. Chain fields come from DXGI; lastPresent*
// records the most recent actual Present call, including transparent primers.
// Feature-query failure or WARP (S_FALSE, unsupported) keeps baseline flags.
struct WispPresentationStatus
{
    uint32_t tearingSupported;
    int32_t tearingSupportHResult;
    uint32_t swapChainFlags, bufferCount, maximumFrameLatency;
    uint32_t syncInterval, presentFlags, lastPresentSyncInterval, lastPresentFlags;
    int32_t lastPresentHResult;
};
static_assert(sizeof(WispDrawMetrics) == 40, "Managed/native draw metrics ABI mismatch.");
static_assert(sizeof(WispPresentMetrics) == 16, "Managed/native present metrics ABI mismatch.");
static_assert(sizeof(WispWaitMetrics) == 48, "Managed/native wait metrics ABI mismatch.");
static_assert(sizeof(WispWaitMetricsV2) == 72, "Managed/native paced wait metrics ABI mismatch.");
static_assert(sizeof(WispGpuPriorityStatus) == 36, "Managed/native GPU priority ABI mismatch.");
static_assert(sizeof(WispPresentationStatus) == 40, "Native presentation status ABI mismatch.");

struct WispCompositorNeedleGeometry
{
    WispDrawCommand command;
    float pivotX, pivotY;
    float parentM11, parentM12, parentM21, parentM22, parentOffsetX, parentOffsetY;
    float opacity;
    uint32_t reserved;
};
struct WispCompositorNeedlePoint { double offsetSeconds, angle, blur; };
struct WispCompositorNeedleStatus
{
    // visible reports the activation gate, not final DWM adoption or display.
    uint32_t supported, enabled, split, atlasHeight, surfaceWidth, surfaceHeight, pointCount, visible;
    uint64_t updates, presents;
    int64_t beginTimestamp, endTimestamp;
};
static_assert(sizeof(WispCompositorNeedleGeometry) == 120, "Compositor needle geometry ABI mismatch.");
static_assert(sizeof(WispCompositorNeedlePoint) == 24, "Compositor needle point ABI mismatch.");
static_assert(sizeof(WispCompositorNeedleStatus) == 64, "Compositor needle status ABI mismatch.");
struct WispMotionStatus
{
    int64_t beginTimestamp, endTimestamp, freshUntilTimestamp, commitTimestamp;
    uint64_t commits, bitmapDraws;
    uint32_t pointCount, geometryAccepted;
};
static_assert(sizeof(WispMotionStatus) == 56, "Independent motion status ABI mismatch.");

#ifdef WISP_RENDERER_IMPORT
#define WISP_API extern "C" __declspec(dllimport)
#else
#define WISP_API extern "C" __declspec(dllexport)
#endif
WISP_API HRESULT __cdecl WispRendererCreate(HWND hwnd, uint32_t width, uint32_t height, void** renderer) noexcept;
// cpuRendering: 0 = hardware, 1 = WARP; no automatic driver fallback.
WISP_API HRESULT __cdecl WispRendererCreateWithMode(HWND hwnd, uint32_t width, uint32_t height, uint32_t cpuRendering, void** renderer) noexcept;
// Explicit hardware-only atlas; existing creation entry points keep their original allocation.
WISP_API HRESULT __cdecl WispRendererCreateWithCompositorNeedle(HWND hwnd, uint32_t width, uint32_t height, uint32_t cpuRendering, void** renderer) noexcept;
// Owner thread, independent of frame readiness. Null geometry/count zero disables.
// Curves use absolute QPC plus increasing second offsets and stop at their last point.
// Signed blur is rasterized at point zero and held until the next update.
WISP_API HRESULT __cdecl WispRendererUpdateCompositorNeedle(void* renderer,
    const WispCompositorNeedleGeometry* geometry, int64_t absoluteStartTimestamp,
    int64_t freshUntilTimestamp, const WispCompositorNeedlePoint* points, uint32_t count) noexcept;
WISP_API HRESULT __cdecl WispRendererGetCompositorNeedleStatus(void* renderer, WispCompositorNeedleStatus* status) noexcept;
// Owner-thread contract counter; includes explicit bitmap readback redraws.
WISP_API HRESULT __cdecl WispRendererGetCompositorNeedleBitmapDrawCount(void* renderer, uint64_t* count) noexcept;
// Create/prepare belong to the renderer owner. Update/clear belong to one motion
// thread, bound on first use. Stop/join that thread before destroying its handle.
WISP_API HRESULT __cdecl WispRendererCreateMotionChannel(void* renderer, void** channel) noexcept;
WISP_API HRESULT __cdecl WispRendererPrepareCompositorNeedleMotion(void* renderer,
    const WispCompositorNeedleGeometry* geometry, int64_t beginTimestamp, int64_t freshUntilTimestamp,
    const WispCompositorNeedlePoint* points, uint32_t count, uint64_t generation) noexcept;
WISP_API HRESULT __cdecl WispMotionChannelUpdate(void* channel,
    const WispCompositorNeedleGeometry* geometry, int64_t beginTimestamp, int64_t freshUntilTimestamp,
    const WispCompositorNeedlePoint* points, uint32_t count, uint64_t generation, WispMotionStatus* status) noexcept;
WISP_API HRESULT __cdecl WispMotionChannelClear(void* channel) noexcept;
WISP_API void __cdecl WispMotionChannelDestroy(void* channel) noexcept;
// Contract readback: 0=atlas underlay, 1=needle bitmap, 2=atlas foreground.
// This does not capture the final compositor output or its resampling.
WISP_API HRESULT __cdecl WispRendererCaptureCompositorLayer(void* renderer, uint32_t layer,
    uint8_t* pixels, uint32_t byteCount, uint32_t stride) noexcept;
WISP_API void __cdecl WispRendererDestroy(void* renderer) noexcept;
// Owner-thread-only; querying never changes priorities. Failed queries return
// the not-attempted sentinel when a non-null output was supplied.
WISP_API HRESULT __cdecl WispRendererGetGpuPriorityStatus(void* renderer, WispGpuPriorityStatus* status) noexcept;
WISP_API HRESULT __cdecl WispRendererGetPresentationStatus(void* renderer, WispPresentationStatus* status) noexcept;
// S_OK replaces the swapchain; S_FALSE preserves an already fresh chain.
WISP_API HRESULT __cdecl WispRendererPrepareForResume(void* renderer) noexcept;
WISP_API HRESULT __cdecl WispRendererSetOpacity(void* renderer, float opacity) noexcept;
WISP_API HRESULT __cdecl WispRendererSetVisible(void* renderer, int visible) noexcept;
WISP_API HRESULT __cdecl WispRendererSetOffset(void* renderer, float offsetX, float offsetY) noexcept;
WISP_API HRESULT __cdecl WispRendererResize(void* renderer, uint32_t width, uint32_t height, float offsetX, float offsetY) noexcept;
WISP_API HRESULT __cdecl WispRendererUploadTexture(void* renderer, uint32_t id, uint32_t width, uint32_t height,
    uint32_t stride, const uint8_t* pixels, uint32_t byteCount) noexcept;
WISP_API HRESULT __cdecl WispRendererRemoveTexture(void* renderer, uint32_t id) noexcept;
WISP_API HRESULT __cdecl WispRendererRender(void* renderer, const WispDrawCommand* commands, uint32_t count, int present) noexcept;
// Optional CPU timings use QPC ticks. Disabled measurement returns zeroed metrics;
// the presentation pacer still uses its clock to enforce the rendering rate.
WISP_API HRESULT __cdecl WispRendererDrawForPresentation(void* renderer, const WispDrawCommand* commands,
    uint32_t count, int measure, WispDrawMetrics* metrics) noexcept;
// A busy/occluded result retains the drawn frame for retry; successful presentation consumes it.
WISP_API HRESULT __cdecl WispRendererTryPresent(void* renderer, int measure, WispPresentMetrics* metrics) noexcept;
WISP_API HRESULT __cdecl WispRendererWaitForFrame(void* renderer, uint32_t timeoutMilliseconds, HANDLE cancellation, uint32_t* result) noexcept;
// Wall times use QPC ticks; coarse thread execution time uses 100 ns units (-1 if unavailable).
// waitResult is the raw Win32 wait result, or WAIT_FAILED before a wait/error.
WISP_API HRESULT __cdecl WispRendererWaitForFrameMeasured(void* renderer, uint32_t timeoutMilliseconds,
    HANDLE cancellation, int measure, uint32_t* result, WispWaitMetrics* metrics) noexcept;
// The legacy 48-byte entry point remains intact for older diagnostic clients.
// syncInterval=1 is the safe fallback when timer/display-rate setup fails.
WISP_API HRESULT __cdecl WispRendererWaitForFrameMeasuredV2(void* renderer, uint32_t timeoutMilliseconds,
    HANDLE cancellation, int measure, uint32_t* result, WispWaitMetricsV2* metrics) noexcept;
WISP_API HRESULT __cdecl WispRendererCapture(void* renderer, uint8_t* pixels, uint32_t byteCount, uint32_t stride) noexcept;
WISP_API HRESULT __cdecl WispRendererDeviceRemovedReason(void* renderer) noexcept;
