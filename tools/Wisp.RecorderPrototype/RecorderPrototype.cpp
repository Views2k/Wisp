#include "ProbeContract.h"

#include <windows.h>
#include <tlhelp32.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <memory>
#include <mutex>
#include <sstream>

namespace
{
    using namespace recorder;
    using namespace winrt::Windows::Graphics::Capture;
    using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;
    using winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
    using Clock = std::chrono::steady_clock;

    class Handle
    {
    public:
        explicit Handle(HANDLE value = nullptr) noexcept : value_(value) {}
        ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        HANDLE get() const noexcept { return value_; }
        explicit operator bool() const noexcept { return value_ && value_ != INVALID_HANDLE_VALUE; }
    private:
        HANDLE value_;
    };

    struct Unavailable : std::exception
    {
        const char* reason;
        bool hasSize = false;
        int width = 0;
        int height = 0;
        explicit Unavailable(const char* value) noexcept : reason(value) {}
        Unavailable(const char* value, int w, int h) noexcept : reason(value), hasSize(true), width(w), height(h) {}
    };

    std::atomic<bool> cancelled{ false };
    BOOL WINAPI ConsoleHandler(DWORD signal) noexcept
    {
        if (signal != CTRL_C_EVENT && signal != CTRL_BREAK_EVENT) return FALSE;
        cancelled.store(true);
        return TRUE;
    }

    std::int64_t Qpc() noexcept
    {
        LARGE_INTEGER value{};
        QueryPerformanceCounter(&value);
        return value.QuadPart;
    }

    std::int64_t To100ns(std::int64_t ticks, std::int64_t frequency) noexcept
    {
        return (ticks / frequency) * 10000000 + (ticks % frequency) * 10000000 / frequency;
    }

    std::int64_t DiagnosticDifference(std::int64_t value, std::int64_t reference) noexcept
    {
        if (reference > 0 && value < std::numeric_limits<std::int64_t>::min() + reference)
            return std::numeric_limits<std::int64_t>::min();
        if (reference < 0 && value > std::numeric_limits<std::int64_t>::max() + reference)
            return std::numeric_limits<std::int64_t>::max();
        return value - reference;
    }

    std::uint64_t CreationTime(HANDLE process)
    {
        FILETIME created{}, exited{}, kernel{}, user{};
        winrt::check_bool(GetProcessTimes(process, &created, &exited, &kernel, &user));
        return (static_cast<std::uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    }

    bool IsForzaName(const wchar_t* file) noexcept
    {
        return _wcsicmp(file, L"ForzaHorizon6.exe") == 0;
    }

    bool IsForzaProcess(HANDLE process)
    {
        std::array<wchar_t, 32768> path{};
        DWORD length = static_cast<DWORD>(path.size());
        winrt::check_bool(QueryFullProcessImageNameW(process, 0, path.data(), &length));
        const wchar_t* name = wcsrchr(path.data(), L'\\');
        return IsForzaName(name ? name + 1 : path.data());
    }

    bool IsAnyForzaRunning()
    {
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
        if (!snapshot) winrt::throw_last_error();
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        if (!Process32FirstW(snapshot.get(), &entry)) winrt::throw_last_error();
        do { if (IsForzaName(entry.szExeFile)) return true; }
        while (Process32NextW(snapshot.get(), &entry));
        if (GetLastError() != ERROR_NO_MORE_FILES) winrt::throw_last_error();
        return false;
    }

    std::string GuidString(const GUID& guid)
    {
        wchar_t value[40]{};
        if (!StringFromGUID2(guid, value, static_cast<int>(std::size(value)))) return "unavailable";
        std::string result;
        for (const auto character : value)
        {
            if (!character) break;
            result.push_back(static_cast<char>(character));
        }
        return result;
    }

    std::string HrString(HRESULT hr)
    {
        std::ostringstream text;
        text << "0x" << std::hex << std::setw(8) << std::setfill('0') << static_cast<unsigned long>(hr);
        return text.str();
    }

    bool IntervalPropertyPresent()
    {
        return winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
            L"Windows.Graphics.Capture.GraphicsCaptureSession", L"MinUpdateInterval");
    }

    struct MediaFoundation
    {
        MediaFoundation() { winrt::check_hresult(MFStartup(MF_VERSION, MFSTARTUP_LITE)); }
        ~MediaFoundation() { MFShutdown(); }
        MediaFoundation(const MediaFoundation&) = delete;
        MediaFoundation& operator=(const MediaFoundation&) = delete;
    };

    struct RuntimeApartment
    {
        RuntimeApartment() { winrt::init_apartment(winrt::apartment_type::multi_threaded); }
        ~RuntimeApartment() { winrt::uninit_apartment(); }
    };

    struct ConsoleCancellation
    {
        ConsoleCancellation() { winrt::check_bool(SetConsoleCtrlHandler(ConsoleHandler, TRUE)); }
        ~ConsoleCancellation() { SetConsoleCtrlHandler(ConsoleHandler, FALSE); }
    };

    struct Activations
    {
        IMFActivate** values = nullptr;
        UINT32 count = 0;
        ~Activations()
        {
            for (UINT32 index = 0; index < count; ++index) values[index]->Release();
            CoTaskMemFree(values);
        }
    };

    int Probe()
    {
        const bool captureSupported = GraphicsCaptureSession::IsSupported();
        const bool intervalPresent = IntervalPropertyPresent();
        MediaFoundation mediaFoundation;
        winrt::com_ptr<IDXGIFactory1> factory;
        winrt::check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1), factory.put_void()));
        std::ostringstream output;
        output << "{\"mode\":\"probe\",\"captureSupported\":" << (captureSupported ? "true" : "false")
            << ",\"minUpdateIntervalMetadataPresent\":" << (intervalPresent ? "true" : "false")
            << ",\"graphicsDeviceCreated\":false,\"captureStarted\":false,\"encoderActivated\":false,\"encodingVerified\":false,\"adapters\":[";
        bool first = true;
        for (UINT index = 0; ; ++index)
        {
            winrt::com_ptr<IDXGIAdapter1> adapter;
            const auto result = factory->EnumAdapters1(index, adapter.put());
            if (result == DXGI_ERROR_NOT_FOUND) break;
            winrt::check_hresult(result);
            DXGI_ADAPTER_DESC1 description{};
            winrt::check_hresult(adapter->GetDesc1(&description));
            if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
            winrt::com_ptr<IMFAttributes> attributes;
            winrt::check_hresult(MFCreateAttributes(attributes.put(), 1));
            winrt::check_hresult(attributes->SetBlob(MFT_ENUM_ADAPTER_LUID,
                reinterpret_cast<const UINT8*>(&description.AdapterLuid), sizeof(LUID)));
            MFT_REGISTER_TYPE_INFO input{ MFMediaType_Video, MFVideoFormat_NV12 };
            MFT_REGISTER_TYPE_INFO encoded{ MFMediaType_Video, MFVideoFormat_H264 };
            Activations transforms;
            const auto enumeration = MFTEnum2(MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, &input, &encoded,
                attributes.get(), &transforms.values, &transforms.count);
            if (!first) output << ',';
            first = false;
            output << "{\"index\":" << index << ",\"vendorId\":" << description.VendorId
                << ",\"deviceId\":" << description.DeviceId
                << ",\"adapterLuidLow\":" << description.AdapterLuid.LowPart << ",\"adapterLuidHigh\":" << description.AdapterLuid.HighPart
                << ",\"dedicatedVideoMemoryMiB\":" << description.DedicatedVideoMemory / (1024 * 1024)
                << ",\"hardwareEnumerationResult\":\"" << HrString(enumeration) << "\",\"hardwareH264Candidates\":[";
            if (SUCCEEDED(enumeration))
            {
                for (UINT32 transform = 0; transform < transforms.count; ++transform)
                {
                    GUID classId{};
                    const bool hasId = SUCCEEDED(transforms.values[transform]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &classId));
                    UINT32 urlLength = 0;
                    const bool hasHardwareAttribute = SUCCEEDED(transforms.values[transform]->GetStringLength(
                        MFT_ENUM_HARDWARE_URL_Attribute, &urlLength)) && urlLength > 0;
                    if (transform) output << ',';
                    output << "{\"classId\":\"" << (hasId ? GuidString(classId) : "unavailable")
                        << "\",\"hardwareAttributePresent\":" << (hasHardwareAttribute ? "true" : "false") << '}';
                }
            }
            output << "],\"outputs\":[";
            bool firstOutput = true;
            for (UINT outputIndex = 0; ; ++outputIndex)
            {
                winrt::com_ptr<IDXGIOutput> displayOutput;
                const auto outputResult = adapter->EnumOutputs(outputIndex, displayOutput.put());
                if (outputResult == DXGI_ERROR_NOT_FOUND) break;
                winrt::check_hresult(outputResult);
                DXGI_OUTPUT_DESC displayDescription{};
                winrt::check_hresult(displayOutput->GetDesc(&displayDescription));
                const auto output6 = displayOutput.try_as<IDXGIOutput6>();
                DXGI_OUTPUT_DESC1 color{};
                const auto colorResult = output6 ? output6->GetDesc1(&color) : E_NOINTERFACE;
                if (!firstOutput) output << ',';
                firstOutput = false;
                output << "{\"index\":" << outputIndex
                    << ",\"attachedToDesktop\":" << (displayDescription.AttachedToDesktop ? "true" : "false")
                    << ",\"width\":" << displayDescription.DesktopCoordinates.right - displayDescription.DesktopCoordinates.left
                    << ",\"height\":" << displayDescription.DesktopCoordinates.bottom - displayDescription.DesktopCoordinates.top
                    << ",\"colorQueryResult\":\"" << HrString(colorResult) << "\",\"colorSpace\":";
                if (SUCCEEDED(colorResult)) output << static_cast<unsigned>(color.ColorSpace);
                else output << "null";
                output << ",\"bitsPerColor\":";
                if (SUCCEEDED(colorResult)) output << color.BitsPerColor;
                else output << "null";
                output << ",\"sdrSrgb\":";
                if (SUCCEEDED(colorResult)) output << (color.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ? "true" : "false");
                else output << "null";
                output << '}';
            }
            output << "]}";
        }
        output << "]}";
        std::cout << output.str() << '\n';
        return 0;
    }

    struct Target
    {
        HWND window;
        DWORD processId;
        Handle process;
        HMONITOR monitor;
        RECT client{};
        bool fixture;

        Target(HWND handle, DWORD id, std::uint64_t created, bool ownFixture)
            : window(handle), processId(id),
              process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, id)),
              monitor(MonitorFromWindow(handle, MONITOR_DEFAULTTONULL)), fixture(ownFixture)
        {
            if (!process) winrt::throw_last_error();
            if (CreationTime(process.get()) != created) throw Unavailable("process_identity_mismatch");
            if (fixture ? id != GetCurrentProcessId() : !IsForzaProcess(process.get()))
                throw Unavailable("target_is_not_authorized_process");
            DWORD windowProcess = 0;
            GetWindowThreadProcessId(window, &windowProcess);
            if (!IsWindow(window) || windowProcess != id || GetAncestor(window, GA_ROOT) != window)
                throw Unavailable("target_window_identity_mismatch");
            if (!monitor) throw Unavailable("target_has_no_monitor");
            winrt::check_bool(GetClientRect(window, &client));
            if (!IsValidCaptureSize(client.right - client.left, client.bottom - client.top))
                throw Unavailable("target_size_unsupported");
        }

        StopReason Check(ID3D11Device* device) const noexcept
        {
            DWORD currentProcess = 0;
            GetWindowThreadProcessId(window, &currentProcess);
            RECT current{};
            const bool gotRect = GetClientRect(window, &current) != FALSE;
            TargetSnapshot snapshot;
            snapshot.processAlive = WaitForSingleObject(process.get(), 0) == WAIT_TIMEOUT;
            snapshot.windowExists = IsWindow(window) != FALSE;
            snapshot.sameProcess = currentProcess == processId && GetAncestor(window, GA_ROOT) == window;
            snapshot.minimized = IsIconic(window) != FALSE;
            snapshot.visible = IsWindowVisible(window) != FALSE;
            snapshot.foreground = GetAncestor(GetForegroundWindow(), GA_ROOTOWNER) == GetAncestor(window, GA_ROOTOWNER);
            snapshot.sameSize = gotRect && current.right - current.left == client.right - client.left &&
                current.bottom - current.top == client.bottom - client.top;
            snapshot.sameMonitor = MonitorFromWindow(window, MONITOR_DEFAULTTONULL) == monitor;
            snapshot.deviceHealthy = !device || SUCCEEDED(device->GetDeviceRemovedReason());
            return ValidateSnapshot(snapshot, !fixture);
        }
    };

    struct DisplayAdapter
    {
        winrt::com_ptr<IDXGIAdapter1> adapter;
        winrt::com_ptr<IDXGIOutput6> output;
        DXGI_COLOR_SPACE_TYPE colorSpace;
    };

    DisplayAdapter SelectDisplayAdapter(HMONITOR monitor, bool hdrMetadata)
    {
        winrt::com_ptr<IDXGIFactory1> factory;
        winrt::check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1), factory.put_void()));
        for (UINT adapterIndex = 0; ; ++adapterIndex)
        {
            winrt::com_ptr<IDXGIAdapter1> adapter;
            const auto adapterResult = factory->EnumAdapters1(adapterIndex, adapter.put());
            if (adapterResult == DXGI_ERROR_NOT_FOUND) break;
            winrt::check_hresult(adapterResult);
            DXGI_ADAPTER_DESC1 description{};
            winrt::check_hresult(adapter->GetDesc1(&description));
            if ((description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
            for (UINT outputIndex = 0; ; ++outputIndex)
            {
                winrt::com_ptr<IDXGIOutput> output;
                const auto outputResult = adapter->EnumOutputs(outputIndex, output.put());
                if (outputResult == DXGI_ERROR_NOT_FOUND) break;
                winrt::check_hresult(outputResult);
                DXGI_OUTPUT_DESC outputDescription{};
                winrt::check_hresult(output->GetDesc(&outputDescription));
                if (outputDescription.Monitor != monitor) continue;
                const auto output6 = output.try_as<IDXGIOutput6>();
                if (!output6) throw Unavailable("display_color_capability_unavailable");
                DXGI_OUTPUT_DESC1 color{};
                winrt::check_hresult(output6->GetDesc1(&color));
                const bool sdr = color.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
                const bool hdr = color.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                if (!sdr && !(hdrMetadata && hdr))
                    throw Unavailable("hdr_or_advanced_color_not_supported_by_prototype");
                return { adapter, output6, color.ColorSpace };
            }
        }
        throw Unavailable("hardware_display_adapter_unavailable");
    }

    struct Statistics
    {
        std::uint64_t frames = 0;
        std::uint64_t emptyCallbacks = 0;
        std::int64_t firstTimestamp = 0;
        std::int64_t lastTimestamp = 0;
        std::int64_t maxFrameGap = 0;
        std::int64_t callbackTicks = 0;
        std::int64_t maxCallbackTicks = 0;
        std::int64_t minimumTimestampMinusArrival = 0;
        std::int64_t maximumTimestampMinusArrival = 0;
        std::int64_t lastTimestampMinusArrival = 0;
        std::uint64_t aheadOfArrivalFrames = 0;
        std::int64_t maximumAheadOfArrival = 0;
        unsigned clockFailure = 0;
        std::int64_t failureTimestampMinusArrival = 0;
        std::int64_t failureTimestampMinusPrevious = 0;
    };

    struct CaptureState
    {
        const int width;
        const int height;
        const std::int64_t frequency;
        Handle stopped{ CreateEventW(nullptr, TRUE, FALSE, nullptr) };
        std::atomic<StopReason> reason{ StopReason::None };
        std::atomic<HRESULT> error{ S_OK };
        std::atomic<std::int64_t> lastArrival;
        std::mutex countersMutex;
        Statistics counters;
        std::mutex callbackMutex;
        std::condition_variable callbacksFinished;
        unsigned activeCallbacks = 0;

        CaptureState(int w, int h, std::int64_t f)
            : width(w), height(h), frequency(f), lastArrival(Qpc())
        {
            if (!stopped) winrt::throw_last_error();
        }

        void Stop(StopReason value) noexcept
        {
            auto expected = StopReason::None;
            if (reason.compare_exchange_strong(expected, value)) SetEvent(stopped.get());
        }
    };

    struct CallbackScope
    {
        std::shared_ptr<CaptureState> state;
        explicit CallbackScope(std::shared_ptr<CaptureState> value) : state(std::move(value))
        {
            std::lock_guard<std::mutex> lock(state->callbackMutex);
            ++state->activeCallbacks;
        }
        ~CallbackScope()
        {
            std::lock_guard<std::mutex> lock(state->callbackMutex);
            --state->activeCallbacks;
            state->callbacksFinished.notify_all();
        }
    };

    struct FrameScope
    {
        Direct3D11CaptureFrame frame{ nullptr };
        ~FrameScope() { try { if (frame) frame.Close(); } catch (...) {} }
    };

    void FrameArrived(const std::weak_ptr<CaptureState>& weak,
        const Direct3D11CaptureFramePool& pool) noexcept
    {
        const auto state = weak.lock();
        if (!state) return;
        CallbackScope callback(state);
        if (state->reason.load() != StopReason::None) return;
        const auto started = Qpc();
        try
        {
            FrameScope frame{ pool.TryGetNextFrame() };
            if (!frame.frame)
            {
                std::lock_guard<std::mutex> lock(state->countersMutex);
                ++state->counters.emptyCallbacks;
                return;
            }
            const auto size = frame.frame.ContentSize();
            if (size.Width != state->width || size.Height != state->height)
            {
                state->Stop(StopReason::SizeChanged);
                return;
            }
            const auto timestamp = frame.frame.SystemRelativeTime().count();
            const auto now = Qpc();
            const auto currentTime = To100ns(now, state->frequency);
            if (timestamp <= 0)
            {
                {
                    std::lock_guard<std::mutex> lock(state->countersMutex);
                    state->counters.clockFailure = 1;
                    state->counters.failureTimestampMinusArrival = DiagnosticDifference(timestamp, currentTime);
                }
                state->Stop(StopReason::FrameClockInvalid);
                return;
            }
            frame.frame.Close();
            frame.frame = nullptr;
            state->lastArrival.store(now);
            std::lock_guard<std::mutex> lock(state->countersMutex);
            auto& counters = state->counters;
            if (counters.frames && timestamp <= counters.lastTimestamp)
            {
                counters.clockFailure = 3;
                counters.failureTimestampMinusArrival = DiagnosticDifference(timestamp, currentTime);
                counters.failureTimestampMinusPrevious = DiagnosticDifference(timestamp, counters.lastTimestamp);
                state->Stop(StopReason::FrameClockInvalid);
                return;
            }
            if (!counters.frames) counters.firstTimestamp = timestamp;
            else counters.maxFrameGap = std::max(counters.maxFrameGap, timestamp - counters.lastTimestamp);
            counters.lastTimestamp = timestamp;
            ++counters.frames;
            const auto callbackTicks = Qpc() - started;
            counters.callbackTicks += callbackTicks;
            counters.maxCallbackTicks = std::max(counters.maxCallbackTicks, callbackTicks);
            const auto offset = DiagnosticDifference(timestamp, currentTime);
            if (counters.frames == 1)
            {
                counters.minimumTimestampMinusArrival = offset;
                counters.maximumTimestampMinusArrival = offset;
            }
            else
            {
                counters.minimumTimestampMinusArrival = std::min(counters.minimumTimestampMinusArrival, offset);
                counters.maximumTimestampMinusArrival = std::max(counters.maximumTimestampMinusArrival, offset);
            }
            counters.lastTimestampMinusArrival = offset;
            if (offset > 0)
            {
                ++counters.aheadOfArrivalFrames;
                counters.maximumAheadOfArrival = std::max(counters.maximumAheadOfArrival, offset);
            }
        }
        catch (const winrt::hresult_error& error)
        {
            state->error.store(error.code());
            state->Stop(StopReason::FrameError);
        }
        catch (...)
        {
            state->error.store(E_FAIL);
            state->Stop(StopReason::FrameError);
        }
    }

    class CaptureResources
    {
    public:
        GraphicsCaptureItem item{ nullptr };
        Direct3D11CaptureFramePool pool{ nullptr };
        GraphicsCaptureSession session{ nullptr };
        std::shared_ptr<CaptureState> state;
        winrt::event_token framesToken{};
        winrt::event_token closedToken{};
        bool hasFramesHandler = false;
        bool hasClosedHandler = false;
        bool shutdown = false;

        ~CaptureResources() { Shutdown(); }
        bool Shutdown() noexcept
        {
            if (shutdown) return true;
            shutdown = true;
            if (state) state->Stop(StopReason::Cancelled);
            try { if (hasFramesHandler) pool.FrameArrived(framesToken); } catch (...) {}
            try { if (hasClosedHandler) item.Closed(closedToken); } catch (...) {}
            // Do not hold a callback/statistics lock across WinRT revocation or Close.
            try { if (session) session.Close(); } catch (...) {}
            try { if (pool) pool.Close(); } catch (...) {}
            bool drained = true;
            if (state)
            {
                std::unique_lock<std::mutex> lock(state->callbackMutex);
                drained = state->callbacksFinished.wait_for(lock, std::chrono::seconds(2),
                    [this] { return state->activeCallbacks == 0; });
            }
            session = nullptr;
            pool = nullptr;
            item = nullptr;
            return drained;
        }
    };

    LRESULT CALLBACK FixtureProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_TIMER)
        {
            InvalidateRect(window, nullptr, FALSE);
            return 0;
        }
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint{};
            const HDC dc = BeginPaint(window, &paint);
            RECT client{};
            GetClientRect(window, &client);
            FillRect(dc, &client, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
            const int width = std::max(1L, client.right - 80);
            const int x = static_cast<int>((GetTickCount64() / 4) % static_cast<ULONGLONG>(width));
            RECT bar{ x, 20, x + 80, std::max(21L, client.bottom - 20) };
            const HBRUSH brush = CreateSolidBrush(RGB(55, 215, 190));
            if (brush) { FillRect(dc, &bar, brush); DeleteObject(brush); }
            EndPaint(window, &paint);
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    struct FixtureWindow
    {
        HWND window = nullptr;
        FixtureWindow()
        {
            if (IsAnyForzaRunning()) throw Unavailable("close_game_before_fixture");
            WNDCLASSW definition{};
            definition.lpfnWndProc = FixtureProcedure;
            definition.hInstance = GetModuleHandleW(nullptr);
            definition.lpszClassName = L"WispRecorderPrototypeFixture";
            definition.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            if (!RegisterClassW(&definition)) winrt::throw_last_error();
            window = CreateWindowExW(WS_EX_NOACTIVATE, definition.lpszClassName,
                L"Wisp capture-only fixture", WS_OVERLAPPEDWINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, 960, 540,
                nullptr, nullptr, definition.hInstance, nullptr);
            if (!window) winrt::throw_last_error();
            // Consume a launcher SW_HIDE hint, then show only the fixture without activation.
            ShowWindow(window, SW_SHOWNOACTIVATE);
            if (!SetWindowPos(window, nullptr, 0, 0, 0, 0,
                SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER))
            {
                const auto error = GetLastError();
                DestroyWindow(window);
                window = nullptr;
                winrt::throw_hresult(HRESULT_FROM_WIN32(error));
            }
            if (!SetTimer(window, 1, 16, nullptr))
            {
                const auto error = GetLastError();
                DestroyWindow(window);
                window = nullptr;
                winrt::throw_hresult(HRESULT_FROM_WIN32(error));
            }
        }
        ~FixtureWindow()
        {
            if (window && IsWindow(window)) { KillTimer(window, 1); DestroyWindow(window); }
            UnregisterClassW(L"WispRecorderPrototypeFixture", GetModuleHandleW(nullptr));
        }
    };

    int Capture(const Arguments& arguments)
    {
        if (!GraphicsCaptureSession::IsSupported()) throw Unavailable("windows_capture_not_supported");
        if (!IntervalPropertyPresent()) throw Unavailable("native_capture_rate_limit_unavailable");
        std::unique_ptr<FixtureWindow> fixture;
        HWND window = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(arguments.window));
        DWORD processId = arguments.processId;
        auto created = arguments.creationTime;
        if (arguments.mode == Mode::Fixture)
        {
            fixture = std::make_unique<FixtureWindow>();
            window = fixture->window;
            processId = GetCurrentProcessId();
            created = CreationTime(GetCurrentProcess());
        }
        Target target(window, processId, created, fixture != nullptr);
        if (const auto initial = target.Check(nullptr); initial != StopReason::None)
            throw Unavailable(Name(initial));
        const bool fixtureHdrMetadata = arguments.mode == Mode::Fixture && arguments.fixtureHdrMetadata;
        const bool captureHdrMetadata = arguments.mode == Mode::Capture && arguments.captureHdrMetadata;
        const bool hdrMetadata = fixtureHdrMetadata || captureHdrMetadata;
        const auto display = SelectDisplayAdapter(target.monitor, hdrMetadata);
        const auto& adapter = display.adapter;
        DXGI_ADAPTER_DESC1 adapterDescription{};
        winrt::check_hresult(adapter->GetDesc1(&adapterDescription));
        winrt::com_ptr<ID3D11Device> device;
        const D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 };
        winrt::check_hresult(D3D11CreateDevice(adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, 1, D3D11_SDK_VERSION, device.put(), nullptr, nullptr));
        const auto dxgiDevice = device.as<IDXGIDevice>();
        winrt::com_ptr<IInspectable> projectedDevice;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), projectedDevice.put()));
        const auto captureDevice = projectedDevice.as<IDirect3DDevice>();
        CaptureResources capture;
        const auto factory = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(factory->CreateForWindow(window, winrt::guid_of<GraphicsCaptureItem>(),
            reinterpret_cast<void**>(winrt::put_abi(capture.item))));
        const auto size = capture.item.Size();
        if (!IsValidCaptureSize(size.Width, size.Height))
            throw Unavailable("native_capture_size_unsupported", size.Width, size.Height);
        LARGE_INTEGER frequency{};
        winrt::check_bool(QueryPerformanceFrequency(&frequency));
        capture.state = std::make_shared<CaptureState>(size.Width, size.Height, frequency.QuadPart);
        const auto pixelFormat = hdrMetadata ? DirectXPixelFormat::R16G16B16A16Float :
            DirectXPixelFormat::B8G8R8A8UIntNormalized;
        capture.pool = Direct3D11CaptureFramePool::CreateFreeThreaded(captureDevice,
            pixelFormat, static_cast<int>(arguments.buffers), size);
        capture.session = capture.pool.CreateCaptureSession(capture.item);
        const auto rateControl = capture.session.try_as<IGraphicsCaptureSession5>();
        if (!rateControl) throw Unavailable("native_capture_rate_limit_interface_unavailable");
        const winrt::Windows::Foundation::TimeSpan interval{ 333334 };
        rateControl.MinUpdateInterval(interval);
        const auto appliedInterval = rateControl.MinUpdateInterval();
        if (appliedInterval < interval) throw Unavailable("native_capture_rate_limit_not_applied");
        if (const auto cursor = capture.session.try_as<IGraphicsCaptureSession2>()) cursor.IsCursorCaptureEnabled(false);
        if (const auto secondary = capture.session.try_as<IGraphicsCaptureSession6>()) secondary.IncludeSecondaryWindows(false);
        const std::weak_ptr<CaptureState> weak(capture.state);
        capture.framesToken = capture.pool.FrameArrived([weak](const auto& pool, const auto&)
            { FrameArrived(weak, pool); });
        capture.hasFramesHandler = true;
        capture.closedToken = capture.item.Closed([weak](const auto&, const auto&)
        {
            if (const auto state = weak.lock())
            {
                CallbackScope callback(state);
                state->Stop(StopReason::CaptureItemClosed);
            }
        });
        capture.hasClosedHandler = true;
        if (const auto beforeStart = target.Check(device.get()); beforeStart != StopReason::None)
            throw Unavailable(Name(beforeStart));
        ConsoleCancellation consoleCancellation;
        capture.session.StartCapture();
        const auto started = Clock::now();
        auto nextValidation = started;
        bool transitionApplied = false;
        while (capture.state->reason.load() == StopReason::None)
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            const auto now = Clock::now();
            if (cancelled.load()) capture.state->Stop(StopReason::Cancelled);
            if (now - started >= std::chrono::seconds(arguments.seconds))
                capture.state->Stop(StopReason::Completed);
            if (fixture && !transitionApplied && now - started >= std::chrono::seconds(1))
            {
                transitionApplied = true;
                if (arguments.scenario == Scenario::Resize)
                    SetWindowPos(window, nullptr, 0, 0, 720, 420, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                else if (arguments.scenario == Scenario::Minimize) ShowWindow(window, SW_SHOWMINNOACTIVE);
                else if (arguments.scenario == Scenario::Close) DestroyWindow(window);
            }
            if (now >= nextValidation)
            {
                nextValidation = now + std::chrono::milliseconds(100);
                if (const auto invalid = target.Check(device.get()); invalid != StopReason::None)
                    capture.state->Stop(invalid);
                DXGI_OUTPUT_DESC1 color{};
                if (FAILED(display.output->GetDesc1(&color)) ||
                    color.ColorSpace != display.colorSpace)
                    capture.state->Stop(StopReason::DisplayColorChanged);
                if (fixture && IsAnyForzaRunning()) capture.state->Stop(StopReason::GameStarted);
                if (Qpc() - capture.state->lastArrival.load() > 2 * frequency.QuadPart)
                    capture.state->Stop(StopReason::NoFrames);
            }
            if (capture.state->reason.load() != StopReason::None) break;
            const HANDLE event = capture.state->stopped.get();
            const auto wait = MsgWaitForMultipleObjectsEx(1, &event, 100, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (wait == WAIT_FAILED) winrt::throw_last_error();
        }
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - started).count();
        const bool callbacksDrained = capture.Shutdown();
        Statistics statistics;
        {
            std::lock_guard<std::mutex> lock(capture.state->countersMutex);
            statistics = capture.state->counters;
        }
        auto reason = callbacksDrained ? capture.state->reason.load() : StopReason::TeardownTimeout;
        if (reason == StopReason::Completed && statistics.frames == 0) reason = StopReason::NoFrames;
        const bool complete = reason == StopReason::Completed && statistics.frames > 0;
        std::cout << "{\"mode\":\"" << (fixture ? "fixture" : "capture")
            << "\",\"outcome\":\"" << (complete ? "completed" : "invalidated")
            << "\",\"reason\":\"" << Name(reason) << "\",\"captureOnly\":true"
            << ",\"encodingVerified\":false,\"gameplayPerformanceVerified\":false"
            << ",\"colorFidelityVerified\":false,\"fixtureHdrMetadata\":" << (fixtureHdrMetadata ? "true" : "false")
            << ",\"captureHdrMetadata\":" << (captureHdrMetadata ? "true" : "false")
            << ",\"nativeSize\":true,\"width\":" << size.Width << ",\"height\":" << size.Height
            << ",\"requestedMaxFps\":30,\"interval100ns\":" << interval.count()
            << ",\"intervalAppliedAndReadBack\":true,\"appliedInterval100ns\":" << appliedInterval.count()
            << ",\"displayColorAtStart\":\"" << (display.colorSpace == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ? "SDR_sRGB" : "HDR_PQ_BT2020") << '"'
            << ",\"poolPixelFormat\":\"" << (hdrMetadata ? "R16G16B16A16Float" : "B8G8R8A8UIntNormalized") << '"'
            << ",\"nominalPoolPixelBytes\":" << static_cast<std::uint64_t>(size.Width) * static_cast<std::uint64_t>(size.Height) * (hdrMetadata ? 8 : 4) * arguments.buffers
            << ",\"poolEstimateExcludesPaddingAndOsAllocations\":true"
            << ",\"poolBuffers\":" << arguments.buffers << ",\"frames\":" << statistics.frames
            << ",\"emptyCallbacks\":" << statistics.emptyCallbacks << ",\"elapsedMs\":" << elapsed
            << ",\"firstFrameTimestamp100ns\":" << statistics.firstTimestamp
            << ",\"lastFrameTimestamp100ns\":" << statistics.lastTimestamp
            << ",\"timestampSpan100ns\":" << statistics.lastTimestamp - statistics.firstTimestamp
            << ",\"maxFrameGap100ns\":" << statistics.maxFrameGap
            << ",\"minimumTimestampMinusArrival100ns\":" << statistics.minimumTimestampMinusArrival
            << ",\"maximumTimestampMinusArrival100ns\":" << statistics.maximumTimestampMinusArrival
            << ",\"lastTimestampMinusArrival100ns\":" << statistics.lastTimestampMinusArrival
            << ",\"aheadOfArrivalFrames\":" << statistics.aheadOfArrivalFrames
            << ",\"maximumAheadOfArrival100ns\":" << statistics.maximumAheadOfArrival
            << ",\"meanAcceptedFrameWork100ns\":" << (statistics.frames ? To100ns(statistics.callbackTicks, frequency.QuadPart) / static_cast<std::int64_t>(statistics.frames) : 0)
            << ",\"maxAcceptedFrameWork100ns\":" << To100ns(statistics.maxCallbackTicks, frequency.QuadPart)
            << ",\"qpcFrequencyHz\":" << frequency.QuadPart
            << ",\"clockFailureKind\":\"" << (statistics.clockFailure == 1 ? "nonpositive" :
                statistics.clockFailure == 3 ? "nonmonotonic" : "none") << '"'
            << ",\"failureTimestampMinusArrival100ns\":" << statistics.failureTimestampMinusArrival
            << ",\"failureTimestampMinusPrevious100ns\":" << statistics.failureTimestampMinusPrevious
            << ",\"vendorId\":" << adapterDescription.VendorId << ",\"deviceId\":" << adapterDescription.DeviceId
            << ",\"adapterLuidLow\":" << adapterDescription.AdapterLuid.LowPart << ",\"adapterLuidHigh\":" << adapterDescription.AdapterLuid.HighPart
            << ",\"displayAdapterNotGameAdapterClaim\":true,\"callbacksDrained\":" << (callbacksDrained ? "true" : "false")
            << ",\"hresult\":\"" << HrString(capture.state->error.load()) << "\"}" << '\n';
        return complete ? 0 : 2;
    }

    int SelfTest()
    {
        unsigned tests = 0;
        const auto require = [&tests](bool condition)
        {
            if (!condition) throw std::runtime_error("contract_assertion_failed");
            ++tests;
        };
        const auto rejects = [&require](const std::vector<std::wstring>& input)
        {
            bool rejected = false;
            try { static_cast<void>(ParseArguments(input)); }
            catch (const std::invalid_argument&) { rejected = true; }
            require(rejected);
        };
        require(ParseArguments({}).mode == Mode::Help);
        require(ParseArguments({ L"--probe" }).mode == Mode::Probe);
        const auto target = ParseArguments({ L"--capture", L"--hwnd", L"0x1234", L"--pid", L"123",
            L"--creation-time", L"133000000000000000", L"--seconds", L"60", L"--buffers", L"3" });
        require(target.window == 0x1234 && target.processId == 123 && target.seconds == 60 && target.buffers == 3);
        require(!target.fixtureHdrMetadata && !target.captureHdrMetadata);
        require(ParseArguments({ L"--fixture", L"--seconds", L"2", L"--scenario", L"resize" }).scenario == Scenario::Resize);
        require(ParseArguments({ L"--fixture", L"--fixture-hdr-metadata" }).fixtureHdrMetadata);
        require(ParseArguments({ L"--fixture", L"--fixture-hdr-metadata", L"--seconds", L"3" }).seconds == 3);
        require(ParseArguments({ L"--fixture", L"--seconds", L"3", L"--fixture-hdr-metadata" }).fixtureHdrMetadata);
        require(!ParseArguments({ L"--fixture" }).fixtureHdrMetadata);
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100", L"--fixture-hdr-metadata" });
        rejects({ L"--probe", L"--fixture-hdr-metadata" });
        rejects({ L"--self-test", L"--fixture-hdr-metadata" });
        rejects({ L"--fixture", L"--fixture-hdr-metadata", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100" });
        rejects({ L"--fixture", L"--fixture-hdr-metadata", L"--fixture-hdr-metadata" });
        const auto hdrTarget = ParseArguments({ L"--capture", L"--capture-hdr-metadata", L"--hwnd", L"0x1234",
            L"--pid", L"123", L"--creation-time", L"133000000000000000", L"--seconds", L"3", L"--buffers", L"2" });
        require(hdrTarget.mode == Mode::Capture && hdrTarget.captureHdrMetadata && !hdrTarget.fixtureHdrMetadata &&
            hdrTarget.window == 0x1234 && hdrTarget.processId == 123 && hdrTarget.creationTime == 133000000000000000 &&
            hdrTarget.seconds == 3 && hdrTarget.buffers == 2);
        require(ParseArguments({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100",
            L"--capture-hdr-metadata" }).captureHdrMetadata);
        require(!ParseArguments({ L"--fixture", L"--fixture-hdr-metadata" }).captureHdrMetadata);
        rejects({ L"--capture", L"--capture-hdr-metadata" });
        rejects({ L"--capture", L"--capture-hdr-metadata", L"--pid", L"123", L"--creation-time", L"100" });
        rejects({ L"--capture", L"--capture-hdr-metadata", L"--hwnd", L"1", L"--creation-time", L"100" });
        rejects({ L"--capture", L"--capture-hdr-metadata", L"--hwnd", L"1", L"--pid", L"123" });
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100",
            L"--capture-hdr-metadata", L"--capture-hdr-metadata" });
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100",
            L"--capture-hdr-metadata", L"true" });
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100",
            L"--capture-hdr-metadata=true" });
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100",
            L"--capture-hdr-metadata", L"--fixture-hdr-metadata" });
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"100",
            L"--capture-hdr-metadata", L"--scenario", L"normal" });
        rejects({ L"--fixture", L"--capture-hdr-metadata" });
        rejects({ L"--fixture", L"--fixture-hdr-metadata", L"--capture-hdr-metadata" });
        rejects({ L"--probe", L"--capture-hdr-metadata" });
        rejects({ L"--self-test", L"--capture-hdr-metadata" });
        rejects({ L"--help", L"--capture-hdr-metadata" });
        rejects({ L"--capture" });
        rejects({ L"--probe", L"--seconds", L"1" });
        rejects({ L"--fixture", L"--hwnd", L"123" });
        rejects({ L"--fixture", L"--seconds", L"0" });
        rejects({ L"--fixture", L"--seconds", L"61" });
        rejects({ L"--fixture", L"--buffers", L"1" });
        rejects({ L"--fixture", L"--buffers", L"4" });
        rejects({ L"--fixture", L"--seconds", L"-1" });
        rejects({ L"--fixture", L"--seconds", L"18446744073709551616" });
        rejects({ L"--fixture", L"--seconds", L"+1" });
        rejects({ L"--fixture", L"--seconds", L"1x" });
        rejects({ L"--fixture", L"--seconds", L" 1" });
        rejects({ L"--fixture", L"--seconds" });
        rejects({ L"--fixture", L"--seconds", L"2", L"--seconds", L"3" });
        rejects({ L"--fixture", L"--seconds", L"1", L"--scenario", L"close" });
        rejects({ L"--fixture", L"--scenario", L"desktop" });
        rejects({ L"--desktop" });
        rejects({ L"--capture", L"--hwnd", L"0", L"--pid", L"123", L"--creation-time", L"100" });
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"4294967296", L"--creation-time", L"100" });
        rejects({ L"--capture", L"--hwnd", L"1", L"--pid", L"123", L"--creation-time", L"0" });
        require(IsValidCaptureSize(1920, 1080));
        require(IsValidCaptureSize(3840, 2160));
        require(!IsValidCaptureSize(0, 1080));
        require(!IsValidCaptureSize(8193, 1080));
        require(!IsValidCaptureSize(8192, 8192));
        TargetSnapshot snapshot;
        require(ValidateSnapshot(snapshot, true) == StopReason::None);
        snapshot.foreground = false;
        require(ValidateSnapshot(snapshot, true) == StopReason::ForegroundLost);
        require(ValidateSnapshot(snapshot, false) == StopReason::None);
        const auto invalidates = [&require](auto alter, StopReason expected)
        {
            TargetSnapshot value;
            alter(value);
            require(ValidateSnapshot(value, true) == expected);
        };
        invalidates([](auto& value) { value.processAlive = false; }, StopReason::ProcessExited);
        invalidates([](auto& value) { value.windowExists = false; }, StopReason::TargetClosed);
        invalidates([](auto& value) { value.sameProcess = false; }, StopReason::WrongWindow);
        invalidates([](auto& value) { value.minimized = true; }, StopReason::Minimized);
        invalidates([](auto& value) { value.visible = false; }, StopReason::Hidden);
        invalidates([](auto& value) { value.sameSize = false; }, StopReason::SizeChanged);
        invalidates([](auto& value) { value.sameMonitor = false; }, StopReason::MonitorChanged);
        invalidates([](auto& value) { value.deviceHealthy = false; }, StopReason::DeviceLost);
        require(To100ns(150, 100) == 15000000);
        require(To100ns(333, 1000) == 3330000);
        require(DiagnosticDifference(120, 100) == 20);
        require(DiagnosticDifference(100, 120) == -20);
        require(DiagnosticDifference(std::numeric_limits<std::int64_t>::min(), 1) == std::numeric_limits<std::int64_t>::min());
        require(DiagnosticDifference(std::numeric_limits<std::int64_t>::max(), -1) == std::numeric_limits<std::int64_t>::max());
        std::cout << "{\"mode\":\"self-test\",\"passed\":" << tests
            << ",\"failed\":0,\"captureStarted\":false,\"graphicsResourcesCreated\":false}" << '\n';
        return 0;
    }

    void Help()
    {
        std::cout << "Wisp capture-only diagnostic. Default/help performs no capture.\n"
            "--self-test  Pure argument and lifecycle policy contracts; no graphics/WinRT initialization.\n"
            "--probe      WGC metadata, hardware adapter and H264 MFT enumeration only; not an encoding test.\n"
            "--capture --hwnd <integer> --pid <integer> --creation-time <UTC FILETIME integer>\n"
            "          [--seconds 1..60] [--buffers 2..3]\n"
            "             Explicit foreground ForzaHorizon6 window only; requires prior live-test readiness.\n"
            "          [--capture-hdr-metadata] Explicit game-window float16 metadata experiment.\n"
            "--fixture [--seconds 1..60] [--buffers 2..3] [--scenario normal|resize|minimize|close]\n"
            "             Captures only its own visible test window; refuses while Forza runs.\n"
            "          [--fixture-hdr-metadata] Explicit fixture-only float16 lifecycle experiment.\n"
            "             Neither HDR metadata option establishes color fidelity, encoding or performance.\n"
            "Capture stays native size, requested <=30fps; SDR BGRA unless its HDR metadata flag is explicit.\n"
            "No encoder, audio, scaling,\n"
            "preview, frame readback or image/video files. OS capture border remains enabled.\n"
            "Resize/minimize/close, game foreground loss, monitor/device loss or 2s without frames\n"
            "invalidates the trial. Exit0 means completed diagnostic only, not a gameplay performance pass.\n";
    }
}

int wmain(int argc, wchar_t** argv)
{
    try
    {
        const std::vector<std::wstring> values(argv + 1, argv + argc);
        const auto arguments = recorder::ParseArguments(values);
        if (arguments.mode == recorder::Mode::Help) { Help(); return 0; }
        if (arguments.mode == recorder::Mode::SelfTest) return SelfTest();
        RuntimeApartment apartment;
        const int result = arguments.mode == recorder::Mode::Probe ? Probe() : Capture(arguments);
        return result;
    }
    catch (const Unavailable& error)
    {
        std::cout << "{\"outcome\":\"unavailable\",\"reason\":\"" << error.reason << '"';
        if (error.hasSize) std::cout << ",\"width\":" << error.width << ",\"height\":" << error.height;
        std::cout << '}' << '\n';
        return 3;
    }
    catch (const std::invalid_argument& error)
    {
        std::cout << "{\"outcome\":\"invalid_arguments\",\"reason\":\"" << error.what() << "\"}" << '\n';
        return 4;
    }
    catch (const winrt::hresult_error& error)
    {
        std::cout << "{\"outcome\":\"failed\",\"hresult\":\"" << HrString(error.code()) << "\"}" << '\n';
        return 5;
    }
    catch (...)
    {
        std::cout << "{\"outcome\":\"failed\",\"reason\":\"internal_error\"}" << '\n';
        return 6;
    }
}
