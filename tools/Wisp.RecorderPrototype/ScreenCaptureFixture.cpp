#include "GameScreenCapture.h"
#include "HdrFrameConverter.h"
#include <d3d10_1.h>
#include <dwmapi.h>
#include <tlhelp32.h>
#include <winrt/base.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <thread>

#if !defined(WISP_SCREEN_CAPTURE_FIXTURE)
#error This diagnostic requires the isolated current-process-only capture build.
#endif

// Explicit diagnostic only: actual production duplication/conversion, generated
// owned-window patches, no encoder/audio/WGC/permissions/files. Readback copies
// only eight 4x4 patch interiors, never the whole desktop image. Foreground and
// point-occlusion fences cannot be atomic with a desktop change; a changed guard
// discards the result. Values never leave this process except scalar verdicts.
namespace
{
    constexpr DWORD MaximumMs = 15000;
    constexpr UINT Width = 1920, Height = 1080, Tile = 4, FramesPerArm = 12;
    constexpr std::array<BYTE, 8> Shades{ 0, 16, 32, 64, 128, 192, 224, 255 };
    struct Failure { const char* reason; HRESULT hr; };
    void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_FAIL }; }
    void Check(HRESULT value, const char* reason) { if (FAILED(value)) throw Failure{ reason, value }; }
    HRESULT LastError() noexcept
    { const DWORD value = GetLastError(); return HRESULT_FROM_WIN32(value ? value : ERROR_GEN_FAILURE); }
    void Win32(BOOL value, const char* reason) { if (!value) throw Failure{ reason, LastError() }; }
    void Remember(HRESULT& value, HRESULT next) noexcept { if (SUCCEEDED(value) && FAILED(next)) value = next; }
    bool SameRect(const RECT& a, const RECT& b) noexcept
    { return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom; }
    void AppsClosed()
    {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) throw Failure{ "process_guard_failed", LastError() };
        HRESULT hr = S_OK;
        PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
        BOOL more = Process32FirstW(snapshot, &entry);
        unsigned count = 0;
        while (more)
        {
            if (++count > 32768) { hr = E_FAIL; break; }
            if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"Wisp.exe") == 0 || _wcsicmp(entry.szExeFile, L"Wisp.Recorder.exe") == 0)
            { hr = E_ACCESSDENIED; break; }
            more = Process32NextW(snapshot, &entry);
        }
        if (SUCCEEDED(hr) && GetLastError() != ERROR_NO_MORE_FILES) hr = LastError();
        if (!CloseHandle(snapshot)) Remember(hr, LastError());
        Check(hr, "close_forza_and_wisp_first");
    }
    LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM wp, LPARAM lp)
    {
        if (message == WM_ERASEBKGND) return 1;
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint{}; const HDC dc = BeginPaint(window, &paint);
            if (dc)
            {
                RECT client{};
                if (GetClientRect(window, &client))
                {
                    for (UINT i = 0; i < Shades.size(); ++i)
                    {
                        RECT band{ client.right * static_cast<LONG>(i) / 8, 0,
                            client.right * static_cast<LONG>(i + 1) / 8, client.bottom };
                        const BYTE shade = Shades[i];
                        SetDCBrushColor(dc, RGB(shade, shade, shade));
                        FillRect(dc, &band, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
                    }
                    // A small owned animation forces real desktop updates; all
                    // numeric samples are far from this strip and its edges.
                    RECT strip{ 0, 0, client.right, 16 };
                    const BYTE shade = static_cast<BYTE>(((GetTickCount64() / 40) & 1) ? 20 : 40);
                    SetDCBrushColor(dc, RGB(shade, shade, shade));
                    FillRect(dc, &strip, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
                    if (GetForegroundWindow() != window)
                    {
                        RECT messageArea{ 24, client.bottom / 3, client.right - 24, client.bottom * 2 / 3 };
                        SetDCBrushColor(dc, RGB(12, 12, 12));
                        FillRect(dc, &messageArea, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
                        const int fontHeight = (std::clamp)(client.bottom / 40, 24L, 54L);
                        const HFONT font = CreateFontW(-fontHeight, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
                            DEFAULT_PITCH, L"Segoe UI");
                        const HGDIOBJ previousFont = font ? SelectObject(dc, font) : nullptr;
                        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(255, 255, 255));
                        DrawTextW(dc, L"Click this pattern to start the Wisp screen test.\nWaiting up to 8 seconds; capture has not started.",
                            -1, &messageArea, DT_CENTER | DT_WORDBREAK | DT_NOPREFIX);
                        if (font)
                        {
                            if (previousFont && previousFont != HGDI_ERROR) SelectObject(dc, previousFont);
                            DeleteObject(font);
                        }
                    }
                }
                EndPaint(window, &paint);
            }
            return 0;
        }
        return DefWindowProcW(window, message, wp, lp);
    }
    struct Windows
    {
        static constexpr wchar_t Name[] = L"Wisp.OwnScreenCaptureFixture";
        HINSTANCE instance = GetModuleHandleW(nullptr);
        HWND original = nullptr, target = nullptr, other = nullptr;
        ATOM atom = 0;
        RECT bounds{};
        LONGLONG ownedFrameMinimum100ns = 0;
        ULONGLONG began = GetTickCount64(), nextProcessCheck = 0, nextPaint = 0;
        HRESULT cleanup = S_OK;
        bool restoreEligible = false, restored = false;
        bool startupShowHidden = false, visibleAfterFirstShow = false, visibleAfterExplicitShow = false;
        bool foregroundInitiallyGranted = false, activationGateUsed = false;
        ULONGLONG activationWaitMs = 0;
        ~Windows() { Close(); }
        void Create()
        {
            original = GetForegroundWindow(); Require(original != nullptr, "initial_foreground_missing");
            const HMONITOR monitor = MonitorFromWindow(original, MONITOR_DEFAULTTONULL);
            MONITORINFO info{}; info.cbSize = sizeof(info);
            Win32(GetMonitorInfoW(monitor, &info), "monitor_query_failed"); bounds = info.rcMonitor;
            WNDCLASSEXW cls{}; cls.cbSize = sizeof(cls); cls.hInstance = instance;
            cls.lpfnWndProc = Procedure; cls.lpszClassName = Name;
            atom = RegisterClassExW(&cls); Require(atom != 0, "class_registration_failed");
            target = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, Name, L"Wisp screen capture diagnostic",
                WS_POPUP, bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top,
                nullptr, nullptr, instance, nullptr);
            Require(target != nullptr, "target_window_failed");
            other = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, Name, L"Wisp owned focus test", WS_POPUP,
                bounds.left + 32, bounds.top + 32, 160, 90, nullptr, nullptr, instance, nullptr);
            Require(other != nullptr, "focus_window_failed");
            STARTUPINFOW startup{}; startup.cb = sizeof(startup); GetStartupInfoW(&startup);
            startupShowHidden = (startup.dwFlags & STARTF_USESHOWWINDOW) != 0 && startup.wShowWindow == SW_HIDE;
            // The first ShowWindow can honor the launcher's SW_HIDE instead of
            // this argument. A subsequent explicit show overrides that startup
            // state without bypassing Windows foreground restrictions.
            ShowWindow(target, SW_SHOW);
            visibleAfterFirstShow = IsWindowVisible(target) != FALSE;
            ShowWindow(target, SW_SHOWNOACTIVATE);
            visibleAfterExplicitShow = IsWindowVisible(target) != FALSE;
            Require(visibleAfterExplicitShow, "owned_window_hidden_after_explicit_show");
            if (GetForegroundWindow() != target) (void)SetForegroundWindow(target);
            foregroundInitiallyGranted = GetForegroundWindow() == target;
            if (!foregroundInitiallyGranted)
            {
                activationGateUsed = true;
                const auto waitBegan = GetTickCount64();
                auto nextCheck = waitBegan;
                while (GetForegroundWindow() != target)
                {
                    activationWaitMs = GetTickCount64() - waitBegan;
                    Require(activationWaitMs < 8000 && GetTickCount64() - began < MaximumMs - 4000,
                        "owned_activation_wait_expired");
                    Require(IsWindow(target) && IsWindowVisible(target) && !IsIconic(target), "owned_window_hidden");
                    MSG message{};
                    for (UINT i = 0; i < 64 && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE); ++i)
                    { Require(message.message != WM_QUIT, "window_loop_stopped"); TranslateMessage(&message); DispatchMessageW(&message); }
                    if (GetTickCount64() >= nextCheck)
                    { AppsClosed(); Paint(); nextCheck = GetTickCount64() + 100; }
                    Sleep(10);
                }
                activationWaitMs = GetTickCount64() - waitBegan;
            }
            Guard(); Paint(); Check(DwmFlush(), "initial_paint_not_presented"); Guard();
            LARGE_INTEGER now{}, frequency{};
            Win32(QueryPerformanceCounter(&now) && QueryPerformanceFrequency(&frequency), "fixture_clock_failed");
            Require(now.QuadPart > 0 && frequency.QuadPart > 0 && recorder::capture::ScreenQpcTo100ns(
                static_cast<std::uint64_t>(now.QuadPart), static_cast<std::uint64_t>(frequency.QuadPart),
                ownedFrameMinimum100ns), "fixture_clock_invalid");
        }
        void Guard()
        {
            Require(GetTickCount64() - began < MaximumMs - 1000, "fixture_deadline");
            MSG message{};
            for (UINT i = 0; i < 64 && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE); ++i)
            { Require(message.message != WM_QUIT, "window_loop_stopped"); TranslateMessage(&message); DispatchMessageW(&message); }
            Require(GetForegroundWindow() == target, "owned_foreground_lost");
            Require(IsWindowVisible(target) && !IsIconic(target), "owned_window_hidden");
            DWORD pid = 0; GetWindowThreadProcessId(target, &pid);
            Require(pid == GetCurrentProcessId(), "owned_identity_changed");
            RECT actual{}; Win32(GetWindowRect(target, &actual), "owned_bounds_failed");
            Require(SameRect(actual, bounds), "owned_bounds_changed");
            if (GetTickCount64() >= nextProcessCheck) { AppsClosed(); nextProcessCheck = GetTickCount64() + 100; }
            if (GetTickCount64() >= nextPaint) { Paint(); nextPaint = GetTickCount64() + 40; }
        }
        void Paint()
        {
            Win32(RedrawWindow(target, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW | RDW_NOCHILDREN), "paint_failed");
            Win32(GdiFlush(), "paint_flush_failed");
        }
        void SampleGuard()
        {
            Guard();
            const LONG width = bounds.right - bounds.left, height = bounds.bottom - bounds.top;
            // Bound the whole sampling footprint and bilinear/chroma neighbors.
            for (LONG i = 0; i < 8; ++i)
                for (LONG dy : { -16L, 0L, 16L }) for (LONG dx : { -16L, 0L, 16L })
                    Require(WindowFromPoint({ bounds.left + width * (2 * i + 1) / 16 + dx,
                        bounds.top + height / 2 + dy }) == target, "owned_patch_occluded");
        }
        void Close() noexcept
        {
            if (!target && !other && !atom) return;
            const HWND current = GetForegroundWindow();
            restoreEligible = current == target || current == other;
            if (restoreEligible && original && IsWindow(original))
            {
                if (!SetForegroundWindow(original)) Remember(cleanup, E_FAIL);
                restored = GetForegroundWindow() == original;
                if (!restored) Remember(cleanup, E_FAIL);
            }
            if (other) { if (DestroyWindow(other)) other = nullptr; else Remember(cleanup, LastError()); }
            if (target) { if (DestroyWindow(target)) target = nullptr; else Remember(cleanup, LastError()); }
            if (atom && !target && !other)
            { if (UnregisterClassW(Name, instance)) atom = 0; else Remember(cleanup, LastError()); }
        }
    };
    struct Lock
    {
        ID3D10Multithread* object;
        explicit Lock(ID3D10Multithread* value) : object(value) { object->Enter(); }
        ~Lock() { object->Leave(); }
    };
    struct Converted final : recorder::capture::FrameConsumer
    {
        recorder::hdr::HdrFrameConverter converter;
        recorder::hdr::Evidence evidence;
        winrt::com_ptr<ID3D11DeviceContext> context;
        std::array<winrt::com_ptr<ID3D11Texture2D>, 8> tiles;
        bool sample = false;
        HRESULT Submit(ID3D11Texture2D* source, ID3D11Texture2D* output) noexcept override
        {
            if (!converter.Submit(source, output, evidence)) return evidence.hr;
            if (sample)
            {
                for (UINT i = 0; i < tiles.size(); ++i)
                {
                    const UINT x = Width * (2 * i + 1) / 16;
                    const D3D11_BOX box{ x, Height / 2, 0, x + Tile, Height / 2 + Tile, 1 };
                    context->CopySubresourceRegion(tiles[i].get(), 0, 0, 0, 0, output, 0, &box);
                }
            }
            return S_OK;
        }
    };
    double ExpectedLuma(BYTE shade, bool hdr)
    {
        const double code = static_cast<double>(shade) / 255;
        double linear = code <= .04045 ? code / 12.92 : std::pow((code + .055) / 1.055, 2.4);
        if (hdr && linear > .75) linear = 1.0 - .0625 / (linear - .5);
        const double transferred = linear < .018 ? 4.5 * linear : 1.099 * std::pow(linear, .45) - .099;
        return 16.0 + 219.0 * transferred;
    }
    struct Arm
    {
        UINT format = 0, encoding = 0, submitted = 0, sampled = 0, maxCodeError = 0;
        UINT whiteMilliNits = 0;
        std::uint64_t copied = 0, repeated = 0, elapsedMs = 0;
        bool colorPassed = false, focusRejected = false, hdr = false;
        HRESULT cleanup = S_OK;
    };
    void RunArm(Windows& windows, bool testFocus, Arm& result)
    {
        recorder::capture::GameScreenCapture capture;
        const auto began = GetTickCount64();
        try
        {
            windows.SampleGuard();
            FILETIME creation{}, exit{}, kernel{}, user{};
            Win32(GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user), "self_identity_failed");
            const std::uint64_t born = (static_cast<std::uint64_t>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
            if (!capture.Initialize({ windows.target, GetCurrentProcessId(), born }, { 60, false }))
                throw Failure{ capture.Result().reason, capture.Result().hr };
            Require(capture.Result().copiedFrames == 0, "capture_before_start");
            const auto source = capture.Source();
            result.format = static_cast<UINT>(source.format); result.encoding = static_cast<UINT>(source.encoding);
            result.hdr = source.hdr; result.whiteMilliNits = static_cast<UINT>(source.referenceWhiteNits * 1000.0f);
            Converted converted;
            capture.Device()->GetImmediateContext(converted.context.put());
            winrt::com_ptr<ID3D11Device> device; device.copy_from(capture.Device());
            const auto mt = device.as<ID3D10Multithread>();
            const auto encoding = source.encoding == recorder::capture::SourceEncoding::LinearScRgbFp16
                ? recorder::hdr::SourceEncoding::LinearScRgbFp16 : recorder::hdr::SourceEncoding::SrgbBgra8;
            Require(converted.converter.Initialize(capture.Device(), source.width, source.height, encoding,
                source.hdr ? source.referenceWhiteNits : 0.0f, { Width, Height, 60, 1, 1 }, converted.evidence),
                "production_conversion_initialize_failed");
            D3D11_TEXTURE2D_DESC description{};
            description.Width = Width; description.Height = Height; description.MipLevels = 1; description.ArraySize = 1;
            description.Format = DXGI_FORMAT_NV12; description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT; description.BindFlags = D3D11_BIND_RENDER_TARGET;
            winrt::com_ptr<ID3D11Texture2D> output;
            Check(capture.Device()->CreateTexture2D(&description, nullptr, output.put()), "nv12_output_failed");
            description.Width = Tile; description.Height = Tile;
            description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0; description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            for (auto& tile : converted.tiles)
                Check(capture.Device()->CreateTexture2D(&description, nullptr, tile.put()), "tile_readback_failed");
            winrt::com_ptr<ID3D11Query> query;
            const D3D11_QUERY_DESC queryDescription{ D3D11_QUERY_EVENT, 0 };
            Check(capture.Device()->CreateQuery(&queryDescription, query.put()), "completion_query_failed");
            Require(capture.Start(), "production_capture_start_failed");
            std::uint64_t version = 0;
            LONGLONG previousTimestamp = 0;
            ULONGLONG nextSubmit = GetTickCount64() + 200;
            while (result.submitted < FramesPerArm)
            {
                windows.SampleGuard();
                if (!capture.CheckTarget()) throw Failure{ capture.Result().reason, capture.Result().hr };
                if (!capture.Result().copiedFrames || GetTickCount64() < nextSubmit) { Sleep(2); continue; }
                converted.sample = result.submitted % 4 == 3;
                recorder::capture::FrameInfo frame;
                {
                    Lock lock(mt.get());
                    Check(capture.SubmitLatestLocked(output.get(), converted, frame), "production_submit_failed");
                    if (converted.sample) { converted.context->End(query.get()); converted.context->Flush(); }
                }
                windows.SampleGuard();
                Require(frame.version > 0 && frame.timestamp100ns > 0 && frame.rawTimestamp100ns > 0 &&
                    frame.receivedQpc > 0 && frame.timestamp100ns >= previousTimestamp, "frame_metadata_invalid");
                // Never map a desktop image predating our opaque fullscreen cover.
                Require(frame.rawTimestamp100ns >= windows.ownedFrameMinimum100ns, "frame_predates_owned_window");
                if (frame.version == version) ++result.repeated;
                version = frame.version; previousTimestamp = frame.timestamp100ns;
                ++result.submitted;
                if (converted.sample)
                {
                    const auto waitBegan = GetTickCount64();
                    for (;;)
                    {
                        windows.SampleGuard();
                        BOOL done = FALSE;
                        const HRESULT status = converted.context->GetData(query.get(), &done, sizeof(done), D3D11_ASYNC_GETDATA_DONOTFLUSH);
                        Check(status, "gpu_completion_failed");
                        if (status == S_OK && done) break;
                        Require(GetTickCount64() - waitBegan < 1000, "gpu_completion_timeout"); Sleep(2);
                    }
                    for (UINT i = 0; i < converted.tiles.size(); ++i)
                    {
                        windows.SampleGuard();
                        D3D11_MAPPED_SUBRESOURCE mapped{};
                        Check(converted.context->Map(converted.tiles[i].get(), 0, D3D11_MAP_READ,
                            D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped), "patch_map_failed");
                        UINT error = 0;
                        const bool valid = mapped.pData && mapped.RowPitch >= Tile && mapped.RowPitch <= 65536;
                        if (valid)
                        {
                            const auto* bytes = static_cast<const BYTE*>(mapped.pData);
                            const int expected = static_cast<int>(std::lround(ExpectedLuma(Shades[i], source.hdr)));
                            for (UINT y = 0; y < Tile; ++y) for (UINT x = 0; x < Tile; ++x)
                                error = (std::max)(error, static_cast<UINT>(std::abs(static_cast<int>(bytes[y * mapped.RowPitch + x]) - expected)));
                            for (UINT y = 0; y < Tile / 2; ++y) for (UINT x = 0; x < Tile; ++x)
                                error = (std::max)(error, static_cast<UINT>(std::abs(static_cast<int>(bytes[(Tile + y) * mapped.RowPitch + x]) - 128)));
                        }
                        converted.context->Unmap(converted.tiles[i].get(), 0);
                        windows.SampleGuard();
                        Require(valid, "patch_layout_invalid");
                        result.maxCodeError = (std::max)(result.maxCodeError, error);
                    }
                    ++result.sampled;
                }
                // Requested 60 Hz submissions with no burst catch-up. This is
                // not an encoding/CFR or production performance measurement.
                nextSubmit = GetTickCount64() + 17;
            }
            result.copied = capture.Result().copiedFrames;
            result.colorPassed = result.sampled == 3 && result.maxCodeError <= 3;
            if (testFocus)
            {
                windows.Guard(); ShowWindow(windows.other, SW_SHOW);
                Win32(SetForegroundWindow(windows.other), "owned_focus_transition_failed");
                Require(GetForegroundWindow() == windows.other, "owned_focus_transition_failed");
                const bool accepted = capture.CheckTarget();
                result.focusRejected = !accepted && std::strcmp(capture.Result().reason, "target_focus_lost") == 0 &&
                    capture.Result().copiedFrames == result.copied;
                Require(result.focusRejected, "focus_loss_not_rejected");
                Check(capture.Close(), "capture_close_failed");
                Require(GetForegroundWindow() == windows.other, "foreground_changed_during_owned_transition");
                ShowWindow(windows.other, SW_HIDE);
                Win32(SetForegroundWindow(windows.target), "owned_refocus_failed"); windows.Guard();
            }
            Check(capture.Close(), "capture_close_failed");
            result.cleanup = capture.Result().cleanupHr;
        }
        catch (...) { result.cleanup = capture.Close(); throw; }
        result.elapsedMs = GetTickCount64() - began;
    }
    void PrintArm(const char* name, const Arm& a)
    {
        std::printf("\"%s\":{\"format\":%u,\"encoding\":%u,\"hdr\":%s,\"referenceWhiteMilliNits\":%u,"
            "\"submitted\":%u,\"copied\":%llu,\"repeated\":%llu,\"samples\":%u,\"maxCodeError\":%u,"
            "\"colorPassed\":%s,\"focusRejected\":%s,\"elapsedMs\":%llu,\"cleanupHr\":\"0x%08lX\"}",
            name, a.format, a.encoding, a.hdr ? "true" : "false", a.whiteMilliNits, a.submitted,
            static_cast<unsigned long long>(a.copied), static_cast<unsigned long long>(a.repeated), a.sampled,
            a.maxCodeError, a.colorPassed ? "true" : "false", a.focusRejected ? "true" : "false",
            static_cast<unsigned long long>(a.elapsedMs), static_cast<unsigned long>(a.cleanup));
    }
    int Run()
    {
        HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr); if (!done) return 2;
        std::thread watchdog;
        try { watchdog = std::thread([done] { if (WaitForSingleObject(done, MaximumMs) != WAIT_OBJECT_0)
            (void)TerminateProcess(GetCurrentProcess(), 124); }); }
        catch (...) { CloseHandle(done); return 2; }
        Windows windows;
        Arm first, reacquired;
        HRESULT hr = S_OK, cleanup = S_OK;
        const char* reason = "not_started";
        bool initialized = false, complete = false;
        DPI_AWARENESS_CONTEXT previousDpi = nullptr;
        try
        {
            AppsClosed();
            Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "mta_initialize_failed"); initialized = true;
            previousDpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            Require(previousDpi != nullptr, "thread_dpi_failed");
            windows.Create();
            RunArm(windows, true, first);
            RunArm(windows, false, reacquired);
            windows.Guard(); AppsClosed();
            complete = true;
            reason = first.colorPassed && reacquired.colorPassed ? "own_screen_capture_and_color_passed" : "own_screen_color_mismatch";
        }
        catch (const Failure& error) { reason = error.reason; hr = error.hr; }
        catch (const winrt::hresult_error& error) { reason = "winrt_operation_failed"; hr = error.code(); }
        catch (...) { reason = "fixture_failed"; hr = E_FAIL; }
        windows.Close(); Remember(cleanup, windows.cleanup);
        Remember(cleanup, first.cleanup); Remember(cleanup, reacquired.cleanup);
        if (previousDpi && !SetThreadDpiAwarenessContext(previousDpi)) Remember(cleanup, LastError());
        if (initialized) CoUninitialize();
        try { AppsClosed(); } catch (const Failure& error) { Remember(cleanup, error.hr); } catch (...) { Remember(cleanup, E_FAIL); }
        const bool passed = complete && SUCCEEDED(hr) && SUCCEEDED(cleanup) && first.colorPassed && reacquired.colorPassed &&
            first.focusRejected && windows.restored && !windows.target && !windows.other && !windows.atom;
        std::printf("{\"mode\":\"own_screen_capture\",\"completed\":%s,\"passed\":%s,\"reason\":\"%s\","
            "\"hr\":\"0x%08lX\",\"cleanupHr\":\"0x%08lX\",\"foregroundRestored\":%s,"
            "\"startupShowHidden\":%s,\"visibleAfterFirstShow\":%s,\"visibleAfterExplicitShow\":%s,"
            "\"foregroundInitiallyGranted\":%s,\"activationGateUsed\":%s,\"activationWaitMs\":%llu,"
            "\"wgcUsed\":false,\"permissionRequested\":false,\"gameCaptured\":false,\"pixelsRetained\":false,",
            complete ? "true" : "false", passed ? "true" : "false", reason,
            static_cast<unsigned long>(hr), static_cast<unsigned long>(cleanup), windows.restored ? "true" : "false",
            windows.startupShowHidden ? "true" : "false", windows.visibleAfterFirstShow ? "true" : "false",
            windows.visibleAfterExplicitShow ? "true" : "false", windows.foregroundInitiallyGranted ? "true" : "false",
            windows.activationGateUsed ? "true" : "false", static_cast<unsigned long long>(windows.activationWaitMs));
        PrintArm("initial", first); std::printf(","); PrintArm("reacquired", reacquired);
        std::printf(",\"elapsedMs\":%llu,\"limits\":\"Owned SDR patch mapping through the active desktop color mode and production converter only; no encoder, HDR content, border visual classifier, CFR or performance proof.\"}\n",
            static_cast<unsigned long long>(GetTickCount64() - windows.began));
        std::fflush(stdout);
        if (!SetEvent(done)) (void)TerminateProcess(GetCurrentProcess(), 125);
        watchdog.join(); if (!CloseHandle(done)) return 2;
        return passed ? 0 : 1;
    }
}
int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && std::wcscmp(argv[1], L"--own-screen-check") == 0) return Run();
    if (argc == 2 && std::wcscmp(argv[1], L"--self-test") == 0)
    {
        const UINT count = recorder::capture::RunScreenCaptureContracts();
        std::printf("{\"mode\":\"screen_capture_cpu_contracts\",\"passed\":%u,\"captureUsed\":false}\n", count);
        return count == 19 ? 0 : 1;
    }
    std::puts("Screen capture diagnostic. Explicit --own-screen-check required. Default/help creates no graphics, capture, windows or COM resources.");
    return argc == 1 || (argc == 2 && std::wcscmp(argv[1], L"--help") == 0) ? 0 : 2;
}
