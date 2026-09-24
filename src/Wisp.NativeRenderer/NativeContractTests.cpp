#define WISP_RENDERER_IMPORT
#include "Wisp.NativeRenderer.h"
#include "FramePacer.h"
#include "TearingPolicy.h"
#include <cstdio>
#include <cstring>
#include <vector>
#include <thread>
#include <cmath>
#include <algorithm>
#include <dxgi.h>
#include <winternl.h>
#include <d3dkmthk.h>
#include <cstddef>
#include <mutex>
#include <condition_variable>
#include <functional>

static int failures = 0;
static void Require(bool passed, const char* name)
{
    if (!passed) { ++failures; std::printf("FAIL: %s\n", name); }
}
static void CheckTearingPolicy()
{
    constexpr UINT baseline = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
    constexpr UINT supported = baseline | DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
    Require(WispTearingPolicy::SwapChainFlags(false, S_OK, true) == supported,
        "hardware capability enables tearing without removing the readiness flag");
    Require(WispTearingPolicy::SwapChainFlags(false, S_OK, false) == baseline &&
        WispTearingPolicy::SwapChainFlags(false, E_NOINTERFACE, true) == baseline &&
        WispTearingPolicy::SwapChainFlags(false, E_FAIL, true) == baseline &&
        WispTearingPolicy::SwapChainFlags(false, S_FALSE, true) == baseline,
        "disabled support and unavailable or unsuccessful feature queries preserve baseline flags");
    Require(WispTearingPolicy::SwapChainFlags(true, S_OK, true) == baseline,
        "WARP keeps baseline flags even when a factory reports tearing support");
    Require(WispTearingPolicy::PresentFlags(supported, 0) ==
        (DXGI_PRESENT_DO_NOT_WAIT | DXGI_PRESENT_ALLOW_TEARING),
        "supported zero-sync presentation keeps nonblocking submission and enables tearing");
    for (UINT interval = 1; interval <= 4; ++interval)
        Require(WispTearingPolicy::PresentFlags(supported, interval) == DXGI_PRESENT_DO_NOT_WAIT,
            "synchronized fallback never passes the incompatible tearing flag");
    Require(WispTearingPolicy::PresentFlags(baseline, 0) == DXGI_PRESENT_DO_NOT_WAIT &&
        WispTearingPolicy::PresentFlags(baseline, 1) == DXGI_PRESENT_DO_NOT_WAIT,
        "a chain without tearing support never receives a tearing Present flag");
}

static WispPresentationStatus CheckPresentationStatus(void* renderer, bool cpuRendering,
    UINT expectedFlags = UINT32_MAX, bool primer = false)
{
    WispPresentationStatus status{};
    const HRESULT result = WispRendererGetPresentationStatus(renderer, &status);
    Require(result == S_OK, "owner can inspect actual DXGI presentation properties");
    if (result != S_OK)
    {
        std::printf("presentationStatusHResult=0x%08lX\n", static_cast<unsigned long>(result));
        return status;
    }
    Require(status.swapChainFlags == WispTearingPolicy::SwapChainFlags(cpuRendering,
        status.tearingSupportHResult, status.tearingSupported != 0),
        "actual composition chain flags match the checked feature policy");
    Require(status.bufferCount == 3 && status.maximumFrameLatency == 2,
        "tearing support preserves three buffers and maximum latency two");
    if (expectedFlags != UINT32_MAX)
        Require(status.swapChainFlags == expectedFlags, "resize and recreation preserve the original chain flags");
    if (cpuRendering)
        Require(status.tearingSupportHResult == S_FALSE && status.tearingSupported == 0,
            "actual WARP chain skips the hardware feature query and retains its baseline");
    Require(status.presentFlags == WispTearingPolicy::PresentFlags(status.swapChainFlags, status.syncInterval) &&
        status.lastPresentFlags == WispTearingPolicy::PresentFlags(status.swapChainFlags, status.lastPresentSyncInterval),
        "next and last actual Present use flags compatible with their sync interval and created chain");
    if (primer)
        Require(status.lastPresentSyncInterval == 0 && status.lastPresentHResult == S_OK,
            "initial or recreated transparent primer used a successful zero-sync Present");
    return status;
}

static void CheckFramePacer()
{
    constexpr int64_t frequency = 10000000, origin = 1000000, lateness = 2500;
    for (const uint32_t hertz : {60u, 144u, 240u})
    {
        FramePacerDeadline schedule;
        Require(schedule.Configure(frequency, hertz), "pacing schedule accepts known refresh rates");
        const int64_t interval = frequency / hertz + (frequency % hertz != 0 ? 1 : 0);
        Require(schedule.Interval() == interval && !schedule.HasDeadline(), "pacing interval rounds up and starts unarmed");
        Require(schedule.AdvanceAfterPresent(origin) && schedule.Deadline() == origin + interval,
            "first successful presentation anchors the pacing phase");
        for (int64_t frame = 1; frame <= 1000; ++frame)
        {
            const int64_t actual = schedule.Deadline() + lateness;
            if (!schedule.AdvanceAfterPresent(actual) || schedule.Deadline() != origin + (frame + 1) * interval)
            {
                Require(false, "constant wake lateness does not accumulate pacing drift");
                break;
            }
        }
        const int64_t beforeStall = schedule.Deadline();
        const int64_t afterStall = beforeStall + 7 * interval + interval / 2;
        const int64_t afterStallDeadline = beforeStall + 8 * interval;
        Require(schedule.AdvanceAfterPresent(afterStall) && schedule.Deadline() == afterStallDeadline &&
            schedule.Deadline() > afterStall && schedule.Deadline() - afterStall <= interval &&
            (schedule.Deadline() - origin) % interval == 0,
            "fractional long overrun skips expired slots and preserves the original phase");
        Require(schedule.AdvanceAfterPresent(afterStallDeadline + lateness) &&
            schedule.Deadline() == afterStallDeadline + interval,
            "presentation after an overrun resumes the original phase without catch-up backlog");
        const int64_t preserved = schedule.Deadline();
        Require(schedule.Configure(frequency, hertz) && schedule.Deadline() == preserved,
            "unchanged refresh lookup preserves absolute phase");
        Require(!schedule.AdvanceAfterPresent(afterStall - 1) && schedule.Deadline() == preserved,
            "backwards presentation timestamps cannot corrupt the schedule");
        Require(schedule.Configure(frequency, hertz == 60 ? 144u : 60u) && !schedule.HasDeadline(),
            "refresh changes reset the old phase");
        Require(schedule.AdvanceAfterPresent(afterStall), "new refresh establishes a new phase");
        schedule.Reset();
        Require(schedule.IsConfigured() && !schedule.HasDeadline(), "resume reset preserves rate without old deadlines");
        Require(!schedule.Configure(frequency, 0) && !schedule.IsConfigured() &&
            !schedule.AdvanceAfterPresent(afterStall), "invalid refresh cannot leave an active schedule");

        FramePacerDeadline boundary;
        Require(boundary.Configure(frequency, hertz) && boundary.AdvanceAfterPresent(origin),
            "phase boundary fixture is configured");
        const int64_t oneTickBefore = boundary.Deadline() + interval - 1;
        Require(boundary.AdvanceAfterPresent(oneTickBefore) && boundary.Deadline() == oneTickBefore + 1,
            "an unexpired phase one tick ahead is preserved without a minimum-spacing reset");
        const int64_t exactDeadline = boundary.Deadline() + interval;
        Require(boundary.AdvanceAfterPresent(exactDeadline) && boundary.Deadline() == exactDeadline + interval,
            "a candidate deadline equal to presentation time is expired and advances one interval");
        const int64_t exactOverrun = boundary.Deadline() + 7 * interval;
        Require(boundary.AdvanceAfterPresent(exactOverrun) && boundary.Deadline() == exactOverrun + interval &&
            (boundary.Deadline() - origin) % interval == 0,
            "an exact multi-interval overrun selects the first strictly future phase");
    }
    int64_t converted = 0;
    Require(FramePacerMath::ScaleCeiling(1, 10000000, 3, converted) && converted == 3333334 &&
        FramePacerMath::ScaleCeiling(5000, 1000, frequency, converted) && converted == 1 &&
        FramePacerMath::ScaleCeiling(10000, 1000, frequency, converted) && converted == 1 &&
        FramePacerMath::ScaleCeiling(0, 1000, frequency, converted) && converted == 0,
        "timer and timeout conversions round upward without a minimum zero-timeout delay");
    Require(!FramePacerMath::ScaleCeiling(INT64_MAX, 2, 1, converted) &&
        !FramePacerMath::ScaleCeiling(1, 1, 0, converted), "pacing conversion rejects overflow and invalid divisors");
    DWORD remaining = INFINITE;
    Require(FramePacerMath::RemainingMilliseconds(origin, origin, frequency, 1, remaining) && remaining == 1 &&
        FramePacerMath::RemainingMilliseconds(origin, origin + 1, frequency, 1, remaining) && remaining == 1 &&
        FramePacerMath::RemainingMilliseconds(origin, origin + 9999, frequency, 1, remaining) && remaining == 1,
        "one millisecond wait retains a blocking timeout until its precise deadline");
    Require(FramePacerMath::RemainingMilliseconds(origin, origin + 10000, frequency, 1, remaining) && remaining == 0 &&
        FramePacerMath::RemainingMilliseconds(origin, origin + 10001, frequency, 1, remaining) && remaining == 0 &&
        FramePacerMath::RemainingMilliseconds(origin, origin, frequency, 0, remaining) && remaining == 0,
        "expired and explicit zero budgets remain nonblocking");
    Require(FramePacerMath::RemainingMilliseconds(origin, origin + 10001, frequency, 4, remaining) && remaining == 3 &&
        FramePacerMath::RemainingMilliseconds(origin, origin + 39999, frequency, 4, remaining) && remaining == 1 &&
        FramePacerMath::RemainingMilliseconds(origin, origin + 40000, frequency, 4, remaining) && remaining == 0,
        "precheck and pacing share one deadline without subtracting rounded elapsed time");
    Require(FramePacerMath::RemainingMilliseconds(origin, origin, 1234567, 1, remaining) && remaining == 1 &&
        FramePacerMath::RemainingMilliseconds(origin, origin + 1234, 1234567, 1, remaining) && remaining == 1 &&
        FramePacerMath::RemainingMilliseconds(origin, origin + 1235, 1234567, 1, remaining) && remaining == 0,
        "fractional QPC budgets round upward without exceeding the requested millisecond timeout");
    Require(!FramePacerMath::RemainingMilliseconds(origin, origin - 1, frequency, 1, remaining) &&
        !FramePacerMath::RemainingMilliseconds(origin, origin, 0, 1, remaining) &&
        !FramePacerMath::RemainingMilliseconds(INT64_MAX, INT64_MAX, frequency, 1, remaining) &&
        !FramePacerMath::RemainingMilliseconds(origin, origin, frequency, INFINITE, remaining),
        "wait-budget conversion rejects backwards clocks, invalid frequency, overflow and unbounded waits");
    FramePacerDeadline overflow;
    Require(overflow.Configure(INT64_MAX, 1) && !overflow.AdvanceAfterPresent(1) && !overflow.HasDeadline(),
        "deadline overflow never creates an unbounded or wrapped schedule");
    FramePacerDeadline activeOverflow;
    Require(activeOverflow.Configure(10, 1) && activeOverflow.AdvanceAfterPresent(INT64_MAX - 15),
        "active overflow fixture has a representable deadline");
    const int64_t preservedDeadline = activeOverflow.Deadline();
    Require(!activeOverflow.AdvanceAfterPresent(INT64_MAX - 4) && activeOverflow.HasDeadline() &&
        activeOverflow.Deadline() == preservedDeadline && activeOverflow.Interval() == 10,
        "advancing an active deadline past the clock range preserves the prior state");
    FramePacerDeadline skippedOverflow;
    Require(skippedOverflow.Configure(10, 1) && skippedOverflow.AdvanceAfterPresent(0),
        "skipped-slot overflow fixture has an initial phase");
    Require(!skippedOverflow.AdvanceAfterPresent(INT64_MAX - 1) && skippedOverflow.HasDeadline() &&
        skippedOverflow.Deadline() == 10 && skippedOverflow.Interval() == 10 &&
        skippedOverflow.AdvanceAfterPresent(10) && skippedOverflow.Deadline() == 20,
        "skipped-slot overflow preserves both the deadline and last accepted timestamp");

    FramePacer pacer;
    Require(pacer.IsValid(), "high-resolution frame timer is available");
    if (!pacer.IsValid())
    {
        std::printf("framePacerInitializationError=%lu\n", static_cast<unsigned long>(pacer.InitializationError()));
        return;
    }
    Require(!pacer.Configure(0) && pacer.Wait(0) == WAIT_FAILED,
        "unconfigured software gate fails instead of permitting an unpaced frame");
    Require(pacer.Configure(2) && pacer.Wait(0) == WAIT_OBJECT_0,
        "configured first frame needs no software delay");
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    Require(pacer.AdvanceAfterPresent(now.QuadPart), "successful presentation arms the software gate");
    Require(pacer.Wait(0) == WAIT_TIMEOUT && pacer.Wait(2) == WAIT_TIMEOUT && pacer.LastWaitTicks() > 0,
        "zero and short timeout leave a future pacing deadline intact");
    HANDLE budgetSignal = CreateEventW(nullptr, TRUE, TRUE, nullptr);
    Require(budgetSignal != nullptr, "shared wait deadline fixture creation");
    if (budgetSignal)
    {
        Require(pacer.Wait(100, nullptr, budgetSignal) == WAIT_TIMEOUT,
            "the shared original deadline interrupts a later software pacing deadline");
    }
    HANDLE cancellation = CreateEventW(nullptr, TRUE, TRUE, nullptr);
    Require(cancellation != nullptr, "pacing cancellation event creation");
    if (cancellation)
    {
        Require(pacer.Wait(0, cancellation) == WAIT_OBJECT_0 + 1,
            "pre-signaled cancellation wins over software readiness");
        if (budgetSignal)
            Require(pacer.Wait(100, cancellation, budgetSignal) == WAIT_OBJECT_0 + 1,
                "cancellation wins when the shared deadline is also signaled");
        ResetEvent(cancellation);
        std::thread signal([&] { Sleep(10); SetEvent(cancellation); });
        const DWORD canceled = pacer.Wait(100, cancellation);
        signal.join();
        Require(canceled == WAIT_OBJECT_0 + 1 && pacer.LastWaitTicks() > 0,
            "cancellation interrupts the pacing timer wait");
        ResetEvent(cancellation);
        Require(pacer.Wait(0, cancellation) == WAIT_TIMEOUT,
            "cancellation retry preserves the deadline without another presentation");
        CloseHandle(cancellation);
    }
    Require(pacer.Wait(INFINITE) == WAIT_FAILED, "software gate refuses an unbounded caller timeout");
    pacer.Reset();
    Require(pacer.Wait(0) == WAIT_OBJECT_0, "hide or resume clears a stale software deadline");
    if (budgetSignal)
    {
        Require(pacer.Wait(100, nullptr, budgetSignal) == WAIT_OBJECT_0 &&
            WaitForSingleObject(budgetSignal, 0) == WAIT_OBJECT_0,
            "software readiness wins without consuming the deadline needed by the following DXGI wait");
        CloseHandle(budgetSignal);
    }
    FrameWaitBudget waitBudget;
    QueryPerformanceCounter(&now);
    Require(waitBudget.Arm(now.QuadPart, pacer.Frequency(), 1000) == S_OK && waitBudget.Handle(),
        "cached high-resolution deadline timer arms from the original call timestamp");
    const HANDLE cachedBudgetHandle = waitBudget.Handle();
    waitBudget.Cancel();
    Require(!waitBudget.Handle(), "completed render wait cancels its outstanding deadline");
    QueryPerformanceCounter(&now);
    Require(waitBudget.Arm(now.QuadPart, pacer.Frequency(), 1) == S_OK &&
        waitBudget.Handle() == cachedBudgetHandle && WaitForSingleObject(waitBudget.Handle(), 1000) == WAIT_OBJECT_0,
        "short deadline signals through the reused timer without per-frame handle allocation");
    QueryPerformanceCounter(&now);
    Require(waitBudget.Arm(now.QuadPart, pacer.Frequency(), 0) == S_FALSE && !waitBudget.Handle(),
        "zero deadline remains a nonblocking poll without an armed timer");
}
static_assert(sizeof(WispGpuPriorityStatus) == 36 &&
    offsetof(WispGpuPriorityStatus, requestedProcessClass) == 4 &&
    offsetof(WispGpuPriorityStatus, processSetStatus) == 8 &&
    offsetof(WispGpuPriorityStatus, processReadStatus) == 12 &&
    offsetof(WispGpuPriorityStatus, effectiveProcessClass) == 16 &&
    offsetof(WispGpuPriorityStatus, requestedDevicePriority) == 20 &&
    offsetof(WispGpuPriorityStatus, deviceSetHResult) == 24 &&
    offsetof(WispGpuPriorityStatus, deviceReadHResult) == 28 &&
    offsetof(WispGpuPriorityStatus, effectiveDevicePriority) == 32,
    "GPU priority status has nine consecutive 32-bit ABI fields.");
static_assert(sizeof(WispWaitMetrics) == 48 && sizeof(WispWaitMetricsV2) == 72 &&
    offsetof(WispWaitMetricsV2, wait) == 0 && offsetof(WispWaitMetricsV2, pacingTicks) == 48 &&
    offsetof(WispWaitMetricsV2, syncInterval) == 56 && offsetof(WispWaitMetricsV2, refreshRate) == 60 &&
    offsetof(WispWaitMetricsV2, pacingHResult) == 64 && offsetof(WispWaitMetricsV2, pacingWaitResult) == 68,
    "Versioned pacing metrics preserve the original 48-byte ABI prefix.");

static bool IsUnattempted(const WispGpuPriorityStatus& status)
{
    return status.attempted == 0 && status.requestedProcessClass == D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH &&
        status.processSetStatus == INT32_MIN && status.processReadStatus == INT32_MIN &&
        status.effectiveProcessClass == -1 && status.requestedDevicePriority == 1 &&
        status.deviceSetHResult == S_FALSE && status.deviceReadHResult == S_FALSE &&
        status.effectiveDevicePriority == INT32_MIN;
}

static void CheckGpuPriority(void* renderer, bool cpuRendering,
    NTSTATUS beforeProcessRead, D3DKMT_SCHEDULINGPRIORITYCLASS beforeProcessClass)
{
    WispGpuPriorityStatus status{};
    Require(WispRendererGetGpuPriorityStatus(renderer, &status) == S_OK,
        "GPU priority initialization snapshot is available on owner thread");
    Require(status.requestedProcessClass == D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH &&
        status.requestedDevicePriority == 1, "GPU priority requests HIGH and relative plus one only");
    auto currentClass = D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL;
    const NTSTATUS currentRead = D3DKMTGetProcessSchedulingPriorityClass(GetCurrentProcess(), &currentClass);
    if (cpuRendering)
    {
        Require(IsUnattempted(status), "WARP skips priority setters and readbacks without reporting success");
        if (beforeProcessRead >= 0 && currentRead >= 0)
            Require(currentClass == beforeProcessClass, "WARP leaves existing process GPU priority unchanged");
    }
    else
    {
        Require(status.attempted == 1 && status.processSetStatus != INT32_MIN &&
            status.processReadStatus != INT32_MIN && status.deviceSetHResult != S_FALSE &&
            status.deviceReadHResult != S_FALSE, "hardware initialization records all four priority call results");
        if (status.processReadStatus >= 0)
        {
            Require(status.effectiveProcessClass >= D3DKMT_SCHEDULINGPRIORITYCLASS_IDLE &&
                status.effectiveProcessClass <= D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME,
                "successful process priority readback is a valid class");
            if (currentRead >= 0)
                Require(status.effectiveProcessClass == static_cast<int32_t>(currentClass),
                    "stored process priority agrees with independent OS readback");
            if (status.processSetStatus == 0)
                Require(status.effectiveProcessClass == status.requestedProcessClass,
                    "successful process priority set and readback agree");
        }
        else Require(status.effectiveProcessClass == -1, "failed process readback retains unknown class");
        if (status.deviceReadHResult == S_OK)
        {
            Require(status.effectiveDevicePriority >= -7 && status.effectiveDevicePriority <= 7,
                "successful device priority readback is a relative priority");
            if (status.deviceSetHResult == S_OK)
                Require(status.effectiveDevicePriority == status.requestedDevicePriority,
                    "successful device priority set and readback agree");
        }
        else Require(status.effectiveDevicePriority == INT32_MIN, "failed device readback retains unknown priority");
    }
    std::printf("gpuPriorityAttempted=%u;processSet=0x%08lX;processRead=0x%08lX;processEffective=%d;deviceSet=0x%08lX;deviceRead=0x%08lX;deviceEffective=%d\n",
        status.attempted, static_cast<unsigned long>(status.processSetStatus),
        static_cast<unsigned long>(status.processReadStatus), status.effectiveProcessClass,
        static_cast<unsigned long>(status.deviceSetHResult), static_cast<unsigned long>(status.deviceReadHResult),
        status.effectiveDevicePriority);
    WispGpuPriorityStatus repeated{};
    Require(WispRendererGetGpuPriorityStatus(renderer, &repeated) == S_OK &&
        std::memcmp(&status, &repeated, sizeof(status)) == 0, "priority snapshot queries remain stable");
    Require(WispRendererGetGpuPriorityStatus(renderer, nullptr) == E_POINTER,
        "priority status requires an output pointer");
    Require(WispRendererGetGpuPriorityStatus(nullptr, &repeated) == E_POINTER && IsUnattempted(repeated),
        "null renderer returns failure and unknown priority status");
    HRESULT wrongThread = S_OK;
    repeated = status;
    std::thread other([&] { wrongThread = WispRendererGetGpuPriorityStatus(renderer, &repeated); });
    other.join();
    Require(wrongThread == RPC_E_WRONG_THREAD && IsUnattempted(repeated),
        "priority query enforces owner thread and resets failed output");
}
static constexpr bool TimingsFitTotal(int64_t totalTicks, int64_t firstTicks, int64_t secondTicks, int64_t thirdTicks = 0)
{
    // Bound each subtraction instead of summing durations that could overflow.
    return firstTicks >= 0 && secondTicks >= 0 && thirdTicks >= 0 &&
        totalTicks >= firstTicks && totalTicks - firstTicks >= secondTicks &&
        totalTicks - firstTicks - secondTicks >= thirdTicks;
}
static_assert(TimingsFitTotal(0, 0, 0) && TimingsFitTotal(10, 2, 3, 5), "Zero and exact-fit timings are valid.");
static_assert(TimingsFitTotal(INT64_MAX, INT64_MAX - 2, 1, 1), "Exact fits remain valid at the signed limit.");
static_assert(!TimingsFitTotal(9, 2, 3, 5), "Nested timings cannot exceed the total.");
static_assert(!TimingsFitTotal(INT64_MAX, INT64_MAX, 1) &&
    !TimingsFitTotal(INT64_MAX, INT64_MAX - 1, 1, 1), "Overflowing sums are rejected without evaluating them.");
static_assert(!TimingsFitTotal(INT64_MIN, 0, 0) && !TimingsFitTotal(INT64_MAX, INT64_MIN, 0) &&
    !TimingsFitTotal(INT64_MAX, 0, INT64_MIN) && !TimingsFitTotal(INT64_MAX, 0, 0, INT64_MIN),
    "Negative timings are rejected before subtraction.");
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

class MotionTestWorker final
{
    std::mutex mutex;
    std::condition_variable changed;
    std::function<void()> action;
    bool stopping = false, done = false;
    std::thread thread{[this] {
        std::unique_lock<std::mutex> lock(mutex);
        for (;;)
        {
            changed.wait(lock, [&] { return stopping || static_cast<bool>(action); });
            if (stopping) return;
            auto next = std::move(action);
            action = {};
            lock.unlock();
            next();
            lock.lock();
            done = true;
            changed.notify_all();
        }
    }};
public:
    ~MotionTestWorker()
    {
        { std::lock_guard<std::mutex> lock(mutex); stopping = true; }
        changed.notify_all();
        thread.join();
    }
    void Run(std::function<void()> next)
    {
        std::unique_lock<std::mutex> lock(mutex);
        action = std::move(next); done = false;
        changed.notify_all();
        changed.wait(lock, [&] { return done; });
    }
};

static void CheckIndependentMotion(HWND window, bool cpuRendering, bool hiddenOnly)
{
    constexpr UINT width = 128, height = 192;
    void* renderer = nullptr;
    Require(WispRendererCreateWithCompositorNeedle(window, width, height, cpuRendering ? 1u : 0u, &renderer) == S_OK && renderer,
        "independent-motion fixture creates its content renderer");
    if (!renderer) return;
    void* channel = nullptr;
    const HRESULT created = WispRendererCreateMotionChannel(renderer, &channel);
    Require(created == (cpuRendering ? S_FALSE : S_OK) && (cpuRendering ? !channel : channel != nullptr),
        "motion channel is explicit and hardware-only");
    if (!channel) { WispRendererDestroy(renderer); return; }
    void* duplicate = nullptr;
    Require(WispRendererCreateMotionChannel(renderer, &duplicate) == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS) && !duplicate,
        "a renderer cannot attach competing motion owners");
    LARGE_INTEGER frequency{}, now{};
    QueryPerformanceFrequency(&frequency);
    WispCompositorNeedleGeometry geometry{};
    geometry.command = Sprite(0, 0, 110, 180); geometry.command.shader = 2;
    geometry.parentM11 = geometry.parentM22 = 1;
    geometry.parentOffsetX = 7; geometry.parentOffsetY = 6;
    geometry.pivotX = -30.8f; geometry.pivotY = 90; geometry.opacity = .6f;
    WispCompositorNeedlePoint points[]{{0, 10, -.2}, {.020, 45, -.1}};
    WispMotionStatus motionStatus{};
    HRESULT motionResult = E_PENDING;
    uint64_t generation = 1;
    bool expiredMaterial = false;
    auto update = [&] {
        LARGE_INTEGER timestamp{}; QueryPerformanceCounter(&timestamp);
        return WispMotionChannelUpdate(channel, &geometry, timestamp.QuadPart,
            timestamp.QuadPart + frequency.QuadPart * 10, points, ARRAYSIZE(points), generation, &motionStatus);
    };
    auto prepare = [&] {
        QueryPerformanceCounter(&now);
        return WispRendererPrepareCompositorNeedleMotion(renderer, &geometry,
            expiredMaterial ? now.QuadPart - frequency.QuadPart : now.QuadPart,
            expiredMaterial ? now.QuadPart - frequency.QuadPart / 2 : now.QuadPart + frequency.QuadPart * 10,
            points, ARRAYSIZE(points), generation);
    };
    auto background = Sprite(0, 0, width, height);
    auto main = geometry.command; main.originX = 7; main.originY = 6; main.parameterW = 1;
    auto foreground = Sprite(64, 48, 50, 60);
    WispDrawCommand scene[]{background, main, foreground};
    uint32_t lastDrawCount = 0;
    auto drawAndPresent = [&] {
        uint32_t ready = 99;
        HRESULT result = WispRendererWaitForFrame(renderer, 1000, nullptr, &ready);
        if (FAILED(result) || ready != 0) return E_FAIL;
        WispDrawMetrics draw{};
        result = WispRendererDrawForPresentation(renderer, scene, ARRAYSIZE(scene), 1, &draw);
        lastDrawCount = draw.drawCount;
        if (FAILED(result)) return result;
        Require(WispRendererSetOffset(renderer, 37, -19) == S_OK &&
            WispRendererSetOffset(renderer, 0, 0) == S_OK,
            "host translation preserves the drawn frame waiting for presentation");
        WispPresentMetrics present{};
        for (UINT attempt = 0; attempt < 100; ++attempt)
        {
            result = WispRendererTryPresent(renderer, 0, &present);
            if (result != S_FALSE) break;
            Sleep(1);
        }
        return result;
    };
    RECT bounds{}; GetWindowRect(window, &bounds);
    const HWND foregroundBefore = GetForegroundWindow();
    bool shown = false;
    {
        MotionTestWorker worker;
        worker.Run([&] { motionResult = update(); });
        Require(motionResult == S_OK && !motionStatus.geometryAccepted && motionStatus.commits == 1,
            "motion starts hidden before matching material and HUD acceptance");
        Require(update() == RPC_E_WRONG_THREAD, "motion updates retain their dedicated owner thread");
        Require(prepare() == S_OK, "renderer prepares exact signed-blur bitmap independently");
        HRESULT accepted = drawAndPresent();
        if (accepted == 2 && !hiddenOnly)
        {
            shown = true;
            SetWindowPos(window, HWND_BOTTOM, GetSystemMetrics(SM_XVIRTUALSCREEN) + 40,
                GetSystemMetrics(SM_YVIRTUALSCREEN) + 40, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Require(WispRendererSetVisible(renderer, 1) == S_OK, "independent fixture shows without activation");
            accepted = drawAndPresent();
        }
        Require(accepted == S_OK || (hiddenOnly && accepted == 2), "independent fixture submits a matching HUD atlas");
        WispCompositorNeedleStatus contentBefore{}, contentAfter{};
        WispRendererGetCompositorNeedleStatus(renderer, &contentBefore);
        uint64_t bitmapBefore = 0;
        WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &bitmapBefore);
        worker.Run([&] {
            for (UINT index = 0; index < 8; ++index)
            {
                points[0].angle += 1;
                points[0].blur += .01;
                geometry.command.parameterX = static_cast<float>(points[0].blur);
                motionResult = update();
                if (motionResult != S_OK) break;
            }
        });
        WispRendererGetCompositorNeedleStatus(renderer, &contentAfter);
        Require(motionResult == S_OK && motionStatus.commits == 9 && motionStatus.bitmapDraws == bitmapBefore &&
            contentAfter.presents == contentBefore.presents && contentAfter.updates == contentBefore.updates &&
            motionStatus.beginTimestamp < motionStatus.endTimestamp && motionStatus.commitTimestamp >= motionStatus.beginTimestamp &&
            motionStatus.geometryAccepted == (accepted == S_OK ? 1u : 0u),
            "motion commits progress while the render owner is withheld without bitmap work or HUD presentation");
        const auto beforeOffset = contentAfter;
        Require(WispRendererSetOffset(renderer, 23.5f, -17.5f) == S_OK &&
            WispRendererGetCompositorNeedleStatus(renderer, &contentAfter) == S_OK &&
            std::memcmp(&beforeOffset, &contentAfter, sizeof(contentAfter)) == 0,
            "host translation preserves accepted needle motion, surface size and atlas state");
        uint64_t bitmapAfterOffset = 0;
        Require(WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &bitmapAfterOffset) == S_OK &&
            bitmapAfterOffset == bitmapBefore,
            "host translation reuses existing needle pixels");
        Require(WispRendererSetOffset(renderer, NAN, 0) == E_INVALIDARG &&
            WispRendererSetOffset(renderer, 0, INFINITY) == E_INVALIDARG,
            "host translation rejects non-finite coordinates");
        HRESULT wrongOffsetThread = S_OK;
        std::thread offsetThread([&] { wrongOffsetThread = WispRendererSetOffset(renderer, 0, 0); });
        offsetThread.join();
        Require(wrongOffsetThread == RPC_E_WRONG_THREAD && WispRendererSetOffset(renderer, 0, 0) == S_OK,
            "host translation retains the render-owner thread contract");
        if (accepted == S_OK)
        {
            generation = 2;
            worker.Run([&] { motionResult = update(); });
            Require(motionResult == S_OK && !motionStatus.geometryAccepted,
                "same geometry with a new car/source generation cannot reuse old HUD acceptance");
            Require(prepare() == S_OK, "new publication generation prepares a closed parent gate");
            worker.Run([&] { motionResult = update(); });
            Require(motionResult == S_OK && !motionStatus.geometryAccepted,
                "material preparation alone cannot activate a new publication generation");
            Require(drawAndPresent() == S_OK, "new generation presents its matching atlas");
            worker.Run([&] { motionResult = update(); });
            Require(motionResult == S_OK && motionStatus.geometryAccepted,
                "matching HUD acceptance opens only the current generation motion");
            expiredMaterial = true;
            Require(prepare() == S_OK && drawAndPresent() == S_OK && lastDrawCount == 2,
                "an expired bitmap preparation deadline cannot flatten fresh independently animated motion");
            worker.Run([&] { motionResult = update(); });
            Require(motionResult == S_OK && motionStatus.geometryAccepted,
                "fresh motion retains its gate after the older material deadline expires");
            expiredMaterial = false;
        }
        worker.Run([&] { motionResult = WispMotionChannelClear(channel); });
        Require(motionResult == S_OK, "motion owner clears freshness independently of render work");
        worker.Run([&] { motionResult = update(); });
        Require(motionResult == S_OK && !motionStatus.geometryAccepted,
            "clearing motion invalidates the prior generation even when its geometry is unchanged");
        if (accepted == S_OK)
        {
            Require(prepare() == S_OK && drawAndPresent() == S_OK,
                "fresh input in the same generation prepares and presents after a clear");
            worker.Run([&] { motionResult = update(); });
            Require(motionResult == S_OK && motionStatus.geometryAccepted,
                "ordinary freshness recovery can reactivate the same generation after matching HUD presentation");
        }
        WispRendererDestroy(renderer); renderer = nullptr;
        worker.Run([&] { motionResult = update(); });
        Require(motionResult == S_FALSE, "retained motion handle safely rejects updates after renderer teardown");
    }
    WispMotionChannelDestroy(channel);
    if (shown) SetWindowPos(window, HWND_NOTOPMOST, bounds.left, bounds.top,
        bounds.right - bounds.left, bounds.bottom - bounds.top, SWP_NOACTIVATE | SWP_HIDEWINDOW);
    Require(GetForegroundWindow() == foregroundBefore && !IsWindowVisible(window),
        "independent motion contracts preserve foreground and restore hidden bounds");
}

static void CheckShortWaitBudget(HWND window, bool cpuRendering)
{
    void* renderer = nullptr;
    const HRESULT created = WispRendererCreateWithMode(window, 32, 32, cpuRendering ? 1u : 0u, &renderer);
    Require(created == S_OK && renderer, "short-wait fixture creates its own queue");
    if (!renderer) return;
    // Drain the finite initial credits without submitting additional frames. The
    // following waits should block; a 1 ms budget must not become a busy poll.
    for (UINT attempt = 0; attempt < 8; ++attempt)
    {
        uint32_t result = 99;
        const HRESULT waited = WispRendererWaitForFrame(renderer, 0, nullptr, &result);
        Require(waited == S_OK, "short-wait fixture drains initial credits safely");
        if (waited != S_OK || result == 1) break;
    }
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    UINT timeouts = 0;
    int64_t timeoutTicks = 0;
    for (UINT attempt = 0; attempt < 32; ++attempt)
    {
        uint32_t result = 99;
        WispWaitMetrics metrics{};
        const HRESULT waited = WispRendererWaitForFrameMeasured(renderer, 1, nullptr, 1, &result, &metrics);
        Require(waited == S_OK && result <= 1, "one millisecond renderer wait returns readiness or timeout");
        if (waited != S_OK) break;
        if (result == 1) { ++timeouts; timeoutTicks += metrics.totalTicks; }
    }
    const double timeoutMilliseconds = frequency.QuadPart > 0
        ? static_cast<double>(timeoutTicks) * 1000.0 / frequency.QuadPart : 0;
    Require(timeouts >= 16 && timeoutMilliseconds >= timeouts * .25,
        "repeated one millisecond waits block instead of producing microsecond timeout polling");
    // No upper duration threshold: scheduler preemption can extend a valid wait.
    std::printf("shortWaitTimeouts=%u;shortWaitTotalMilliseconds=%.3f\n", timeouts, timeoutMilliseconds);
    WispRendererDestroy(renderer);
}

static void CheckCompositorNeedle(HWND window, bool cpuRendering, bool hiddenOnly)
{
    static_assert(sizeof(WispCompositorNeedleGeometry) == 120 &&
        offsetof(WispCompositorNeedleGeometry, pivotX) == 80 &&
        offsetof(WispCompositorNeedleGeometry, parentM11) == 88 &&
        offsetof(WispCompositorNeedleGeometry, opacity) == 112 &&
        offsetof(WispCompositorNeedleGeometry, reserved) == 116 &&
        sizeof(WispCompositorNeedlePoint) == 24 && offsetof(WispCompositorNeedlePoint, blur) == 16,
        "compositor needle interop layouts match managed geometry and curve points");
    constexpr UINT width = 128, height = 192, needleWidth = 110, needleHeight = 180;
    void* renderer = nullptr;
    const HRESULT created = WispRendererCreateWithCompositorNeedle(window, width, height, cpuRendering ? 1u : 0u, &renderer);
    if (FAILED(created)) std::printf("compositorCreateHResult=0x%08lX\n", static_cast<unsigned long>(created));
    Require(created == S_OK && renderer, "explicit compositor renderer creates an attached owned target");
    if (!renderer) return;
    WispCompositorNeedleStatus initial{}, current{};
    Require(WispRendererGetCompositorNeedleStatus(renderer, &initial) == S_OK &&
        initial.supported == (cpuRendering ? 0u : 1u) && initial.atlasHeight == (cpuRendering ? height : height * 2u),
        "only explicit hardware creation allocates the two-region atlas");
    Require(WispRendererGetCompositorNeedleStatus(renderer, nullptr) == E_POINTER,
        "compositor status requires an output");
    Require(WispRendererGetCompositorNeedleBitmapDrawCount(renderer, nullptr) == E_POINTER,
        "needle bitmap counter requires an output");
    if (cpuRendering)
    {
        Require(WispRendererUpdateCompositorNeedle(renderer, nullptr, 0, 0, nullptr, 0) == S_FALSE,
            "WARP preserves its legacy path without enabling compositor needles");
        WispRendererDestroy(renderer);
        return;
    }
    const HWND referenceWindow = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
        L"STATIC", L"Wisp needle pixel contract", WS_POPUP, -32000, -32000, width, height,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    void* reference = nullptr;
    const HRESULT referenceCreated = referenceWindow ? WispRendererCreate(referenceWindow, width, height, &reference) : E_FAIL;
    Require(referenceCreated == S_OK && reference, "needle pixel reference uses an independent hidden legacy target");
    if (!reference) { if (referenceWindow) DestroyWindow(referenceWindow); WispRendererDestroy(renderer); return; }
    LARGE_INTEGER frequency{}, now{};
    QueryPerformanceFrequency(&frequency);
    WispCompositorNeedleGeometry geometry{};
    geometry.command = Sprite(0, 0, needleWidth, needleHeight);
    geometry.command.shader = 2;
    geometry.parentM11 = geometry.parentM22 = 1;
    geometry.parentOffsetX = 7; geometry.parentOffsetY = 6;
    geometry.pivotX = -30.8f; geometry.pivotY = 90;
    geometry.opacity = 1;
    WispCompositorNeedlePoint curve[]{{0, 0, .35}, {.016, 30, -.2}};
    auto update = [&] {
        QueryPerformanceCounter(&now);
        return WispRendererUpdateCompositorNeedle(renderer, &geometry, now.QuadPart,
            now.QuadPart + frequency.QuadPart * 10, curve, ARRAYSIZE(curve));
    };
    uint32_t ready = 99;
    Require(WispRendererWaitForFrame(renderer, 1000, nullptr, &ready) == S_OK && ready == 0,
        "compositor fixture obtains one HUD credit before its pending draw");
    std::vector<uint8_t> expected(width * height * 4), below(expected.size()), above(expected.size());
    std::vector<uint8_t> bitmap(needleWidth * needleHeight * 4);
    auto background = Sprite(0, 0, width, height);
    background.tintR = .2f; background.tintG = .4f; background.tintB = .6f; background.tintA = .6f;
    auto main = geometry.command; main.originX = 7; main.originY = 6; main.parameterW = 1;
    auto side = Sprite(5, 80, 44, 72); side.shader = 2; side.parameterX = -.15f;
    auto foreground = Sprite(64, 48, 50, 60); foreground.tintR = foreground.tintG = 0; foreground.tintA = .5f;
    WispDrawCommand scene[]{background, main, side, foreground};
    unsigned maximumPixelError = 0;
    for (const double blur : {-.35, 0.0, .35})
    {
        curve[0].blur = blur;
        geometry.opacity = blur < 0 ? .6f : 1.0f;
        scene[1].parameterX = static_cast<float>(blur); scene[1].tintA = geometry.opacity;
        Require(update() == S_OK, "signed-blur surface and bounded angle animation update independently");
        WispDrawMetrics draw{};
        Require(WispRendererDrawForPresentation(renderer, scene, ARRAYSIZE(scene), 1, &draw) == S_OK && draw.drawCount == 3,
            "atlas excludes only the marked main needle and retains the unmarked side needle");
        Require(WispRendererCaptureCompositorLayer(renderer, 0, below.data(), static_cast<uint32_t>(below.size()), width * 4) == S_OK &&
            WispRendererCaptureCompositorLayer(renderer, 2, above.data(), static_cast<uint32_t>(above.size()), width * 4) == S_OK &&
            WispRendererCaptureCompositorLayer(renderer, 1, bitmap.data(), static_cast<uint32_t>(bitmap.size()), needleWidth * 4) == S_OK,
            "contract reads the actual atlas regions and the updated DirectComposition needle bitmap");
        Require(WispRendererRender(reference, scene, ARRAYSIZE(scene), 0) == S_OK &&
            WispRendererCapture(reference, expected.data(), static_cast<uint32_t>(expected.size()), width * 4) == S_OK,
            "legacy shader provides the ordered signed-blur pixel reference");
        for (UINT y = 0; y < height; ++y) for (UINT x = 0; x < width; ++x)
        {
            const size_t at = (static_cast<size_t>(y) * width + x) * 4;
            double color[4]{static_cast<double>(below[at]), static_cast<double>(below[at + 1]),
                static_cast<double>(below[at + 2]), static_cast<double>(below[at + 3])};
            if (x >= 7 && x < 7 + needleWidth && y >= 6 && y < 6 + needleHeight)
            {
                const size_t needleAt = (static_cast<size_t>(y - 6) * needleWidth + x - 7) * 4;
                const double alpha = bitmap[needleAt + 3] * geometry.opacity / 255.0;
                for (UINT channel = 0; channel < 4; ++channel)
                    color[channel] = bitmap[needleAt + channel] * geometry.opacity + color[channel] * (1 - alpha);
            }
            const double alpha = above[at + 3] / 255.0;
            for (UINT channel = 0; channel < 4; ++channel)
            {
                color[channel] = above[at + channel] + color[channel] * (1 - alpha);
                const unsigned error = static_cast<unsigned>(std::abs(static_cast<int>(std::lround(color[channel])) - expected[at + channel]));
                maximumPixelError = (std::max)(maximumPixelError, error);
            }
        }
    }
    Require(maximumPixelError <= 4, "ordered layer pixels preserve signed shader blur and partial needle opacity within UNORM rounding");
    std::printf("compositorLayerMaximumPixelError=%u;finalCompositorResamplingVerified=false\n", maximumPixelError);
    Require(WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK && current.updates == 3 &&
        current.presents == initial.presents && current.surfaceWidth == needleWidth && current.surfaceHeight == needleHeight,
        "three independent commits draw and schedule the needle without a HUD Present call");
    RECT originalBounds{};
    Require(GetWindowRect(window, &originalBounds) != FALSE, "activation fixture saves its original hidden bounds");
    const HWND originalForeground = GetForegroundWindow();
    bool showedWindow = false;
    WispPresentMetrics activationMetrics{};
    auto presentPending = [&] {
        HRESULT result = S_FALSE;
        for (UINT attempt = 0; attempt < 100 && result == S_FALSE; ++attempt)
        {
            result = WispRendererTryPresent(renderer, 0, &activationMetrics);
            if (result == S_FALSE) Sleep(1);
        }
        return result;
    };
    HRESULT activation = presentPending();
    if (activation == 2 && !hiddenOnly)
    {
        showedWindow = true;
        SetWindowPos(window, HWND_BOTTOM, GetSystemMetrics(SM_XVIRTUALSCREEN) + 40,
            GetSystemMetrics(SM_YVIRTUALSCREEN) + 40, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)), "activation fallback shows the owned composition surface");
        Require(WispRendererWaitForFrame(renderer, 1000, nullptr, &ready) == S_OK && ready == 0,
            "occluded activation reacquires readiness before replacing its pixels");
        WispDrawMetrics activationDraw{};
        Require(update() == S_OK && WispRendererDrawForPresentation(renderer, scene, ARRAYSIZE(scene), 0, &activationDraw) == S_OK,
            "visible activation fallback resamples and prepares the split frame");
        activation = presentPending();
    }
    Require(activation == S_OK || (hiddenOnly && activation == 2),
        "full contract requires an accepted split frame; hidden-only may report legal occlusion");
    if (activation == S_OK)
    {
        WispCompositorNeedleStatus activated{}, updated{};
        Require(WispRendererGetCompositorNeedleStatus(renderer, &activated) == S_OK && activated.split == 1 && activated.visible == 1,
            "accepted atlas presentation activates the separate needle visual");
        uint64_t beforeBitmap = 0, afterBitmap = 0;
        Require(WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &beforeBitmap) == S_OK,
            "bitmap work counter is available before independent updates");
        curve[0].angle = 9; curve[1].angle = 23;
        Require(update() == S_OK && WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &afterBitmap) == S_OK &&
            afterBitmap == beforeBitmap && WispRendererGetCompositorNeedleStatus(renderer, &updated) == S_OK &&
            updated.visible == 1 && updated.presents == activated.presents && updated.updates == activated.updates + 1,
            "rotation-only curve commit reuses exact existing bitmap pixels without another HUD Present");
        activated = updated;
        curve[0].angle = 17; curve[1].angle = 31;
        curve[0].blur = -.2;
        Require(update() == S_OK && WispRendererGetCompositorNeedleStatus(renderer, &updated) == S_OK &&
            updated.split == 1 && updated.visible == 1 && updated.updates == activated.updates + 1 && updated.presents == activated.presents &&
            WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &afterBitmap) == S_OK && afterBitmap == beforeBitmap + 1,
            "changed signed blur redraws the bitmap once without another HUD Present");
        beforeBitmap = afterBitmap;
        curve[0].blur = -.20001;
        Require(update() == S_OK && WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &afterBitmap) == S_OK &&
            afterBitmap == beforeBitmap + 1,
            "even a small representable blur change redraws the exact shader without quantization");
        beforeBitmap = afterBitmap;

        Require(WispRendererWaitForFrame(renderer, 1000, nullptr, &ready) == S_OK && ready == 0,
            "geometry transition fixture obtains readiness before preparing an older atlas");
        WispDrawMetrics transitionDraw{};
        Require(WispRendererDrawForPresentation(renderer, scene, ARRAYSIZE(scene), 0, &transitionDraw) == S_OK,
            "geometry transition retains a pending atlas with the previously accepted placement");
        geometry.parentOffsetX += 2;
        scene[1].originX += 2;
        Require(update() == S_OK && WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK &&
            current.split == 1 && current.visible == 0 && current.presents == updated.presents &&
            WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &afterBitmap) == S_OK && afterBitmap == beforeBitmap,
            "translation reuses pixels while replacement geometry stays hidden over the previously presented split atlas");
        Require(presentPending() == S_OK && WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK &&
            current.split == 1 && current.visible == 0,
            "accepting an atlas drawn before the geometry update cannot activate the replacement needle");
        Require(WispRendererWaitForFrame(renderer, 1000, nullptr, &ready) == S_OK && ready == 0,
            "matching geometry fixture obtains the next HUD readiness credit");
        Require(WispRendererDrawForPresentation(renderer, scene, ARRAYSIZE(scene), 0, &transitionDraw) == S_OK &&
            presentPending() == S_OK && WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK &&
            current.split == 1 && current.visible == 1,
            "matching split-to-split atlas presentation activates the replacement geometry");
        geometry.command.tintR = .4f;
        Require(update() == S_OK && WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &afterBitmap) == S_OK &&
            afterBitmap == beforeBitmap + 1 && WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK && !current.visible,
            "artwork tint changes redraw exact pixels and retain the matching-atlas activation gate");
        beforeBitmap = afterBitmap;
        Require(update() == S_OK && WispRendererGetCompositorNeedleBitmapDrawCount(renderer, &afterBitmap) == S_OK &&
            afterBitmap == beforeBitmap,
            "repeating unchanged artwork and blur reuses the refreshed bitmap");
    }
    std::printf("compositorActivationAccepted=%s;activationHResult=0x%08lX;activationWindowShown=%s\n",
        activation == S_OK ? "true" : "false", static_cast<unsigned long>(activation), showedWindow ? "true" : "false");
    HRESULT wrongThread = S_OK;
    std::thread other([&] { wrongThread = update(); }); other.join();
    Require(wrongThread == RPC_E_WRONG_THREAD, "independent updates preserve the single render-owner contract");
    auto invalidCurve = curve[1]; curve[1].offsetSeconds = 0;
    Require(update() == E_INVALIDARG, "animation rejects duplicate curve times"); curve[1] = invalidCurve;
    QueryPerformanceCounter(&now);
    Require(WispRendererUpdateCompositorNeedle(renderer, &geometry, now.QuadPart, now.QuadPart, curve, 2) == E_INVALIDARG,
        "animation rejects an empty freshness interval");
    Require(WispRendererUpdateCompositorNeedle(renderer, nullptr, 0, 0, nullptr, 0) == S_OK &&
        WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK && !current.enabled && !current.visible,
        "source invalidation immediately clears independent animation state");
    Require(update() == S_OK && WispRendererResize(renderer, width, height, 0, 0) == S_OK &&
        WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK && !current.enabled && !current.split && !current.visible,
        "resize clears the active source even when dimensions do not change");
    Require(update() == S_OK && WispRendererSetVisible(renderer, 0) == S_OK &&
        WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK && !current.enabled && !current.visible,
        "hiding an already-hidden renderer clears the independent source");
    Require(update() == S_OK && WispRendererPrepareForResume(renderer) == S_OK &&
        WispRendererGetCompositorNeedleStatus(renderer, &current) == S_OK && !current.enabled && !current.split && !current.visible,
        "swapchain recreation clears independent state and retains the atlas");
    Require(current.atlasHeight == height * 2u, "resume preserves the two-region atlas allocation");
    Require(WispRendererWaitForFrame(renderer, 1000, nullptr, &ready) == S_OK && ready == 0,
        "expiry fixture waits before drawing on the recreated HUD chain");
    QueryPerformanceCounter(&now);
    Require(WispRendererUpdateCompositorNeedle(renderer, &geometry, now.QuadPart - frequency.QuadPart,
        now.QuadPart - frequency.QuadPart / 2, curve, 2) == S_OK,
        "an already-expired absolute animation can commit only a hidden endpoint");
    WispDrawMetrics expiredDraw{};
    Require(WispRendererDrawForPresentation(renderer, scene, ARRAYSIZE(scene), 1, &expiredDraw) == S_OK && expiredDraw.drawCount == 4,
        "expired independent state leaves the normal main and auxiliary needle commands intact");
    Require(WispRendererSetOpacity(renderer, .4f) == S_OK,
        "split-tree root accepts partial group opacity with layer composition semantics");
    WispRendererDestroy(reference); DestroyWindow(referenceWindow);
    WispRendererDestroy(renderer);
    if (showedWindow)
        SetWindowPos(window, HWND_NOTOPMOST, originalBounds.left, originalBounds.top,
            originalBounds.right - originalBounds.left, originalBounds.bottom - originalBounds.top,
            SWP_NOACTIVATE | SWP_HIDEWINDOW);
    Require(GetForegroundWindow() == originalForeground && !IsWindowVisible(window),
        "activation fixture restores hidden bounds without changing foreground focus");
}

static void CheckElectricNeedle(void* renderer)
{
    constexpr uint32_t targetWidth = 288, targetHeight = 288;
    std::vector<uint8_t> pixels(targetWidth * targetHeight * 4), unblurred, combustion;
    auto capture = [&](const WispDrawCommand& needle)
    {
        Require(SUCCEEDED(WispRendererRender(renderer, &needle, 1, 0)), "electric needle material draw");
        Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), targetWidth * 4)),
            "electric needle material readback");
    };
    // The EV speedometer uses 94 DIPs; supplementary gauges retain their
    // existing 110-DIP quad while selecting the same electric material.
    for (const float width : {94.0f, 110.0f})
    {
        auto needle = Sprite(0, 0, width, 180);
        needle.shader = 3;
        for (const float blur : {0.0f, .35f, -.35f})
        {
            needle.parameterX = blur;
            capture(needle);
            size_t visible = 0;
            bool outsideTransparent = true;
            for (uint32_t y = 0; y < targetHeight; ++y)
                for (uint32_t x = 0; x < targetWidth; ++x)
                {
                    const auto alpha = pixels[(static_cast<size_t>(y) * targetWidth + x) * 4 + 3];
                    if (alpha) ++visible;
                    if ((x >= static_cast<uint32_t>(width) || y >= 180) && alpha) outsideTransparent = false;
                }
            Require(visible > 100 && visible < static_cast<size_t>(width * 180),
                "electric needle retains bounded material coverage");
            Require(outsideTransparent, "electric needle leaves pixels outside its quad transparent");
            if (blur == 0) unblurred = pixels;
            else Require(pixels != unblurred, "both electric blur directions change the rendered material");
        }
        needle.parameterX = 0;
        needle.shader = 2;
        capture(needle);
        combustion = pixels;
        Require(combustion != unblurred, "electric needle uses its distinct authored aspect ratio");
        needle.shader = 3;
        capture(needle);
        Require(pixels == unblurred, "switching needle materials restores exact electric pixels");
        needle.shader = 2;
        capture(needle);
        Require(pixels == combustion, "electric drawing does not change combustion material pixels");
    }
    auto invalid = Sprite(0, 0, 94, 180);
    invalid.shader = 6;
    Require(WispRendererRender(renderer, &invalid, 1, 0) == E_INVALIDARG, "unknown needle shader rejected");
}

static void CheckAdditionalGaugeMaterials(void* renderer)
{
    constexpr uint32_t size = 288;
    std::vector<uint8_t> pixels(size * size * 4), full, previous;
    auto capture = [&](const WispDrawCommand& command)
    {
        Require(SUCCEEDED(WispRendererRender(renderer, &command, 1, 0)), "additional gauge material draw");
        Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), size * 4)),
            "additional gauge material readback");
    };
    auto sector = Sprite(0, 0, 128, 128);
    capture(sector);
    full = pixels;
    sector.shader = 4;
    sector.parameterY = 6.283186f;
    capture(sector);
    Require(pixels == full, "full sector preserves exact image pixels");
    sector.parameterY = 0;
    capture(sector);
    Require(std::all_of(pixels.begin(), pixels.end(), [](uint8_t value) { return value == 0; }),
        "empty sector is transparent");
    sector.parameterY = 1.5707963f;
    capture(sector);
    auto alpha = [&](uint32_t x, uint32_t y) { return pixels[(static_cast<size_t>(y) * size + x) * 4 + 3]; };
    Require(alpha(96, 96) == 255 && alpha(32, 96) == 0 && alpha(96, 32) == 0,
        "sector clips in clockwise image coordinates");
    sector.parameterY = 4.712389f;
    capture(sector);
    Require(alpha(96, 96) == 255 && alpha(32, 96) == 255 && alpha(32, 32) == 255 && alpha(96, 32) == 0,
        "sector supports sweeps larger than a semicircle");
    sector.parameterX = .31f;
    sector.parameterY = 1.2f;
    capture(sector);
    size_t antialiased = 0;
    for (size_t index = 3; index < pixels.size(); index += 4)
        if (pixels[index] > 0 && pixels[index] < 255) ++antialiased;
    Require(antialiased > 10, "sector boundaries use derivative antialiasing");
    auto digital = Sprite(0, 0, 256, 24);
    digital.shader = 5;
    digital.parameterY = .8f;
    for (const float amount : {.2f, .7f, 1.0f})
    {
        digital.parameterX = amount;
        capture(digital);
        size_t visible = 0;
        for (size_t index = 3; index < pixels.size(); index += 4) if (pixels[index]) ++visible;
        Require(visible > 100 && visible < 256 * 24, "digital material retains bounded rail coverage");
        Require(previous.empty() || pixels != previous, "digital material responds to its amount parameter");
        previous = pixels;
    }
}

static void CheckDialCache(void* renderer, bool cpuRendering)
{
    uint32_t width = 288, height = 288;
    WispDrawCommand commands[] = {Sprite(12.25f, -7.5f, 266.5f, 277.25f), Sprite(130, 130, 24, 19, 1)};
    commands[0].shader = 1;
    commands[0].parameterX = .8f;
    commands[0].parameterY = .1f;
    commands[0].tintA = .73f;
    auto transparent = Sprite(0, 0, 1, 1);
    transparent.tintA = 0;
    std::vector<uint8_t> pixels, expected;
    auto capture = [&](const WispDrawCommand* draws, uint32_t count, std::vector<uint8_t>& output)
    {
        output.resize(static_cast<size_t>(width) * height * 4);
        Require(SUCCEEDED(WispRendererRender(renderer, draws, count, 0)), "cache pixel comparison draw");
        Require(SUCCEEDED(WispRendererCapture(renderer, output.data(), static_cast<uint32_t>(output.size()), width * 4)),
            "cache pixel comparison readback");
    };
    auto compare = [&]
    {
        // A transparent first sprite forces the original full-draw path without
        // changing the image or the order of either visible command.
        WispDrawCommand reference[] = {transparent, commands[0], commands[1]};
        capture(reference, 3, expected);
        capture(commands, 2, pixels);
        Require(pixels == expected, "CPU dial cache preserves exact uncached pixels");
        capture(commands, 2, pixels);
        Require(pixels == expected, "reused dial preserves exact uncached pixels");
    };
    auto measure = [&](uint32_t expectedDraws)
    {
        WispDrawMetrics metrics{};
        Require(SUCCEEDED(WispRendererDrawForPresentation(renderer, commands, 2, 1, &metrics)), "measured cache draw");
        Require(metrics.drawCount == expectedDraws && metrics.mapCount == expectedDraws,
            "cache metrics count only actual draws and maps");
        Require(TimingsFitTotal(metrics.totalTicks, metrics.setupTicks, metrics.mapTicks),
            "cache setup and maps remain disjoint nested timings");
    };
    const uint32_t cachedDraws = cpuRendering ? 1u : 2u;
    compare();
    measure(cachedDraws);
    const auto previous = pixels;
    commands[1].originX = 20;
    commands[1].originY = 210;
    commands[1].tintA = .5f;
    measure(cachedDraws);
    compare();
    Require(pixels != previous, "dynamic pixels remain live when the dial is cached");

    commands[0].parameterX = .42f;
    commands[0].parameterY = .125f;
    measure(2);
    compare();
    commands[0].originX += .5f;
    commands[0].axisXY = 13.5f;
    commands[0].uvLeft = .05f;
    commands[0].tintG = .7f;
    measure(2);
    compare();

    commands[0].textureId = 1;
    measure(2);
    compare();
    const uint8_t replacement[] = {16, 8, 4, 64};
    Require(SUCCEEDED(WispRendererUploadTexture(renderer, 1, 1, 1, 4, replacement, 4)), "cache source texture replacement");
    measure(2);
    compare();
    Require(SUCCEEDED(WispRendererRemoveTexture(renderer, 1)), "cache source texture removal");
    Require(WispRendererRender(renderer, commands, 2, 0) == E_INVALIDARG,
        "cached pixels never bypass missing texture validation");
    const uint8_t original[] = {64, 32, 16, 128};
    Require(SUCCEEDED(WispRendererUploadTexture(renderer, 1, 1, 1, 4, original, 4)), "restore source texture");
    measure(2);
    compare();

    width = 320; height = 304;
    Require(SUCCEEDED(WispRendererResize(renderer, width, height, 2.5f, 3.5f)), "resize releases old cache target");
    measure(2);
    compare();
    measure(cachedDraws);
    Require(SUCCEEDED(WispRendererResize(renderer, 8192, 513, 0, 0)), "oversized cache uses normal drawing");
    measure(2);
    measure(2);
    width = height = 288;
    Require(SUCCEEDED(WispRendererResize(renderer, width, height, 2.5f, 3.5f)), "restore target after cache bound check");
    measure(2);
    compare();
    measure(cachedDraws);
    capture(commands, 2, pixels);
}

static void CheckHiddenOpacityLifecycle(void* renderer, HWND window)
{
    constexpr uint32_t size = 288;
    const HWND foreground = GetForegroundWindow();
    Require(!IsWindowVisible(window), "opacity contract HWND remains hidden");
    WispDrawCommand commands[] = {Sprite(0, 0, 32, 32, 1), Sprite(16, 16, 32, 32, 1)};
    std::vector<uint8_t> pixels(size * size * 4), expected;
    auto capture = [&]
    {
        Require(SUCCEEDED(WispRendererRender(renderer, commands, 2, 0)), "opacity contract offscreen draw");
        Require(SUCCEEDED(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), size * 4)),
            "opacity contract source readback");
    };
    capture();
    expected = pixels;
    for (const float opacity : {0.0f, .35f, 1.0f})
    {
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 0)), "opacity contract hides visual");
        Require(SUCCEEDED(WispRendererSetOpacity(renderer, opacity)), "opacity stored while hidden");
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)), "stored opacity commits on visual show");
        Require(SUCCEEDED(WispRendererSetOpacity(renderer, 1.0f - opacity)) &&
            SUCCEEDED(WispRendererSetOpacity(renderer, opacity)), "opacity changes commit while visual is active");
        // Readback precedes DirectComposition: this checks unchanged source pixels,
        // not the final compositor opacity or displayed-frame timing.
        capture();
        Require(pixels == expected, "composition opacity preserves precomposed source pixels");
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 0)) &&
            WispRendererPrepareForResume(renderer) == S_OK &&
            WispRendererPrepareForResume(renderer) == S_FALSE, "opacity lifecycle replaces the hidden drawn chain once");
        Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)), "opacity lifecycle shows resumed visual");
        capture();
        Require(pixels == expected, "opacity lifecycle preserves overlapping source artwork after resume");
        Require(!IsWindowVisible(window) && GetForegroundWindow() == foreground,
            "opacity lifecycle never shows or activates its HWND");
    }
    for (const float invalid : {-0.01f, 1.01f, std::nanf(""), INFINITY})
        Require(WispRendererSetOpacity(renderer, invalid) == E_INVALIDARG, "invalid group opacity is rejected");
    HRESULT wrongThread = S_OK;
    std::thread other([&] { wrongThread = WispRendererSetOpacity(renderer, .5f); });
    other.join();
    Require(wrongThread == RPC_E_WRONG_THREAD, "opacity commits retain renderer thread ownership");
    Require(SUCCEEDED(WispRendererSetVisible(renderer, 0)), "opacity contract leaves visual hidden");
    Require(!IsWindowVisible(window) && GetForegroundWindow() == foreground,
        "hidden opacity contract preserves foreground");
}

static void CheckInitialReadinessCredits(HWND window, bool cpuRendering)
{
    // The transparent initialization frame must consume a credit too. Once
    // outstanding presents retire, only the configured two credits may remain.
    constexpr uint32_t expectedCredits = 2;
    auto drain = [&](void* renderer, const char* phase)
    {
        uint32_t credits = 0, timeouts = 0;
        bool healthy = true;
        for (uint32_t attempt = 0; attempt < 12 && credits <= expectedCredits; ++attempt)
        {
            uint32_t ready = 99;
            const HRESULT result = WispRendererWaitForFrame(renderer, 100, nullptr, &ready);
            if (FAILED(result) || ready > 1)
            {
                healthy = false;
                break;
            }
            if (ready == 0) ++credits;
            else ++timeouts;
        }
        std::printf("readinessCreditPhase=%s;credits=%u;timeouts=%u;healthy=%s;expectedCredits=%u\n",
            phase, credits, timeouts, healthy ? "true" : "false", expectedCredits);
        Require(healthy, "readiness credit observation remains healthy");
        Require(credits >= expectedCredits, "readiness credit observation completes queued presentation");
        Require(credits <= expectedCredits, "transparent initial frame does not add an unmatched readiness credit");
    };

    const HWND foreground = GetForegroundWindow();
    RECT previous{};
    Require(GetWindowRect(window, &previous) != FALSE, "credit test preserves the original window bounds");
    SetWindowPos(window, HWND_TOPMOST,
        GetSystemMetrics(SM_XVIRTUALSCREEN) + 40, GetSystemMetrics(SM_YVIRTUALSCREEN) + 40,
        32, 32, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    Require(IsWindowVisible(window) && GetForegroundWindow() == foreground,
        "credit test uses a visible window without activating it");

    void* renderer = nullptr;
    const HRESULT created = WispRendererCreateWithMode(window, 32, 32, cpuRendering ? 1 : 0, &renderer);
    if (FAILED(created) || !renderer)
        std::printf("creditTestCreateHResult=0x%08lX;rendererCreated=%s\n", static_cast<unsigned long>(created), renderer ? "true" : "false");
    Require(SUCCEEDED(created) && renderer, "credit test creates the production renderer");
    if (renderer)
    {
        const auto initialStatus = CheckPresentationStatus(renderer, cpuRendering, UINT32_MAX, true);
        Require(SUCCEEDED(WispRendererSetOpacity(renderer, .01f)) &&
            SUCCEEDED(WispRendererSetVisible(renderer, 1)), "credit test shows the initial composition surface");
        // A balanced real frame ensures this target is participating in
        // composition before counting credits. It cannot add an extra credit.
        uint32_t ready = 99;
        const HRESULT waited = WispRendererWaitForFrame(renderer, 1000, nullptr, &ready);
        bool submitted = false;
        Require(SUCCEEDED(waited) && ready == 0, "credit test obtains its first frame credit");
        if (SUCCEEDED(waited) && ready == 0)
        {
            auto sample = Sprite(0, 0, 32, 32);
            sample.tintG = sample.tintB = 0;
            WispDrawMetrics drawMetrics{};
            const HRESULT drawn = WispRendererDrawForPresentation(renderer, &sample, 1, 0, &drawMetrics);
            Require(SUCCEEDED(drawn), "credit test draws one balanced frame");
            if (SUCCEEDED(drawn))
            {
                WispPresentMetrics presentMetrics{};
                for (uint32_t attempt = 0; attempt < 100; ++attempt)
                {
                    const HRESULT presented = WispRendererTryPresent(renderer, 0, &presentMetrics);
                    if (presented == S_OK) { submitted = true; break; }
                    if (presented != S_FALSE) break;
                    Sleep(1);
                }
            }
        }
        Require(submitted, "credit test successfully submits its balanced frame");
        const auto submittedStatus = CheckPresentationStatus(renderer, cpuRendering, initialStatus.swapChainFlags);
        if (submitted)
            Require(submittedStatus.lastPresentHResult == S_OK,
                "actual attached composition chain accepts its policy-selected Present flags");
        if (submitted)
        {
            drain(renderer, "initialize");
            const HRESULT hidden = WispRendererSetVisible(renderer, 0);
            const HRESULT resumed = SUCCEEDED(hidden) ? WispRendererPrepareForResume(renderer) : hidden;
            Require(SUCCEEDED(hidden) && resumed == S_OK, "credit test replaces the old swapchain on resume");
            if (SUCCEEDED(hidden) && resumed == S_OK)
            {
                CheckPresentationStatus(renderer, cpuRendering, initialStatus.swapChainFlags, true);
                Require(SUCCEEDED(WispRendererSetVisible(renderer, 1)), "credit test shows the resumed composition surface");
                drain(renderer, "resume");
            }
        }
        WispRendererDestroy(renderer);
    }
    SetWindowPos(window, HWND_NOTOPMOST, previous.left, previous.top,
        previous.right - previous.left, previous.bottom - previous.top,
        SWP_NOACTIVATE | SWP_HIDEWINDOW);
    Require(GetForegroundWindow() == foreground, "credit test never changes foreground focus");
}

static void CheckVisiblePacing(void* renderer, HWND window, bool cpuRendering)
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
    std::vector<double> pacingWaits, dxgiWaits;
    waits.reserve(2048);
    pacingWaits.reserve(2048); dxgiWaits.reserve(2048);
    WispWaitMetricsV2 waitMetrics{};
    WispDrawMetrics drawMetrics{};
    WispPresentMetrics presentMetrics{};
    while (now.QuadPart - started.QuadPart < frequency.QuadPart * 3 && submitted < 10000)
    {
        if (!ready)
        {
            uint32_t waitResult = 99;
            const HRESULT result = WispRendererWaitForFrameMeasuredV2(renderer, 100, nullptr, 1, &waitResult, &waitMetrics);
            Require(SUCCEEDED(result), "pacing wait remains healthy");
            if (FAILED(result)) break;
            Require(waitMetrics.pacingTicks >= 0 && waitMetrics.wait.waitCallTicks >= 0 &&
                TimingsFitTotal(waitMetrics.wait.totalTicks, waitMetrics.wait.precheckTicks,
                    waitMetrics.wait.waitCallTicks, waitMetrics.wait.postcheckTicks) &&
                waitMetrics.wait.totalTicks >= waitMetrics.pacingTicks &&
                waitMetrics.wait.totalTicks - waitMetrics.pacingTicks >= waitMetrics.wait.waitCallTicks,
                "software gate and DXGI wait report separate bounded durations");
            Require(waitMetrics.syncInterval <= 1 &&
                (waitMetrics.syncInterval != 0 || (waitMetrics.refreshRate > 0 && SUCCEEDED(waitMetrics.pacingHResult))),
                "interval-zero rendering requires an available configured software pacer");
            waits.push_back(static_cast<double>(waitMetrics.wait.totalTicks) * 1000.0 / ticksPerSecond);
            pacingWaits.push_back(static_cast<double>(waitMetrics.pacingTicks) * 1000.0 / ticksPerSecond);
            dxgiWaits.push_back(static_cast<double>(waitMetrics.wait.waitCallTicks) * 1000.0 / ticksPerSecond);
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
                ready = pending = false;
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
    std::sort(pacingWaits.begin(), pacingWaits.end());
    std::sort(dxgiWaits.begin(), dxgiWaits.end());
    const double p95 = waits.empty() ? 0 : waits[(waits.size() - 1) * 95 / 100];
    const double pacingP95 = pacingWaits.empty() ? 0 : pacingWaits[(pacingWaits.size() - 1) * 95 / 100];
    const double dxgiP95 = dxgiWaits.empty() ? 0 : dxgiWaits[(dxgiWaits.size() - 1) * 95 / 100];
    std::printf("pacingSeconds=%.3f;displayHz=%lu;submittedPerSecond=%.3f;waitP95Ms=%.3f;softwareWaitP95Ms=%.3f;dxgiWaitP95Ms=%.3f;effectiveSyncInterval=%u;pacingRefreshHz=%u;pacingHResult=0x%08lX;busy=%u;occluded=%u;timeouts=%u\n",
        seconds, mode.dmDisplayFrequency, submitted / seconds, p95, pacingP95, dxgiP95,
        waitMetrics.syncInterval, waitMetrics.refreshRate, static_cast<unsigned long>(waitMetrics.pacingHResult),
        busy, occluded, timeouts);
    // This checks actual submissions, not displayed frames. Allow initial queue
    // credits and clock variance, but fail an accidentally unthrottled loop.
    Require(submitted >= 2, "visible pacing successfully submits frames");
    Require(submitted <= mode.dmDisplayFrequency * seconds * 1.25 + 4,
        "presentation throughput remains bounded by display cadence");
    if (!cpuRendering)
        Require(submitted >= mode.dmDisplayFrequency * seconds * .90,
            "small hardware fixture retains at least ninety percent of display-rate submissions");
}

int main(int argc, char** argv)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    if (argc == 2 && std::strcmp(argv[1], "--pacer-only") == 0)
    {
        CheckFramePacer();
        std::printf("pacerOnlyFailures=%d\n", failures);
        return failures ? 1 : 0;
    }
    CheckFramePacer();
    CheckTearingPolicy();
    bool cpuRendering = false, hiddenOnly = false;
    for (int index = 1; index < argc; ++index)
    {
        if (std::strcmp(argv[index], "--cpu") == 0 && !cpuRendering) cpuRendering = true;
        else if (std::strcmp(argv[index], "--hidden") == 0 && !hiddenOnly) hiddenOnly = true;
        else return 2;
    }
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
        L"STATIC", L"Wisp native renderer contract", WS_POPUP, -32000, -32000, 288, 288,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Require(window != nullptr, "hidden owned window");
    if (!window) return 1;
    HWND foregroundBefore = GetForegroundWindow();
    CheckCompositorNeedle(window, cpuRendering, hiddenOnly);
    CheckIndependentMotion(window, cpuRendering, hiddenOnly);
    Require(GetForegroundWindow() == foregroundBefore && !IsWindowVisible(window),
        "independent compositor contracts do not activate or show a window");
    if (!hiddenOnly) CheckInitialReadinessCredits(window, cpuRendering);
    CheckShortWaitBudget(window, cpuRendering);
    void* renderer = nullptr;
    Require(WispRendererCreateWithMode(window, 32, 32, 2, &renderer) == E_INVALIDARG && !renderer, "invalid renderer mode rejected");
    auto beforeProcessClass = D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL;
    const NTSTATUS beforeProcessRead = D3DKMTGetProcessSchedulingPriorityClass(GetCurrentProcess(), &beforeProcessClass);
    HRESULT result = cpuRendering ? WispRendererCreateWithMode(window, 32, 32, 1, &renderer)
        : WispRendererCreate(window, 32, 32, &renderer);
    if (FAILED(result) || !renderer)
        std::printf("contractCreateHResult=0x%08lX;rendererCreated=%s\n", static_cast<unsigned long>(result), renderer ? "true" : "false");
    Require(SUCCEEDED(result) && renderer, "selected D3D11 driver and DirectComposition creation");
    if (!renderer) { DestroyWindow(window); return 1; }
    const auto initialPresentationStatus = CheckPresentationStatus(renderer, cpuRendering, UINT32_MAX, true);
    std::printf("tearingSupported=%u;featureHResult=0x%08lX;chainFlags=0x%08X;syncInterval=%u;presentFlags=0x%08X;primerFlags=0x%08X;primerHResult=0x%08lX\n",
        initialPresentationStatus.tearingSupported, static_cast<unsigned long>(initialPresentationStatus.tearingSupportHResult),
        initialPresentationStatus.swapChainFlags, initialPresentationStatus.syncInterval, initialPresentationStatus.presentFlags,
        initialPresentationStatus.lastPresentFlags, static_cast<unsigned long>(initialPresentationStatus.lastPresentHResult));
    Require(WispRendererGetPresentationStatus(renderer, nullptr) == E_POINTER,
        "presentation status rejects a missing output");
    WispPresentationStatus rejectedStatus{};
    Require(WispRendererGetPresentationStatus(nullptr, &rejectedStatus) == E_POINTER,
        "presentation status rejects a missing renderer");
    HRESULT wrongPresentationThread = S_OK;
    std::thread presentationThread([&] {
        wrongPresentationThread = WispRendererGetPresentationStatus(renderer, &rejectedStatus);
    });
    presentationThread.join();
    Require(wrongPresentationThread == RPC_E_WRONG_THREAD,
        "presentation status respects render-thread ownership");
    CheckGpuPriority(renderer, cpuRendering, beforeProcessRead, beforeProcessClass);
    uint32_t initialReady = 99;
    WispWaitMetrics waitMetrics{};
    Require(SUCCEEDED(WispRendererWaitForFrameMeasured(renderer, 100, nullptr, 1, &initialReady, &waitMetrics)),
        "initial measured queue wait");
    Require(waitMetrics.totalTicks > 0 && waitMetrics.precheckTicks >= 0 && waitMetrics.waitCallTicks > 0 &&
        waitMetrics.postcheckTicks >= 0 && TimingsFitTotal(waitMetrics.totalTicks,
            waitMetrics.precheckTicks, waitMetrics.waitCallTicks, waitMetrics.postcheckTicks) &&
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
    CheckPresentationStatus(renderer, cpuRendering, initialPresentationStatus.swapChainFlags);
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
    CheckElectricNeedle(renderer);
    CheckAdditionalGaugeMaterials(renderer);
    CheckDialCache(renderer, cpuRendering);
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
    struct LegacyWaitStorage { WispWaitMetrics metrics{}; uint64_t sentinel = 0xFEDCBA9876543210ull; } legacy;
    Require(WispRendererWaitForFrameMeasured(renderer, 100, cancellation, 1, &waited, &legacy.metrics) == S_OK &&
        legacy.sentinel == 0xFEDCBA9876543210ull,
        "legacy wait export writes no bytes beyond its original metrics buffer");
    WispWaitMetricsV2 pacedWait{};
    Require(WispRendererWaitForFrameMeasuredV2(renderer, 100, cancellation, 1, &waited, &pacedWait) == S_OK &&
        waited == 2 && pacedWait.wait.totalTicks > 0 && pacedWait.wait.waitCallTicks == 0 &&
        pacedWait.pacingTicks == 0 && pacedWait.pacingWaitResult == WAIT_FAILED,
        "V2 cancellation returns before both pacing and DXGI waits");
    std::memset(&pacedWait, 0xFF, sizeof(pacedWait));
    const WispWaitMetricsV2 emptyPacedWait{};
    Require(WispRendererWaitForFrameMeasuredV2(renderer, 100, cancellation, 0, &waited, &pacedWait) == S_OK &&
        waited == 2 && std::memcmp(&pacedWait, &emptyPacedWait, sizeof(pacedWait)) == 0,
        "disabled V2 measurement zeroes every field while retaining cancellation behavior");
    std::thread otherPacedWait([&] {
        wrongThread = WispRendererWaitForFrameMeasuredV2(renderer, 100, cancellation, 1, &waited, &pacedWait);
    });
    otherPacedWait.join();
    Require(wrongThread == RPC_E_WRONG_THREAD && pacedWait.wait.waitCallTicks == 0 && pacedWait.pacingTicks == 0 &&
        pacedWait.pacingWaitResult == WAIT_FAILED, "V2 owner-thread rejection performs neither wait");
    Require(WispRendererWaitForFrameMeasuredV2(renderer, 100, cancellation, 1, &waited, nullptr) == E_POINTER,
        "V2 wait requires its metrics output");
    Require(WispRendererWaitForFrameMeasuredV2(renderer, 1001, cancellation, 1, &waited, &pacedWait) == E_INVALIDARG &&
        pacedWait.wait.waitCallTicks == 0 && pacedWait.pacingTicks == 0,
        "V2 invalid timeout is rejected before consuming either wait");
    Require(WispRendererWaitForFrameMeasuredV2(renderer, 100, cancellation, 1, nullptr, &pacedWait) == E_INVALIDARG &&
        pacedWait.wait.waitCallTicks == 0 && pacedWait.pacingTicks == 0, "V2 wait requires its result output");
    Require(WispRendererWaitForFrameMeasuredV2(nullptr, 100, cancellation, 1, &waited, &pacedWait) == E_POINTER &&
        pacedWait.wait.waitCallTicks == 0 && pacedWait.pacingTicks == 0,
        "V2 null renderer reports failure without waiting");
    CloseHandle(cancellation);
    WispDrawMetrics drawMetrics{};
    WispPresentMetrics presentMetrics{};
    Require(WispRendererTryPresent(renderer, 1, &presentMetrics) == HRESULT_FROM_WIN32(ERROR_INVALID_STATE),
        "capture-only target cannot be presented");
    Require(presentMetrics.hResult == HRESULT_FROM_WIN32(ERROR_INVALID_STATE), "invalid presentation status is recorded");
    Require(SUCCEEDED(WispRendererDrawForPresentation(renderer, overlap, 2, 1, &drawMetrics)), "separate measured draw");
    Require(drawMetrics.drawCount == 2 && drawMetrics.mapCount == 2 && drawMetrics.totalTicks > 0 &&
        drawMetrics.setupTicks > 0 && drawMetrics.mapTicks >= drawMetrics.maximumMapTicks &&
        TimingsFitTotal(drawMetrics.totalTicks, drawMetrics.setupTicks, drawMetrics.mapTicks),
        "CPU draw metrics include setup and each constant map");
    Require(WispRendererCapture(renderer, pixels.data(), static_cast<uint32_t>(pixels.size()), 1152) == E_INVALIDARG,
        "presentation draw does not enable readback");
    const HRESULT separatePresent = WispRendererTryPresent(renderer, 1, &presentMetrics);
    const auto separateStatus = CheckPresentationStatus(renderer, cpuRendering, initialPresentationStatus.swapChainFlags);
    Require(separateStatus.lastPresentHResult == presentMetrics.hResult &&
        separateStatus.lastPresentSyncInterval == separateStatus.syncInterval,
        "actual drawn-frame Present records the effective sync interval and raw HRESULT");
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
    if (hiddenOnly)
    {
        CheckHiddenOpacityLifecycle(renderer, window);
        CheckPresentationStatus(renderer, cpuRendering, initialPresentationStatus.swapChainFlags);
        Require(SUCCEEDED(WispRendererDeviceRemovedReason(renderer)), "hidden device remains healthy");
        WispRendererDestroy(renderer);
        Require(!IsWindowVisible(window) && GetForegroundWindow() == foregroundBefore, "hidden contract never changes foreground");
        DestroyWindow(window);
        std::printf("{\"contractFailures\":%d,\"cpuRendering\":%s,\"testWindowShown\":false,\"finalCompositionOpacityVerified\":false}\n",
            failures, cpuRendering ? "true" : "false");
        return failures ? 1 : 0;
    }
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
        CheckPresentationStatus(renderer, cpuRendering, initialPresentationStatus.swapChainFlags, true);
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
    CheckVisiblePacing(renderer, window, cpuRendering);
    Require(SUCCEEDED(WispRendererSetOpacity(renderer, .5f)), "group opacity accepts half alpha");
    Require(WispRendererSetOpacity(renderer, 1.5f) == E_INVALIDARG, "invalid group opacity rejected");
    Require(SUCCEEDED(WispRendererDeviceRemovedReason(renderer)), "device remains healthy");
    WispRendererDestroy(renderer);
    Require(GetForegroundWindow() == foregroundBefore, "no foreground changes");
    DestroyWindow(window);
    std::printf("{\"contractFailures\":%d,\"cpuRendering\":%s,\"testWindowShown\":true}\n", failures, cpuRendering ? "true" : "false");
    return failures ? 1 : 0;
}
