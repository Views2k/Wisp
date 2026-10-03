#include "GameScreenCapture.h"
#include "ConversionOutput.h"

#include <d3d10_1.h>
#include <dxgi1_6.h>
#include <dwmapi.h>
#include <winrt/base.h>
#include <intrin.h>
#include <array>
#include <chrono>
#include <cwchar>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

namespace recorder::capture
{
    bool IsScreenPauseReason(const char* reason) noexcept
    {
        return reason && (std::strcmp(reason, "target_focus_lost") == 0 ||
            std::strcmp(reason, "target_window_minimized") == 0 || std::strcmp(reason, "target_not_fullscreen") == 0);
    }
    SourceEncoding ScreenSourceEncoding(DXGI_COLOR_SPACE_TYPE color, DXGI_FORMAT format) noexcept
    {
        if (color == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 && format == DXGI_FORMAT_B8G8R8A8_UNORM)
            return SourceEncoding::SrgbBgra8;
        if (color == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 && format == DXGI_FORMAT_R16G16B16A16_FLOAT)
            return SourceEncoding::LinearScRgbFp16;
        return SourceEncoding::Unknown;
    }
    bool ScreenFullscreenBounds(const RECT& client, const RECT& monitor) noexcept
    {
        return monitor.right > monitor.left && monitor.bottom > monitor.top &&
            client.left == monitor.left && client.top == monitor.top &&
            client.right == monitor.right && client.bottom == monitor.bottom;
    }
    bool ScreenQpcTo100ns(std::uint64_t ticks, std::uint64_t frequency, LONGLONG& value) noexcept
    {
        value = 0;
        constexpr auto maximum = static_cast<std::uint64_t>((std::numeric_limits<LONGLONG>::max)());
        if (!ticks || !frequency || frequency > maximum || ticks > maximum || ticks / frequency > maximum / 10000000)
            return false;
        const auto whole = (ticks / frequency) * 10000000;
        unsigned __int64 high = 0, remainder = 0;
        const auto low = _umul128(ticks % frequency, 10000000, &high);
        const auto fraction = _udiv128(high, low, frequency, &remainder);
        if (whole > maximum - fraction || whole + fraction == 0) return false;
        value = static_cast<LONGLONG>(whole + fraction);
        return true;
    }
    namespace
    {
        using Clock = std::chrono::steady_clock;
        struct Failure { const char* reason; HRESULT hr; };
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_INVALIDARG }; }
        void CheckWin32(BOOL value, const char* reason)
        { if (!value) { const auto error = GetLastError(); throw Failure{ reason, HRESULT_FROM_WIN32(error ? error : ERROR_GEN_FAILURE) }; } }
        void CheckDisplay(LONG code, const char* reason)
        { if (code != ERROR_SUCCESS) throw Failure{ reason, HRESULT_FROM_WIN32(code) }; }
        bool SameLuid(LUID a, LUID b) noexcept { return a.LowPart == b.LowPart && a.HighPart == b.HighPart; }
        bool SameRect(const RECT& a, const RECT& b) noexcept
        { return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom; }
        struct GraphicsLock
        {
            ID3D10Multithread* value;
            explicit GraphicsLock(ID3D10Multithread* item) : value(item) { value->Enter(); }
            ~GraphicsLock() { value->Leave(); }
        };
        struct Display
        {
            winrt::com_ptr<IDXGIFactory1> factory;
            winrt::com_ptr<IDXGIAdapter1> adapter;
            winrt::com_ptr<IDXGIOutput6> output;
            LUID luid{};
            DXGI_COLOR_SPACE_TYPE color = DXGI_COLOR_SPACE_CUSTOM;
            UINT whiteLevel = 0;
        };

        UINT ReadWhiteLevel(HMONITOR monitor, LUID expectedAdapter)
        {
            MONITORINFOEXW monitorInfo{};
            monitorInfo.cbSize = sizeof(monitorInfo);
            CheckWin32(GetMonitorInfoW(monitor, &monitorInfo), "monitor_description_failed");
            std::vector<DISPLAYCONFIG_PATH_INFO> paths;
            std::vector<DISPLAYCONFIG_MODE_INFO> modes;
            bool queried = false;
            for (UINT attempt = 0; attempt < 3; ++attempt)
            {
                UINT pathCount = 0, modeCount = 0;
                CheckDisplay(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount),
                    "display_path_size_failed");
                Require(pathCount > 0 && pathCount <= 256 && modeCount <= 2048, "display_path_count_unsupported");
                paths.resize(pathCount); modes.resize(modeCount);
                const LONG result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(),
                    &modeCount, modes.data(), nullptr);
                if (result == ERROR_INSUFFICIENT_BUFFER) continue;
                CheckDisplay(result, "display_path_query_failed");
                paths.resize(pathCount);
                queried = true;
                break;
            }
            Require(queried, "display_configuration_unstable");
            bool found = false;
            UINT level = 0;
            for (const auto& path : paths)
            {
                if (!SameLuid(path.sourceInfo.adapterId, expectedAdapter)) continue;
                DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = sizeof(source);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                CheckDisplay(DisplayConfigGetDeviceInfo(&source.header), "display_source_name_failed");
                if (_wcsicmp(source.viewGdiDeviceName, monitorInfo.szDevice) != 0) continue;
                // Cloned output paths have no single monitor white level.
                Require(!found, "display_white_target_ambiguous");
                DISPLAYCONFIG_SDR_WHITE_LEVEL white{};
                white.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                white.header.size = sizeof(white);
                white.header.adapterId = path.targetInfo.adapterId;
                white.header.id = path.targetInfo.id;
                CheckDisplay(DisplayConfigGetDeviceInfo(&white.header), "display_white_query_failed");
                level = white.SDRWhiteLevel;
                // Same explicit range as the HDR converter, not a fallback.
                Require(level >= 125 && level <= 12500, "display_white_out_of_bounds");
                found = true;
            }
            Require(found, "display_white_target_missing");
            return level;
        }

        Display SelectDisplay(HMONITOR monitor)
        {
            Display result;
            Check(CreateDXGIFactory1(__uuidof(IDXGIFactory1), result.factory.put_void()), "dxgi_factory_failed");
            for (UINT a = 0; a < 32; ++a)
            {
                winrt::com_ptr<IDXGIAdapter1> adapter;
                const HRESULT adapterHr = result.factory->EnumAdapters1(a, adapter.put());
                if (adapterHr == DXGI_ERROR_NOT_FOUND) break;
                Check(adapterHr, "adapter_enumeration_failed");
                DXGI_ADAPTER_DESC1 description{};
                Check(adapter->GetDesc1(&description), "adapter_description_failed");
                if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                for (UINT o = 0; o < 32; ++o)
                {
                    winrt::com_ptr<IDXGIOutput> output;
                    const HRESULT outputHr = adapter->EnumOutputs(o, output.put());
                    if (outputHr == DXGI_ERROR_NOT_FOUND) break;
                    Check(outputHr, "output_enumeration_failed");
                    DXGI_OUTPUT_DESC description2{};
                    Check(output->GetDesc(&description2), "output_description_failed");
                    if (description2.Monitor != monitor) continue;
                    result.output = output.try_as<IDXGIOutput6>();
                    Require(result.output != nullptr, "display_color_information_unavailable");
                    DXGI_OUTPUT_DESC1 color{};
                    Check(result.output->GetDesc1(&color), "display_color_query_failed");
                    Require(color.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ||
                        color.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, "display_color_unsupported");
                    result.adapter = adapter;
                    result.luid = description.AdapterLuid;
                    result.color = color.ColorSpace;
                    if (result.color == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)
                    {
                        // Diagnostic metadata only. HDR exposure is determined
                        // by absolute scRGB units, so an unavailable SDR-window
                        // white query must not prevent gameplay recording.
                        try { result.whiteLevel = ReadWhiteLevel(monitor, result.luid); }
                        catch (const Failure&) { result.whiteLevel = 0; }
                    }
                    return result;
                }
            }
            throw Failure{ "hardware_display_adapter_missing", E_FAIL };
        }

        RECT PhysicalClient(HWND window)
        {
            RECT client{};
            CheckWin32(GetClientRect(window, &client), "target_size_query_failed");
            POINT first{ client.left, client.top }, last{ client.right, client.bottom };
            CheckWin32(ClientToScreen(window, &first), "target_bounds_query_failed");
            CheckWin32(ClientToScreen(window, &last), "target_bounds_query_failed");
            return { first.x, first.y, last.x, last.y };
        }
        const char* DuplicationFailure(HRESULT hr) noexcept
        {
            switch (hr)
            {
            case DXGI_ERROR_ACCESS_LOST: return "duplication_access_lost";
            case DXGI_ERROR_SESSION_DISCONNECTED: return "duplication_session_disconnected";
            case DXGI_ERROR_NOT_CURRENTLY_AVAILABLE: return "duplication_busy";
            case E_ACCESSDENIED: return "duplication_desktop_unavailable";
            case DXGI_ERROR_UNSUPPORTED: return "duplication_mode_unsupported";
            default: return "duplication_failed";
            }
        }
        HRESULT RecordRelease(HRESULT hr, Evidence& evidence, bool cleanup) noexcept
        {
            if (hr != DXGI_ERROR_ACCESS_LOST) return hr;
            evidence.duplicationInvalidated = true;
            // AccessLost invalidates this interface; Close still releases the
            // COM object. It is a media-recovery cause, not unclosed ownership.
            return cleanup ? S_OK : hr;
        }
    }

    struct GameScreenCapture::Impl
    {
        DWORD ownerThread = GetCurrentThreadId();
        HANDLE process = nullptr;
        DPI_AWARENESS_CONTEXT previousDpi = nullptr;
        TargetIdentity target{};
        Options options{};
        HMONITOR monitor = nullptr;
        RECT monitorRect{};
        Display display;
        DXGI_OUTPUT_DESC1 outputDescription{};
        DXGI_OUTDUPL_DESC duplicationDescription{};
        winrt::com_ptr<ID3D11Device> device;
        winrt::com_ptr<ID3D11DeviceContext> context;
        winrt::com_ptr<ID3D10Multithread> multithread;
        winrt::com_ptr<IDXGIOutputDuplication> duplication;
        winrt::com_ptr<ID3D11Texture2D> latest;
        bool frameHeld = false;
        bool latestAvailable = false;
        FrameInfo frame{};
        std::uint64_t frequency = 0;
        LONGLONG resumedAfter100ns = 0;
        Clock::time_point nextAcquire = Clock::time_point::min();
        Clock::time_point lastDisplayCheck = Clock::time_point::min();
        std::chrono::nanoseconds period{};

        void ValidateIdentity() const
        {
            Require(ownerThread == GetCurrentThreadId(), "wrong_capture_worker");
            Require(WaitForSingleObject(process, 0) == WAIT_TIMEOUT, "target_process_exited");
            DWORD processId = 0;
            Require(GetWindowThreadProcessId(target.window, &processId) != 0 && IsWindow(target.window) &&
                processId == target.processId && GetAncestor(target.window, GA_ROOT) == target.window,
                "target_window_identity_changed");
        }
        void ValidateBasic() const
        {
            ValidateIdentity();
            Require(IsIconic(target.window) == FALSE, "target_window_minimized");
            Require(GetForegroundWindow() == target.window, "target_focus_lost");
            Require(IsWindowVisible(target.window) != FALSE, "target_window_hidden");
            DWORD cloaked = 0;
            Check(DwmGetWindowAttribute(target.window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked)), "target_cloak_query_failed");
            Require(cloaked == 0, "target_window_hidden");
            Require(MonitorFromWindow(target.window, MONITOR_DEFAULTTONULL) == monitor, "target_monitor_changed");
            MONITORINFO description{}; description.cbSize = sizeof(description);
            CheckWin32(GetMonitorInfoW(monitor, &description), "monitor_description_failed");
            Require(SameRect(description.rcMonitor, monitorRect), "display_configuration_changed");
            Require(ScreenFullscreenBounds(PhysicalClient(target.window), monitorRect), "target_not_fullscreen");
            if (device) Check(device->GetDeviceRemovedReason(), "capture_device_removed");
        }
        void ValidateDisplay(bool queryColor)
        {
            Require(display.factory->IsCurrent() != FALSE, "display_configuration_changed");
            DXGI_OUTDUPL_DESC mode{};
            duplication->GetDesc(&mode);
            Require(mode.ModeDesc.Width == duplicationDescription.ModeDesc.Width &&
                mode.ModeDesc.Height == duplicationDescription.ModeDesc.Height &&
                mode.ModeDesc.Format == duplicationDescription.ModeDesc.Format &&
                mode.ModeDesc.RefreshRate.Numerator == duplicationDescription.ModeDesc.RefreshRate.Numerator &&
                mode.ModeDesc.RefreshRate.Denominator == duplicationDescription.ModeDesc.RefreshRate.Denominator &&
                mode.ModeDesc.ScanlineOrdering == duplicationDescription.ModeDesc.ScanlineOrdering &&
                mode.ModeDesc.Scaling == duplicationDescription.ModeDesc.Scaling &&
                mode.Rotation == duplicationDescription.Rotation, "duplication_mode_changed");
            const auto now = Clock::now();
            // GetDesc1 color queries retain the existing 250 ms
            // cadence. Factory/mode/actual-texture guards still bracket frames.
            if (!queryColor || (lastDisplayCheck != Clock::time_point::min() &&
                now - lastDisplayCheck < std::chrono::milliseconds(250))) return;
            DXGI_OUTPUT_DESC1 current{};
            Check(display.output->GetDesc1(&current), "display_color_query_failed");
            Require(current.AttachedToDesktop && current.Monitor == monitor &&
                SameRect(current.DesktopCoordinates, outputDescription.DesktopCoordinates), "display_configuration_changed");
            Require(current.Rotation == outputDescription.Rotation, "display_rotation_changed");
            Require(current.ColorSpace == display.color && current.BitsPerColor == outputDescription.BitsPerColor,
                "display_color_changed");
            // Windows SDR-content white does not change absolute HDR scRGB
            // units. Its adjustment must not discard the current clip buffer.
            lastDisplayCheck = now;
        }
        HRESULT Release() noexcept
        {
            if (!frameHeld) return S_OK;
            frameHeld = false;
            return duplication->ReleaseFrame();
        }
    };

    GameScreenCapture::GameScreenCapture() noexcept = default;
    GameScreenCapture::~GameScreenCapture() { (void)Close(); }
    bool GameScreenCapture::Fail(const char* reason, HRESULT hr) noexcept
    {
        // A desktop transition may invalidate duplication before the next
        // foreground guard. Preserve history only when focus loss is observed;
        // a focused access loss still follows normal capture recovery.
        if (evidence_.started && impl_ && hr == DXGI_ERROR_ACCESS_LOST &&
            WaitForSingleObject(impl_->process, 0) == WAIT_TIMEOUT && IsWindow(impl_->target.window) &&
            GetForegroundWindow() != impl_->target.window)
            reason = "target_focus_lost";
        if (evidence_.started && IsScreenPauseReason(reason) && Pause())
        { evidence_.reason = reason; evidence_.hr = S_FALSE; return false; }
        if (!evidence_.stopped) { evidence_.reason = reason; evidence_.hr = hr; evidence_.stopped = true; }
        return false;
    }

    bool GameScreenCapture::Initialize(const TargetIdentity& target, const Options& options) noexcept
    {
        if (impl_ || closed_ || evidence_.stopped) return Fail("capture_not_fresh", E_UNEXPECTED);
        try
        {
            Require(target.window && target.processId && target.creationTime &&
                (options.frameRate == 30 || options.frameRate == 60), "capture_options_invalid");
            APTTYPE apartment{}; APTTYPEQUALIFIER qualifier{};
            Check(CoGetApartmentType(&apartment, &qualifier), "capture_apartment_uninitialized");
            Require(apartment == APTTYPE_MTA, "capture_requires_mta_worker");
            impl_ = std::make_unique<Impl>();
            auto& value = *impl_;
            value.target = target; value.options = options;
            value.period = std::chrono::nanoseconds((1000000000ll + options.frameRate - 1) / options.frameRate);
            // This dedicated worker's DPI scope lasts until Close; it changes no
            // process-wide awareness or display settings. Bounds remain physical.
            value.previousDpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            CheckWin32(value.previousDpi != nullptr, "capture_dpi_context_failed");
            value.process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, target.processId);
            CheckWin32(value.process != nullptr, "target_process_open_failed");
            FILETIME creation{}, exit{}, kernel{}, user{};
            CheckWin32(GetProcessTimes(value.process, &creation, &exit, &kernel, &user), "target_process_time_failed");
            Require(((static_cast<std::uint64_t>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime) == target.creationTime,
                "target_process_identity_mismatch");
#if defined(WISP_SCREEN_CAPTURE_FIXTURE)
            // Isolated diagnostic binary only; never an arbitrary target bypass.
            Require(target.processId == GetCurrentProcessId(), "fixture_target_is_not_self");
#else
            std::array<wchar_t, 32768> path{};
            DWORD length = static_cast<DWORD>(path.size());
            CheckWin32(QueryFullProcessImageNameW(value.process, 0, path.data(), &length), "target_process_name_failed");
            const wchar_t* name = std::wcsrchr(path.data(), L'\\');
            Require(_wcsicmp(name ? name + 1 : path.data(), L"ForzaHorizon6.exe") == 0, "target_is_not_fh6");
#endif
            value.monitor = MonitorFromWindow(target.window, MONITOR_DEFAULTTONULL);
            Require(value.monitor != nullptr, "target_monitor_missing");
            MONITORINFO monitor{}; monitor.cbSize = sizeof(monitor);
            CheckWin32(GetMonitorInfoW(value.monitor, &monitor), "monitor_description_failed");
            value.monitorRect = monitor.rcMonitor;
            value.ValidateBasic();
            value.display = SelectDisplay(value.monitor);
            Check(value.display.output->GetDesc1(&value.outputDescription), "display_color_query_failed");
            Require(value.outputDescription.AttachedToDesktop && SameRect(value.outputDescription.DesktopCoordinates, value.monitorRect),
                "display_configuration_changed");
            Require(value.outputDescription.Rotation == DXGI_MODE_ROTATION_IDENTITY, "display_rotation_unsupported");
            const D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_0 };
            Check(D3D11CreateDevice(value.display.adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 1, D3D11_SDK_VERSION, value.device.put(), nullptr,
                value.context.put()), "capture_device_creation_failed");
            value.multithread = value.device.as<ID3D10Multithread>();
            (void)value.multithread->SetMultithreadProtected(TRUE);
            Require(value.multithread->GetMultithreadProtected() != FALSE, "capture_multithread_protection_failed");
            // Microsoft Desktop Duplication sample negotiates FP16 for HDR.
            // No PQ10 format is requested or treated as scRGB. Actual texture
            // format must match this negotiated description on every new frame.
            const DXGI_FORMAT formats[]{ DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM };
            const HRESULT duplicationHr = value.display.output->DuplicateOutput1(value.device.get(), 0,
                static_cast<UINT>(std::size(formats)), formats, value.duplication.put());
            Check(duplicationHr, DuplicationFailure(duplicationHr));
            value.duplication->GetDesc(&value.duplicationDescription);
            const auto& description = value.duplicationDescription;
            Require(description.Rotation == DXGI_MODE_ROTATION_IDENTITY, "display_rotation_unsupported");
            Require(description.ModeDesc.Width == static_cast<UINT>(value.monitorRect.right - value.monitorRect.left) &&
                description.ModeDesc.Height == static_cast<UINT>(value.monitorRect.bottom - value.monitorRect.top),
                "duplication_mode_changed");
            if (const auto* reason = conversion::ValidateSourceGeometry(description.ModeDesc.Width, description.ModeDesc.Height))
                throw Failure{ reason, E_INVALIDARG };
            source_.width = description.ModeDesc.Width; source_.height = description.ModeDesc.Height;
            source_.format = description.ModeDesc.Format; source_.outputColorSpace = value.display.color;
            source_.encoding = ScreenSourceEncoding(value.display.color, source_.format);
            Require(source_.encoding != SourceEncoding::Unknown, "duplication_format_unsupported");
            source_.hdr = source_.encoding == SourceEncoding::LinearScRgbFp16;
            source_.referenceWhiteQueried = source_.hdr && value.display.whiteLevel != 0;
            source_.referenceWhiteNits = source_.hdr ? static_cast<float>(value.display.whiteLevel) * 80.0f / 1000.0f : 0.0f;
            D3D11_TEXTURE2D_DESC texture{};
            texture.Width = source_.width; texture.Height = source_.height;
            texture.Format = source_.format; texture.ArraySize = 1; texture.MipLevels = 1;
            texture.SampleDesc.Count = 1; texture.Usage = D3D11_USAGE_DEFAULT;
            texture.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            Check(value.device->CreateTexture2D(&texture, nullptr, value.latest.put()), "capture_latest_texture_failed");
            LARGE_INTEGER frequency{};
            CheckWin32(QueryPerformanceFrequency(&frequency), "capture_clock_failed");
            Require(frequency.QuadPart > 0, "capture_clock_invalid");
            value.frequency = static_cast<std::uint64_t>(frequency.QuadPart);
            value.ValidateBasic(); value.ValidateDisplay(true);
            evidence_.initialized = true; evidence_.source = source_; evidence_.reason = "capture_initialized";
            return true;
        }
        catch (const Failure& error) { Fail(error.reason, error.hr); }
        catch (const winrt::hresult_error& error) { Fail("capture_initialization_failed", error.code()); }
        catch (const std::bad_alloc&) { Fail("capture_allocation_failed", E_OUTOFMEMORY); }
        catch (...) { Fail("capture_initialization_failed", E_FAIL); }
        (void)Close();
        return false;
    }

    bool GameScreenCapture::Start() noexcept
    {
        if (!impl_ || closed_ || !evidence_.initialized || evidence_.started || evidence_.stopped)
            return Fail("capture_not_startable", E_UNEXPECTED);
        if (!CheckTarget()) return false;
        evidence_.started = true; evidence_.reason = "capture_started";
        return true;
    }

    bool GameScreenCapture::CheckTarget() noexcept
    {
        if (!impl_ || closed_ || !evidence_.initialized) return Fail("capture_not_initialized", E_UNEXPECTED);
        if (evidence_.stopped) return false;
        if (paused_) return false;
        auto& value = *impl_;
        try
        {
            value.ValidateBasic();
            const auto now = Clock::now();
            if (!evidence_.started) { value.ValidateDisplay(true); return true; }
            if (now < value.nextAcquire) return true;
            value.nextAcquire = value.nextAcquire == Clock::time_point::min() ? now + value.period : value.nextAcquire + value.period;
            if (value.nextAcquire <= now) value.nextAcquire = now + value.period;
            value.ValidateDisplay(true);
            DXGI_OUTDUPL_FRAME_INFO frame{};
            winrt::com_ptr<IDXGIResource> resource;
            const HRESULT acquired = value.duplication->AcquireNextFrame(1, &frame, resource.put());
            if (acquired == DXGI_ERROR_WAIT_TIMEOUT)
            {
                Require(evidence_.emptyCallbacks != (std::numeric_limits<std::uint64_t>::max)(), "capture_counter_limit");
                ++evidence_.emptyCallbacks;
                value.ValidateBasic(); value.ValidateDisplay(false);
                return true;
            }
            if (acquired == DXGI_ERROR_ACCESS_LOST) evidence_.duplicationInvalidated = true;
            Check(acquired, DuplicationFailure(acquired));
            value.frameHeld = true;
            LARGE_INTEGER received{};
            CheckWin32(QueryPerformanceCounter(&received), "capture_receipt_clock_failed");
            Require(received.QuadPart > 0, "capture_receipt_clock_invalid");
            value.ValidateBasic(); value.ValidateDisplay(false);
            // Pointer-only notifications are not new video. They cannot create
            // the first A/V epoch or refresh the age of an unchanged picture.
            if (frame.LastPresentTime.QuadPart == 0)
            {
                const HRESULT released = RecordRelease(value.Release(), evidence_, false);
                Check(released, released == DXGI_ERROR_ACCESS_LOST ? "duplication_access_lost" : "duplication_release_failed");
                return true;
            }
            Require(frame.LastPresentTime.QuadPart > 0, "capture_timestamp_invalid");
            Require(resource != nullptr, "capture_surface_unavailable");
            auto surface = resource.as<ID3D11Texture2D>();
            D3D11_TEXTURE2D_DESC description{}; surface->GetDesc(&description);
            Require(description.Width == source_.width && description.Height == source_.height &&
                description.Format == source_.format && description.ArraySize == 1 && description.MipLevels == 1 &&
                description.SampleDesc.Count == 1 && description.SampleDesc.Quality == 0, "capture_surface_changed");
            winrt::com_ptr<ID3D11Device> surfaceDevice; surface->GetDevice(surfaceDevice.put());
            Require(surfaceDevice.as<IUnknown>() == value.device.as<IUnknown>(), "capture_surface_device_mismatch");
            LONGLONG raw = 0, normalized = 0; bool clamped = false;
            Require(ScreenQpcTo100ns(static_cast<std::uint64_t>(frame.LastPresentTime.QuadPart), value.frequency, raw) &&
                NormalizeFrameTimestamp(raw, value.frame.timestamp100ns, normalized, clamped), "capture_timestamp_invalid");
            if (value.resumedAfter100ns && raw < value.resumedAfter100ns)
            {
                const HRESULT released = RecordRelease(value.Release(), evidence_, false);
                Check(released, "duplication_release_failed");
                return true;
            }
            Require(value.frame.version != (std::numeric_limits<std::uint64_t>::max)() &&
                (!clamped || evidence_.timestampClamps != (std::numeric_limits<std::uint64_t>::max)()), "capture_counter_limit");
            {
                GraphicsLock lock(value.multithread.get());
                value.ValidateBasic();
                value.context->CopyResource(value.latest.get(), surface.get());
                value.ValidateBasic(); value.ValidateDisplay(false);
                Check(value.device->GetDeviceRemovedReason(), "capture_device_removed");
                value.frame.timestamp100ns = normalized; value.frame.rawTimestamp100ns = raw;
                value.frame.receivedQpc = static_cast<std::uint64_t>(received.QuadPart);
                ++value.frame.version;
                value.latestAvailable = true;
                if (clamped) ++evidence_.timestampClamps;
            }
            // Commands are ordered on this owned immediate context. Release the
            // borrowed duplication resource after submitting the GPU copy, not
            // after a CPU readback/wait. The latest texture is separately owned.
            surface = nullptr; resource = nullptr;
            const HRESULT released = RecordRelease(value.Release(), evidence_, false);
            Check(released, released == DXGI_ERROR_ACCESS_LOST ? "duplication_access_lost" : "duplication_release_failed");
            evidence_.copiedFrames = value.frame.version;
            return true;
        }
        catch (const Failure& error) { Fail(error.reason, error.hr); }
        catch (const winrt::hresult_error& error) { Fail("capture_frame_failed", error.code()); }
        catch (...) { Fail("capture_frame_failed", E_FAIL); }
        const HRESULT released = RecordRelease(value.Release(), evidence_, true);
        if (FAILED(released) && SUCCEEDED(evidence_.cleanupHr)) evidence_.cleanupHr = released;
        return false;
    }

    ID3D11Device* GameScreenCapture::Device() const noexcept
    { return impl_ && !closed_ ? impl_->device.get() : nullptr; }
    const SourceDescription& GameScreenCapture::Source() const noexcept { return source_; }
    HRESULT GameScreenCapture::SubmitLatestLocked(ID3D11Texture2D* destination, FrameConsumer& consumer, FrameInfo& info) noexcept
    {
        info = {};
        if (!impl_ || closed_ || !evidence_.started || impl_->ownerThread != GetCurrentThreadId()) return E_UNEXPECTED;
        if (evidence_.stopped) return FAILED(evidence_.hr) ? evidence_.hr : E_ABORT;
        if (paused_) return S_FALSE;
        if (!destination) return E_INVALIDARG;
        try
        {
            auto& value = *impl_;
            value.ValidateBasic(); value.ValidateDisplay(false);
            if (!value.latestAvailable) return S_FALSE;
            const HRESULT result = consumer.Submit(value.latest.get(), destination);
            Check(result, "capture_conversion_failed");
            value.ValidateBasic(); value.ValidateDisplay(false);
            info = value.frame;
            return result;
        }
        catch (const Failure& error) { Fail(error.reason, error.hr); }
        catch (const winrt::hresult_error& error) { Fail("capture_conversion_failed", error.code()); }
        catch (...) { Fail("capture_conversion_failed", E_FAIL); }
        return paused_ ? S_FALSE : evidence_.hr;
    }
    bool GameScreenCapture::Pause() noexcept
    {
        if (!impl_ || closed_ || evidence_.stopped || impl_->ownerThread != GetCurrentThreadId()) return false;
        if (paused_) return true;
        auto& value = *impl_;
        const HRESULT released = RecordRelease(value.Release(), evidence_, true);
        if (FAILED(released))
        {
            evidence_.cleanupHr = released; evidence_.hr = released;
            evidence_.reason = "duplication_release_failed"; evidence_.stopped = true;
            return false;
        }
        value.duplication = nullptr; value.latestAvailable = false; value.nextAcquire = Clock::time_point::min();
        evidence_.reason = "target_focus_lost"; evidence_.hr = S_FALSE;
        paused_ = true;
        return true;
    }
    bool GameScreenCapture::Resume(const TargetIdentity& target) noexcept
    {
        if (!impl_ || closed_ || evidence_.stopped || !paused_ || !evidence_.started) return false;
        auto& value = *impl_;
        try
        {
            Require(target.window == value.target.window && target.processId == value.target.processId &&
                target.creationTime == value.target.creationTime, "target_window_identity_changed");
            value.ValidateBasic();
            const DXGI_FORMAT formats[]{ DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM };
            const HRESULT duplicated = value.display.output->DuplicateOutput1(value.device.get(), 0,
                static_cast<UINT>(std::size(formats)), formats, value.duplication.put());
            Check(duplicated, DuplicationFailure(duplicated));
            value.lastDisplayCheck = Clock::time_point::min();
            value.ValidateDisplay(true); value.ValidateBasic();
            LARGE_INTEGER now{};
            CheckWin32(QueryPerformanceCounter(&now), "capture_clock_failed");
            Require(now.QuadPart > 0 && ScreenQpcTo100ns(static_cast<std::uint64_t>(now.QuadPart),
                value.frequency, value.resumedAfter100ns), "capture_clock_invalid");
            paused_ = false; evidence_.reason = "capture_resumed"; evidence_.hr = S_OK;
            return true;
        }
        catch (const Failure& error)
        {
            value.duplication = nullptr;
            return Fail(error.reason, error.hr);
        }
        catch (const winrt::hresult_error& error) { value.duplication = nullptr; return Fail("capture_resume_failed", error.code()); }
        catch (...) { value.duplication = nullptr; return Fail("capture_resume_failed", E_FAIL); }
    }
    Evidence GameScreenCapture::Result() const noexcept { return evidence_; }
    bool GameScreenCapture::HasFrame() const noexcept
    { return impl_ && !closed_ && !paused_ && !evidence_.stopped && impl_->latestAvailable; }
    bool GameScreenCapture::IsForeground() const noexcept
    { return impl_ && !closed_ && GetForegroundWindow() == impl_->target.window && IsIconic(impl_->target.window) == FALSE; }
    HWND GameScreenCapture::TargetWindow() const noexcept
    { return impl_ && !closed_ ? impl_->target.window : nullptr; }
    bool GameScreenCapture::CheckIdentity() noexcept
    {
        if (!impl_ || closed_ || evidence_.stopped) return false;
        try { impl_->ValidateIdentity(); return true; }
        catch (const Failure& error) { return Fail(error.reason, error.hr); }
        catch (...) { return Fail("target_window_identity_changed", E_FAIL); }
    }
    HRESULT GameScreenCapture::Close() noexcept
    {
        if (closed_) return evidence_.cleanupHr;
        if (!impl_) { closed_ = true; evidence_.callbacksDrained = true; return evidence_.cleanupHr; }
        auto& value = *impl_;
        if (value.ownerThread != GetCurrentThreadId()) return E_UNEXPECTED;
        const auto remember = [this](HRESULT hr) { if (FAILED(hr) && SUCCEEDED(evidence_.cleanupHr)) evidence_.cleanupHr = hr; };
        remember(RecordRelease(value.Release(), evidence_, true));
        value.duplication = nullptr; value.latest = nullptr;
        value.multithread = nullptr; value.context = nullptr; value.device = nullptr;
        if (value.process)
        {
            if (!CloseHandle(value.process)) remember(HRESULT_FROM_WIN32(GetLastError()));
            value.process = nullptr;
        }
        if (value.previousDpi)
        {
            if (!SetThreadDpiAwarenessContext(value.previousDpi)) remember(HRESULT_FROM_WIN32(GetLastError()));
            value.previousDpi = nullptr;
        }
        evidence_.callbacksDrained = true;
        if (!evidence_.stopped) { evidence_.stopped = true; evidence_.reason = "capture_stopped"; }
        impl_.reset(); closed_ = true;
        return evidence_.cleanupHr;
    }

    UINT RunScreenCaptureContracts() noexcept
    {
        UINT count = 0; bool passed = true;
        const auto test = [&](bool value) { ++count; passed = passed && value; };
        test(IsScreenPauseReason("target_focus_lost") && IsScreenPauseReason("target_window_minimized") &&
            IsScreenPauseReason("target_not_fullscreen"));
        test(!IsScreenPauseReason(nullptr) && !IsScreenPauseReason("target_process_exited") &&
            !IsScreenPauseReason("capture_device_removed") && !IsScreenPauseReason("display_color_changed"));
        test(ScreenSourceEncoding(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, DXGI_FORMAT_B8G8R8A8_UNORM) == SourceEncoding::SrgbBgra8);
        test(ScreenSourceEncoding(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, DXGI_FORMAT_R16G16B16A16_FLOAT) == SourceEncoding::LinearScRgbFp16);
        test(ScreenSourceEncoding(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, DXGI_FORMAT_R10G10B10A2_UNORM) == SourceEncoding::Unknown);
        test(ScreenSourceEncoding(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, DXGI_FORMAT_B8G8R8A8_UNORM) == SourceEncoding::Unknown);
        test(ScreenSourceEncoding(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, DXGI_FORMAT_R16G16B16A16_FLOAT) == SourceEncoding::Unknown);
        test(ScreenSourceEncoding(DXGI_COLOR_SPACE_CUSTOM, DXGI_FORMAT_R16G16B16A16_FLOAT) == SourceEncoding::Unknown);
        test(ScreenFullscreenBounds({ -3840, 0, 0, 2160 }, { -3840, 0, 0, 2160 }));
        test(!ScreenFullscreenBounds({ -3840, 0, 0, 2160 }, { 0, 0, 3840, 2160 }));
        test(!ScreenFullscreenBounds({ 0, 0, 3840, 2159 }, { 0, 0, 3840, 2160 }));
        test(!ScreenFullscreenBounds({}, {}));
        LONGLONG time = 0;
        test(ScreenQpcTo100ns(12345678, 10000000, time) && time == 12345678);
        test(ScreenQpcTo100ns(3125001, 3125000, time) && time == 10000003);
        test(!ScreenQpcTo100ns(1, 0, time));
        test(!ScreenQpcTo100ns(0, 10000000, time));
        constexpr auto maximum = static_cast<std::uint64_t>((std::numeric_limits<LONGLONG>::max)());
        test(ScreenQpcTo100ns(maximum, maximum, time) && time == 10000000);
        test(!ScreenQpcTo100ns(maximum, 1, time));
        Evidence evidence;
        test(RecordRelease(DXGI_ERROR_ACCESS_LOST, evidence, true) == S_OK && evidence.duplicationInvalidated);
        test(RecordRelease(DXGI_ERROR_ACCESS_LOST, evidence, false) == DXGI_ERROR_ACCESS_LOST);
        evidence = {};
        test(RecordRelease(E_FAIL, evidence, true) == E_FAIL && !evidence.duplicationInvalidated);
        return passed ? count : 0;
    }
}

