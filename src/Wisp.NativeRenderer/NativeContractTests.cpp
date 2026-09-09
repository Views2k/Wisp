#define WISP_RENDERER_IMPORT
#include "Wisp.NativeRenderer.h"
#include <cstdio>
#include <vector>
#include <thread>
#include <cmath>

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
int main()
{
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
        L"STATIC", L"Wisp native renderer contract", WS_POPUP, -32000, -32000, 288, 288,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Require(window != nullptr, "hidden owned window");
    if (!window) return 1;
    HWND foregroundBefore = GetForegroundWindow();
    void* renderer = nullptr;
    HRESULT result = WispRendererCreate(window, 32, 32, &renderer);
    Require(SUCCEEDED(result) && renderer, "hardware D3D11 and DirectComposition creation");
    if (!renderer) { DestroyWindow(window); return 1; }
    uint32_t initialReady = 99;
    Require(SUCCEEDED(WispRendererWaitForFrame(renderer, 100, nullptr, &initialReady)), "initial queue wait");
    std::printf("initialHiddenWait=%u\n", initialReady);
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
    uint32_t waited = 99;
    Require(SUCCEEDED(WispRendererWaitForFrame(renderer, 100, cancellation, &waited)) && waited == 2, "cancellation wins over frame readiness");
    CloseHandle(cancellation);
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
        Require(SUCCEEDED(WispRendererPrepareForResume(renderer)), "resume replaces only the stale swapchain");
        Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 1152)), "explicit fresh target readback");
        bool freshTransparent = true; for (uint8_t value : pixels) freshTransparent = freshTransparent && value == 0;
        Require(freshTransparent, "fresh resume target contains no previous colored frame");
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)), "show fresh transparent chain before waiting");
        uint32_t resumed = 99;
        Require(SUCCEEDED(WispRendererWaitForFrame(renderer, 100, nullptr, &resumed)), "fresh queue wait");
        if (resumed != 0) ++transitionTimeouts;
        transition.tintR = (index & 1) ? 1.0f : 0.0f;
        transition.tintB = (index & 1) ? 0.0f : 1.0f;
        const HRESULT resumedPresent = WispRendererRender(renderer, &transition, 1, 1);
        if (resumedPresent == 2) ++occludedFrames;
        Require(resumedPresent == S_OK || resumedPresent == 2, "fresh resume frame reports presentation or occlusion");
    }
    Require(transitionTimeouts == 0, "fresh queue has no resume readiness deadlock");
    std::printf("resumeTimeouts=%u;occludedFrames=%u\n", transitionTimeouts, occludedFrames);
    Require(SUCCEEDED(WispRendererSetOpacity(renderer, .5f)), "group opacity accepts half alpha");
    Require(WispRendererSetOpacity(renderer, 1.5f) == E_INVALIDARG, "invalid group opacity rejected");
    Require(SUCCEEDED(WispRendererDeviceRemovedReason(renderer)), "device remains healthy");
    WispRendererDestroy(renderer);
    Require(GetForegroundWindow() == foregroundBefore, "no foreground changes");
    DestroyWindow(window);
    std::printf("{\"contractFailures\":%d,\"hardwareOnly\":true,\"testWindowShown\":true}\n", failures);
    return failures ? 1 : 0;
}
