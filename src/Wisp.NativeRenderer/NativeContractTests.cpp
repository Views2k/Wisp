#define WISP_RENDERER_IMPORT
#include "Wisp.NativeRenderer.h"
#include <cstdio>
#include <cstring>
#include <vector>
#include <thread>
#include <cmath>
#include <algorithm>
#include <dxgi.h>

static int failures = 0;
static void Require(bool passed, const char* name)
{
    if (!passed) { ++failures; std::printf("FAIL: %s\n", name); }
}
static WispDrawCommand Sprite(float x, float y, float width, float height, uint32_t texture = 0)
{
    WispDrawCommand command{};
    command.textureId = texture;
    command.originX = x; command.originY = y;
    command.axisXX = width; command.axisYY = height;
    command.uvRight = command.uvBottom = 1;
    command.tintR = command.tintG = command.tintB = command.tintA = 1;
    return command;
}

static void CheckVisiblePacing(void* renderer, HWND window)
{
    MONITORINFOEXW monitor{};
    monitor.cbSize = sizeof(monitor);
    DEVMODEW mode{};
    mode.dmSize = sizeof(mode);
    const bool hasRefresh = GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &monitor) &&
        EnumDisplaySettingsW(monitor.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1;
    Require(hasRefresh, "visible pacing has a known display refresh rate");
    if (!hasRefresh) return;

    Require(SUCCEEDED(WispRendererSetVisible(renderer, 0)) &&
        WispRendererPrepareForResume(renderer) == S_OK, "pacing starts with a fresh queue");
    Require(SUCCEEDED(WispRendererSetOpacity(renderer, .12f)), "pacing surface has low opacity");
    Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)), "pacing surface is visible");
    SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    Require(IsWindowVisible(window) && GetForegroundWindow() != window, "pacing window does not activate");

    LARGE_INTEGER frequency{}, started{}, now{};
    QueryPerformanceFrequency(&frequency);
    const double ticksPerSecond = static_cast<double>(frequency.QuadPart);
    QueryPerformanceCounter(&started);
    now = started;
    uint32_t submitted = 0, busy = 0, occluded = 0, timeouts = 0;
    bool ready = false, pending = false;
    std::vector<double> waits;
    waits.reserve(2048);
    WispWaitMetrics waitMetrics{};
    WispDrawMetrics drawMetrics{};
    WispPresentMetrics presentMetrics{};
    while (now.QuadPart - started.QuadPart < frequency.QuadPart * 3 && submitted < 10000)
    {
        if (!ready)
        {
            uint32_t waitResult = 99;
            const HRESULT result = WispRendererWaitForFrameMeasured(renderer, 100, nullptr, 1, &waitResult, &waitMetrics);
            Require(SUCCEEDED(result), "pacing wait remains healthy");
            if (FAILED(result)) break;
            waits.push_back(static_cast<double>(waitMetrics.totalTicks) * 1000.0 / ticksPerSecond);
            if (waitResult == 1) ++timeouts;
            else ready = waitResult == 0;
        }
        if (ready)
        {
            if (!pending)
            {
                auto sample = Sprite(64.0f + static_cast<float>(submitted % 80), 96, 24, 24);
                sample.tintR = .2f;
                sample.tintB = .7f;
                const HRESULT result = WispRendererDrawForPresentation(renderer, &sample, 1, 0, &drawMetrics);
                Require(SUCCEEDED(result), "pacing draw remains healthy");
                if (FAILED(result)) break;
                pending = true;
            }
            const HRESULT result = WispRendererTryPresent(renderer, 0, &presentMetrics);
            if (result == S_OK)
            {
                ++submitted;
                ready = pending = false;
            }
            else if (result == S_FALSE)
            {
                ++busy;
                Sleep(1);
            }
            else if (result == 2)
            {
                ++occluded;
                pending = false;
                Sleep(100);
            }
            else
            {
                Require(false, "pacing presentation remains healthy");
                break;
            }
        }
        MSG message{};
        while (PeekMessageW(&message, window, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        QueryPerformanceCounter(&now);
    }
    SetWindowPos(window, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    const double seconds = static_cast<double>(now.QuadPart - started.QuadPart) / ticksPerSecond;
    std::sort(waits.begin(), waits.end());
    const double p95 = waits.empty() ? 0 : waits[(waits.size() - 1) * 95 / 100];
    std::printf("pacingSeconds=%.3f;displayHz=%lu;submittedPerSecond=%.3f;waitP95Ms=%.3f;busy=%u;occluded=%u;timeouts=%u\n",
        seconds, mode.dmDisplayFrequency, submitted / seconds, p95, busy, occluded, timeouts);
    // This checks actual submissions, not displayed frames. Allow initial queue
    // credits and clock variance, but fail an accidentally unthrottled loop.
    Require(submitted >= 2, "visible pacing successfully submits frames");
    Require(submitted <= mode.dmDisplayFrequency * seconds * 1.25 + 4,
        "presentation throughput remains bounded by display cadence");
}

int main(int argc, char** argv)
{
    const bool cpuRendering = argc == 2 && std::strcmp(argv[1], "--cpu") == 0;
    if (argc > 1 && !cpuRendering) return 2;
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
        L"STATIC", L"Wisp native renderer contract", WS_POPUP, -32000, -32000, 288, 288,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Require(window != nullptr, "hidden owned window");
    if (!window) return 1;
    HWND foregroundBefore = GetForegroundWindow();
    void* renderer = nullptr;
    Require(WispRendererCreateWithMode(window, 32, 32, 2, &renderer) == E_INVALIDARG && !renderer, "invalid renderer mode rejected");
    HRESULT result = cpuRendering ? WispRendererCreateWithMode(window, 32, 32, 1, &renderer)
        : WispRendererCreate(window, 32, 32, &renderer);
    Require(SUCCEEDED(result) && renderer, "selected D3D11 driver and DirectComposition creation");
    if (!renderer) { DestroyWindow(window); return 1; }
    uint32_t initialReady = 99;
    WispWaitMetrics waitMetrics{};
    Require(SUCCEEDED(WispRendererWaitForFrameMeasured(renderer, 100, nullptr, 1, &initialReady, &waitMetrics)),
        "initial measured queue wait");
    Require(waitMetrics.totalTicks > 0 && waitMetrics.precheckTicks >= 0 && waitMetrics.waitCallTicks > 0 &&
        waitMetrics.postcheckTicks >= 0 && waitMetrics.totalTicks >=
        waitMetrics.precheckTicks + waitMetrics.waitCallTicks + waitMetrics.postcheckTicks &&
        waitMetrics.cpuTime100ns >= -1 && waitMetrics.swapChainGeneration == 1 &&
        waitMetrics.waitResult == (initialReady == 1 ? WAIT_TIMEOUT : WAIT_OBJECT_0),
        "wait metrics partition wall time and record initial swapchain generation");
    std::printf("initialHiddenWait=%u\n", initialReady);
    Require(WispRendererPrepareForResume(renderer) == S_FALSE,
        "fresh resume preserves the current chain and acquired readiness");
    std::vector<uint8_t> pixels(32 * 32 * 4);
    Require(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 128) == E_INVALIDARG,
        "readback requires explicit capture draw");
    Require(SUCCEEDED(WispRendererRender(renderer, nullptr, 0, 0)), "transparent clear draw");
    Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 128)), "explicit capture");
    bool transparent = true; for (uint8_t value : pixels) transparent = transparent && value == 0;
    Require(transparent, "transparent BGRA target");
    const uint8_t sample[] = {64,32,16,128};
    Require(SUCCEEDED(WispRendererUploadTexture(renderer, 1, 1, 1, 4, sample, 4)), "premultiplied texture upload");
    Require(WispRendererUploadTexture(renderer, 0, 1, 1, 4, sample, 4) == E_INVALIDARG, "white material texture is reserved");
    Require(WispRendererUploadTexture(renderer, 2, 2, 1, 4, sample, 4) == E_INVALIDARG, "invalid stride rejected");
    auto image = Sprite(0, 0, 32, 32, 1);
    Require(SUCCEEDED(WispRendererRender(renderer, &image, 1, 0)), "image draw");
    Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 128)), "image readback");
    const size_t middle = (16 * 32 + 16) * 4;
    Require(pixels[middle] == 64 && pixels[middle+1] == 32 && pixels[middle+2] == 16 && pixels[middle+3] == 128,
        "premultiplied BGRA channel preservation");
    image.tintA = .5f;
    Require(SUCCEEDED(WispRendererRender(renderer, &image, 1, 0)), "opacity draw");
    Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 128)), "opacity readback");
    Require(pixels[middle] == 32 && pixels[middle+1] == 16 && pixels[middle+2] == 8 && pixels[middle+3] == 64,
        "opacity multiplies premultiplied RGB and alpha once");
    WispDrawCommand overlap[] = {Sprite(0,0,32,32,1), Sprite(0,0,32,32,1)};
    Require(SUCCEEDED(WispRendererRender(renderer, overlap, 2, 0)), "ordered source-over draw");
    Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 128)), "source-over readback");
    Require(std::abs(static_cast<int>(pixels[middle])-96) <= 1 && std::abs(static_cast<int>(pixels[middle+3])-192) <= 1,
        "source-over premultiplied blend");
    auto rotated = Sprite(24,8,0,0);
    rotated.axisXY = 8; rotated.axisYX = -16;
    Require(SUCCEEDED(WispRendererRender(renderer, &rotated, 1, 0)), "affine rotated quad");
    Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 128)), "rotated readback");
    Require(pixels[(12*32+12)*4+3] == 255 && pixels[(24*32+24)*4+3] == 0, "affine position and rotation");
    Require(SUCCEEDED(WispRendererResize(renderer, 288, 288, 2.5f, 3.5f)), "resize and fractional composition offset");
    pixels.resize(288*288*4);
    auto dial = Sprite(0,0,288,288); dial.shader = 1; dial.parameterX = .8f; dial.parameterY = .1f;
    Require(SUCCEEDED(WispRendererRender(renderer, &dial, 1, 0)), "ported dial material");
    Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 1152)), "dial material readback");
    size_t visible = 0; for (size_t index = 3; index < pixels.size(); index += 4) if (pixels[index]) ++visible;
    std::printf("dialVisible=%zu\n", visible);
    Require(visible > 100 && visible < 288*288/2, "dial material has ring coverage and transparent background");
    auto needle = Sprite(0,0,110,180); needle.shader = 2; needle.parameterX = .35f;
    Require(SUCCEEDED(WispRendererRender(renderer, &needle, 1, 0)), "ported needle blur material");
    Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 1152)), "needle material readback");
    visible = 0; for (size_t index = 3; index < pixels.size(); index += 4) if (pixels[index]) ++visible;
    std::printf("needleVisible=%zu\n", visible);
    Require(visible > 100 && visible < 110*180, "needle blur has bounded coverage");
    HRESULT wrongThread = S_OK;
    std::thread other([&] { wrongThread = WispRendererRender(renderer, nullptr, 0, 0); }); other.join();
    Require(wrongThread == RPC_E_WRONG_THREAD, "render worker ownership enforced");
    HANDLE cancellation = CreateEventW(nullptr, TRUE, TRUE, nullptr);
    Require(cancellation != nullptr, "cancellation event creation");
    uint32_t waited = 99;
    Require(SUCCEEDED(WispRendererWaitForFrame(renderer, 100, cancellation, &waited)) && waited == 2, "cancellation wins over frame readiness");
    Require(SUCCEEDED(WispRendererWaitForFrameMeasured(renderer, 100, cancellation, 1, &waited, &waitMetrics)) &&
        waited == 2 && waitMetrics.totalTicks > 0 && waitMetrics.precheckTicks >= 0 &&
        waitMetrics.waitCallTicks == 0 && waitMetrics.postcheckTicks == 0 &&
        waitMetrics.totalTicks >= waitMetrics.precheckTicks && waitMetrics.cpuTime100ns >= -1 &&
        waitMetrics.swapChainGeneration == 1 && waitMetrics.waitResult == WAIT_OBJECT_0,
        "measured cancellation returns before the frame wait");
    waitMetrics = {1,1,1,1,1,1,1};
    Require(SUCCEEDED(WispRendererWaitForFrameMeasured(renderer, 100, cancellation, 0, &waited, &waitMetrics)) &&
        waited == 2 && waitMetrics.totalTicks == 0 && waitMetrics.precheckTicks == 0 &&
        waitMetrics.waitCallTicks == 0 && waitMetrics.postcheckTicks == 0 && waitMetrics.cpuTime100ns == 0 &&
        waitMetrics.swapChainGeneration == 0 && waitMetrics.waitResult == 0,
        "disabled wait metrics stay zero without changing cancellation");
    std::thread otherWait([&] {
        wrongThread = WispRendererWaitForFrameMeasured(renderer, 100, cancellation, 1, &waited, &waitMetrics);
    });
    otherWait.join();
    Require(wrongThread == RPC_E_WRONG_THREAD && waitMetrics.totalTicks > 0 && waitMetrics.precheckTicks >= 0 &&
        waitMetrics.waitCallTicks == 0 && waitMetrics.postcheckTicks == 0 &&
        waitMetrics.swapChainGeneration == 0 && waitMetrics.waitResult == WAIT_FAILED,
        "failed ownership check preserves measured timing without waiting");
    Require(WispRendererWaitForFrameMeasured(renderer, 100, cancellation, 1, &waited, nullptr) == E_POINTER,
        "wait metrics output is required");
    Require(WispRendererWaitForFrameMeasured(renderer, 1001, cancellation, 1, &waited, &waitMetrics) == E_INVALIDARG &&
        waitMetrics.waitResult == WAIT_FAILED && waitMetrics.swapChainGeneration == 0 && waitMetrics.waitCallTicks == 0,
        "invalid measured timeout is rejected before waiting");
    Require(WispRendererWaitForFrameMeasured(renderer, 100, cancellation, 1, nullptr, &waitMetrics) == E_INVALIDARG &&
        waitMetrics.waitResult == WAIT_FAILED && waitMetrics.waitCallTicks == 0,
        "measured wait requires its result output");
    Require(WispRendererWaitForFrameMeasured(nullptr, 100, cancellation, 1, &waited, &waitMetrics) == E_POINTER &&
        waitMetrics.totalTicks > 0 && waitMetrics.swapChainGeneration == 0 && waitMetrics.waitResult == WAIT_FAILED,
        "missing renderer preserves failure timing and an unknown generation");
    CloseHandle(cancellation);
    WispDrawMetrics drawMetrics{};
    WispPresentMetrics presentMetrics{};
    Require(WispRendererTryPresent(renderer, 1, &presentMetrics) == HRESULT_FROM_WIN32(ERROR_INVALID_STATE),
        "capture-only target cannot be presented");
    Require(presentMetrics.hResult == HRESULT_FROM_WIN32(ERROR_INVALID_STATE), "invalid presentation status is recorded");
    Require(SUCCEEDED(WispRendererDrawForPresentation(renderer, overlap, 2, 1, &drawMetrics)), "separate measured draw");
    Require(drawMetrics.drawCount == 2 && drawMetrics.mapCount == 2 && drawMetrics.totalTicks > 0 &&
        drawMetrics.setupTicks > 0 && drawMetrics.mapTicks >= drawMetrics.maximumMapTicks &&
        drawMetrics.totalTicks >= drawMetrics.setupTicks + drawMetrics.mapTicks,
        "CPU draw metrics include setup and each constant map");
    Require(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 1152) == E_INVALIDARG,
        "presentation draw does not enable readback");
    const HRESULT separatePresent = WispRendererTryPresent(renderer, 1, &presentMetrics);
    Require(separatePresent == S_OK || separatePresent == S_FALSE || separatePresent == 2, "separate presentation result");
    Require(presentMetrics.durationTicks > 0 && presentMetrics.hResult ==
        (separatePresent == S_FALSE ? DXGI_ERROR_WAS_STILL_DRAWING : separatePresent == 2 ? DXGI_STATUS_OCCLUDED : S_OK),
        "presentation metrics preserve original DXGI status");
    const HRESULT repeatedPresent = WispRendererTryPresent(renderer, 0, &presentMetrics);
    Require(separatePresent == S_OK ? repeatedPresent == HRESULT_FROM_WIN32(ERROR_INVALID_STATE) :
        repeatedPresent == S_OK || repeatedPresent == S_FALSE || repeatedPresent == 2,
        "successful presentation consumes the frame; busy or occluded can retry without drawing");
    Require(presentMetrics.durationTicks == 0 && presentMetrics.hResult == 0 && presentMetrics.reserved == 0,
        "disabled presentation metrics stay zero");
    Require(SUCCEEDED(WispRendererDrawForPresentation(renderer, overlap, 2, 0, &drawMetrics)) &&
        drawMetrics.totalTicks == 0 && drawMetrics.setupTicks == 0 && drawMetrics.mapTicks == 0 &&
        drawMetrics.maximumMapTicks == 0 && drawMetrics.mapCount == 0 && drawMetrics.drawCount == 0,
        "disabled draw metrics stay zero");
    Require(SUCCEEDED(WispRendererSetVisible(renderer, 0)) &&
        WispRendererTryPresent(renderer, 0, &presentMetrics) == HRESULT_FROM_WIN32(ERROR_INVALID_STATE),
        "hide invalidates a pending presentation even when already hidden");
    Require(SUCCEEDED(WispRendererDrawForPresentation(renderer, overlap, 2, 0, &drawMetrics)) &&
        SUCCEEDED(WispRendererResize(renderer, 288, 288, 2.5f, 3.5f)) &&
        WispRendererTryPresent(renderer, 0, &presentMetrics) == HRESULT_FROM_WIN32(ERROR_INVALID_STATE),
        "resize invalidates a pending presentation");
    Require(SUCCEEDED(WispRendererDrawForPresentation(renderer, overlap, 2, 0, &drawMetrics)) &&
        SUCCEEDED(WispRendererRender(renderer, overlap, 2, 0)) &&
        WispRendererTryPresent(renderer, 0, &presentMetrics) == HRESULT_FROM_WIN32(ERROR_INVALID_STATE),
        "capture draw replaces and invalidates pending presentation");
    Require(WispRendererDrawForPresentation(renderer, overlap, 2, 1, nullptr) == E_POINTER &&
        WispRendererTryPresent(renderer, 1, nullptr) == E_POINTER, "metrics output is required");
    Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)) && SUCCEEDED(WispRendererSetVisible(renderer, 0)), "independent visual show and hide");
    Require(SUCCEEDED(WispRendererSetVisible(renderer, 0)), "hide before visible-HWND readiness check");
    SetWindowPos(window, HWND_BOTTOM, GetSystemMetrics(SM_XVIRTUALSCREEN)+40, GetSystemMetrics(SM_YVIRTUALSCREEN)+40,
        288, 288, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    Sleep(100);
    Require(IsWindowVisible(window) && GetForegroundWindow() != window, "normal visible HWND without activation");
    auto transition = Sprite(0,0,288,288);
    transition.tintG = transition.tintB = 0;
    const HRESULT firstPresent = WispRendererRender(renderer, &transition, 1, 1);
    Require(firstPresent == S_OK || firstPresent == 2, "first colored frame reports presentation or occlusion");
    uint32_t transitionTimeouts = 0, occludedFrames = firstPresent == 2 ? 1u : 0u;
    for (uint32_t index = 0; index < 8; ++index)
    {
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 0)), "suspend opacity commit");
        Require(WispRendererPrepareForResume(renderer) == S_OK, "resume reports replacement of the stale swapchain");
        Require(WispRendererPrepareForResume(renderer) == S_FALSE, "repeated resume retains the fresh swapchain");
        Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 1152)), "explicit fresh target readback");
        bool freshTransparent = true; for (uint8_t value : pixels) freshTransparent = freshTransparent && value == 0;
        Require(freshTransparent, "fresh resume target contains no previous colored frame");
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)), "show fresh transparent chain before waiting");
        uint32_t resumed = 99;
        Require(SUCCEEDED(WispRendererWaitForFrameMeasured(renderer, 100, nullptr, 1, &resumed, &waitMetrics)), "fresh queue wait");
        Require(waitMetrics.swapChainGeneration == index + 2,
            "only successful swapchain replacements advance the measured generation");
        if (resumed != 0) ++transitionTimeouts;
        transition.tintR = (index & 1) ? 1.0f : 0.0f;
        transition.tintB = (index & 1) ? 0.0f : 1.0f;
        const HRESULT resumedPresent = WispRendererRender(renderer, &transition, 1, 1);
        if (resumedPresent == 2) ++occludedFrames;
        Require(resumedPresent == S_OK || resumedPresent == 2, "fresh resume frame reports presentation or occlusion");
    }
    Require(transitionTimeouts == 0, "fresh queue has no resume readiness deadlock");
    std::printf("resumeTimeouts=%u;occludedFrames=%u\n", transitionTimeouts, occludedFrames);
    CheckVisiblePacing(renderer, window);
    Require(SUCCEEDED(WispRendererSetOpacity(renderer, .5f)), "group opacity accepts half alpha");
    Require(WispRendererSetOpacity(renderer, 1.5f) == E_INVALIDARG, "invalid group opacity rejected");
    Require(SUCCEEDED(WispRendererDeviceRemovedReason(renderer)), "device remains healthy");
    WispRendererDestroy(renderer);
    Require(GetForegroundWindow() == foregroundBefore, "no foreground changes");
    DestroyWindow(window);
    std::printf("{\"contractFailures\":%d,\"cpuRendering\":%s,\"testWindowShown\":true}\n", failures, cpuRendering ? "true" : "false");
    return failures ? 1 : 0;
}
