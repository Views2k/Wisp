#pragma once
#include <windows.h>
#include <cstdint>

namespace FramePacerMath
{
    inline constexpr bool Add(int64_t first, int64_t second, int64_t& result) noexcept
    {
        if (first < 0 || second < 0 || first > INT64_MAX - second) return false;
        result = first + second;
        return true;
    }

    inline constexpr bool ScaleCeiling(int64_t value, int64_t multiplier,
        int64_t divisor, int64_t& result) noexcept
    {
        if (value < 0 || multiplier <= 0 || divisor <= 0) return false;
        const int64_t whole = value / divisor;
        const int64_t remainder = value % divisor;
        if (whole > INT64_MAX / multiplier || remainder > INT64_MAX / multiplier) return false;
        const int64_t product = remainder * multiplier;
        return Add(whole * multiplier,
            product / divisor + (product % divisor != 0 ? 1 : 0), result);
    }

    inline constexpr bool RemainingMilliseconds(int64_t started, int64_t now,
        int64_t frequency, uint32_t timeout, DWORD& remaining) noexcept
    {
        if (started < 0 || now < started || frequency <= 0 || timeout == INFINITE) return false;
        int64_t budget = 0, expires = 0, milliseconds = 0;
        if (!ScaleCeiling(timeout, frequency, 1000, budget) || !Add(started, budget, expires)) return false;
        // Round the remaining duration up. Rounding elapsed time up first would
        // turn a 1 ms budget into a zero-time poll after even one QPC tick.
        if (now < expires && !ScaleCeiling(expires - now, 1000, frequency, milliseconds)) return false;
        if (milliseconds >= INFINITE) return false;
        remaining = milliseconds > timeout ? timeout : static_cast<DWORD>(milliseconds);
        return true;
    }
}

// One cached high-resolution timer bounds the complete render wait. Its signal
// is shared by the software pacing and DXGI waits, never a readiness credit.
class FrameWaitBudget final
{
    HANDLE timer_ = nullptr;
    DWORD initializationError_ = ERROR_SUCCESS;
    bool armed_ = false;
public:
    FrameWaitBudget() noexcept
    {
        timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_MODIFY_STATE | SYNCHRONIZE);
        if (!timer_) initializationError_ = GetLastError();
    }
    ~FrameWaitBudget() noexcept { if (timer_) { Cancel(); CloseHandle(timer_); } }
    FrameWaitBudget(const FrameWaitBudget&) = delete;
    FrameWaitBudget& operator=(const FrameWaitBudget&) = delete;
    void Cancel() noexcept
    {
        const DWORD error = GetLastError();
        if (armed_) CancelWaitableTimer(timer_);
        armed_ = false;
        SetLastError(error);
    }
    HANDLE Handle() const noexcept { return armed_ ? timer_ : nullptr; }
    HRESULT Arm(int64_t started, int64_t frequency, DWORD milliseconds) noexcept
    {
        Cancel();
        if (!timer_) return HRESULT_FROM_WIN32(initializationError_ ? initializationError_ : ERROR_GEN_FAILURE);
        if (started < 0 || frequency <= 0 || milliseconds == INFINITE) return E_INVALIDARG;
        int64_t ticks = 0, expires = 0, relative100ns = 0;
        if (!FramePacerMath::ScaleCeiling(milliseconds, frequency, 1000, ticks) ||
            !FramePacerMath::Add(started, ticks, expires)) return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        LARGE_INTEGER now{};
        if (!QueryPerformanceCounter(&now) || now.QuadPart < started) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        if (now.QuadPart >= expires) return S_FALSE;
        if (!FramePacerMath::ScaleCeiling(expires - now.QuadPart, 10000000, frequency, relative100ns))
            return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        LARGE_INTEGER due{};
        due.QuadPart = -relative100ns;
        if (!SetWaitableTimerEx(timer_, &due, 0, nullptr, nullptr, nullptr, 0)) return HRESULT_FROM_WIN32(GetLastError());
        armed_ = true;
        return S_OK;
    }
    struct Scope final
    {
        FrameWaitBudget& budget;
        ~Scope() noexcept { budget.Cancel(); }
    };
};

// Pure schedule state, separate from Windows waits for deterministic tests.
class FramePacerDeadline final
{
    int64_t frequency_ = 0, interval_ = 0, deadline_ = 0, lastPresent_ = 0;
    uint32_t hertz_ = 0;
    bool hasDeadline_ = false;
public:
    bool Configure(int64_t frequency, uint32_t hertz) noexcept
    {
        if (frequency <= 0 || !hertz)
        {
            frequency_ = interval_ = 0;
            hertz_ = 0;
            Reset();
            return false;
        }
        if (frequency_ != frequency || hertz_ != hertz)
        {
            frequency_ = frequency;
            hertz_ = hertz;
            interval_ = frequency / hertz + (frequency % hertz != 0 ? 1 : 0);
            Reset();
        }
        return true;
    }
    void Reset() noexcept { deadline_ = lastPresent_ = 0; hasDeadline_ = false; }
    bool IsConfigured() const noexcept { return interval_ > 0; }
    bool HasDeadline() const noexcept { return hasDeadline_; }
    int64_t Deadline() const noexcept { return deadline_; }
    int64_t Interval() const noexcept { return interval_; }
    uint32_t Hertz() const noexcept { return hertz_; }

    bool AdvanceAfterPresent(int64_t now) noexcept
    {
        if (!IsConfigured() || now < 0 || (hasDeadline_ && now < lastPresent_)) return false;
        int64_t next = 0;
        if (!FramePacerMath::Add(hasDeadline_ ? deadline_ : now, interval_, next)) return false;
        // Preserve the phase, skipping expired slots instead of submitting an
        // immediate catch-up frame. A future phase can still be only one tick
        // away; this is not a guaranteed minimum spacing between submissions.
        if (next <= now)
        {
            const int64_t remaining = interval_ - ((now - next) % interval_);
            if (!FramePacerMath::Add(now, remaining, next)) return false;
        }
        deadline_ = next;
        lastPresent_ = now;
        hasDeadline_ = true;
        return true;
    }
};

// Render-thread-owned rate gate. It never receives or consumes a DXGI credit.
class FramePacer final
{
    HANDLE timer_ = nullptr;
    int64_t frequency_ = 0, lastWaitTicks_ = 0;
    DWORD initializationError_ = ERROR_SUCCESS;
    FramePacerDeadline schedule_;

    static DWORD Failed(DWORD error) noexcept
    {
        SetLastError(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
        return WAIT_FAILED;
    }
    struct WaitScope final
    {
        FramePacer& owner;
        int64_t started;
        bool armed = false;
        ~WaitScope() noexcept
        {
            const DWORD error = GetLastError();
            if (armed) CancelWaitableTimer(owner.timer_);
            LARGE_INTEGER ended{};
            if (QueryPerformanceCounter(&ended) && ended.QuadPart >= started)
                owner.lastWaitTicks_ = ended.QuadPart - started;
            SetLastError(error);
        }
    };
public:
    FramePacer() noexcept
    {
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0)
        {
            initializationError_ = ERROR_GEN_FAILURE;
            return;
        }
        frequency_ = frequency.QuadPart;
        timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_MODIFY_STATE | SYNCHRONIZE);
        if (!timer_)
        {
            initializationError_ = GetLastError();
            if (!initializationError_) initializationError_ = ERROR_GEN_FAILURE;
        }
    }
    ~FramePacer() noexcept
    {
        if (timer_) { CancelWaitableTimer(timer_); CloseHandle(timer_); }
    }
    FramePacer(const FramePacer&) = delete;
    FramePacer& operator=(const FramePacer&) = delete;

    bool IsValid() const noexcept { return timer_ != nullptr; }
    DWORD InitializationError() const noexcept { return initializationError_; }
    int64_t Frequency() const noexcept { return frequency_; }
    int64_t LastWaitTicks() const noexcept { return lastWaitTicks_; }
    bool Configure(uint32_t hertz) noexcept
    {
        if (!IsValid()) { Failed(initializationError_); return false; }
        if (!schedule_.Configure(frequency_, hertz)) { Failed(ERROR_INVALID_PARAMETER); return false; }
        return true;
    }
    void Reset() noexcept
    {
        schedule_.Reset();
        if (timer_) CancelWaitableTimer(timer_);
    }
    bool AdvanceAfterPresent(int64_t now) noexcept
    {
        if (!IsValid()) { Failed(initializationError_); return false; }
        if (!schedule_.AdvanceAfterPresent(now)) { Failed(ERROR_INVALID_DATA); return false; }
        return true;
    }

    // Ready=0, canceled=1, otherwise WAIT_TIMEOUT/WAIT_FAILED. The caller must
    // fall back to synchronized presentation on failure, never uncap Present0.
    DWORD Wait(DWORD timeout, HANDLE cancellation = nullptr, HANDLE budgetDeadline = nullptr) noexcept
    {
        lastWaitTicks_ = 0;
        if (!IsValid()) return Failed(initializationError_);
        if (!schedule_.IsConfigured()) return Failed(ERROR_INVALID_STATE);
        LARGE_INTEGER started{};
        if (!QueryPerformanceCounter(&started)) return Failed(ERROR_GEN_FAILURE);
        WaitScope scope{*this, started.QuadPart};
        if (timeout == INFINITE) return Failed(ERROR_INVALID_PARAMETER);
        int64_t budget = 0, expires = 0;
        if (!FramePacerMath::ScaleCeiling(timeout, frequency_, 1000, budget) ||
            !FramePacerMath::Add(started.QuadPart, budget, expires)) return Failed(ERROR_ARITHMETIC_OVERFLOW);
        for (;;)
        {
            if (cancellation)
            {
                const DWORD canceled = WaitForSingleObject(cancellation, 0);
                if (canceled == WAIT_OBJECT_0) return WAIT_OBJECT_0 + 1;
                if (canceled == WAIT_FAILED) return Failed(GetLastError());
                if (canceled != WAIT_TIMEOUT) return Failed(ERROR_INVALID_HANDLE);
            }
            LARGE_INTEGER now{};
            if (!QueryPerformanceCounter(&now) || now.QuadPart < started.QuadPart)
                return Failed(ERROR_INVALID_DATA);
            if (!schedule_.HasDeadline() || now.QuadPart >= schedule_.Deadline()) return WAIT_OBJECT_0;
            if (!timeout || now.QuadPart >= expires) return WAIT_TIMEOUT;
            int64_t relative100ns = 0;
            if (!FramePacerMath::ScaleCeiling(schedule_.Deadline() - now.QuadPart,
                10000000, frequency_, relative100ns)) return Failed(ERROR_ARITHMETIC_OVERFLOW);
            LARGE_INTEGER due{};
            due.QuadPart = -relative100ns;
            if (!SetWaitableTimerEx(timer_, &due, 0, nullptr, nullptr, nullptr, 0)) return Failed(GetLastError());
            scope.armed = true;
            // Timer setup and any early-wake retry share the original budget.
            if (!QueryPerformanceCounter(&now) || now.QuadPart < started.QuadPart)
                return Failed(ERROR_INVALID_DATA);
            int64_t remainingMs = 0;
            if (now.QuadPart < expires &&
                !FramePacerMath::ScaleCeiling(expires - now.QuadPart, 1000, frequency_, remainingMs))
                return Failed(ERROR_ARITHMETIC_OVERFLOW);
            if (remainingMs >= INFINITE) return Failed(ERROR_ARITHMETIC_OVERFLOW);
            HANDLE handles[3]{};
            DWORD handleCount = 0;
            if (cancellation) handles[handleCount++] = cancellation;
            const DWORD readyIndex = handleCount;
            handles[handleCount++] = timer_;
            const DWORD budgetIndex = handleCount;
            if (budgetDeadline) handles[handleCount++] = budgetDeadline;
            const DWORD waited = WaitForMultipleObjects(handleCount, handles, FALSE,
                static_cast<DWORD>(remainingMs));
            if (waited == WAIT_FAILED) return Failed(GetLastError());
            if (cancellation && waited == WAIT_OBJECT_0) return WAIT_OBJECT_0 + 1;
            if (waited == WAIT_TIMEOUT) return WAIT_TIMEOUT;
            if (budgetDeadline && waited == WAIT_OBJECT_0 + budgetIndex) return WAIT_TIMEOUT;
            if (waited != WAIT_OBJECT_0 + readyIndex) return Failed(ERROR_INVALID_HANDLE);
            // Recheck an early signal and block on another positive timer delay.
        }
    }
};
