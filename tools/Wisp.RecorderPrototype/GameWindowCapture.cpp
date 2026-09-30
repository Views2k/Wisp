#include "GameWindowCapture.h"
#include "ConversionOutput.h"

#include <d3d10_1.h>
#include <dxgi1_6.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cwchar>
#include <limits>
#include <mutex>
#include <new>
#include <vector>

namespace recorder::capture
{
    namespace
    {
        using namespace winrt::Windows::Graphics::Capture;
        using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;
        using winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
        using Clock = std::chrono::steady_clock;
        struct Failure { const char* reason; HRESULT hr; };
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_INVALIDARG }; }
        void CheckWin32(BOOL value, const char* reason)
        { if (!value) throw Failure{ reason, HRESULT_FROM_WIN32(GetLastError()) }; }
        void CheckDisplay(LONG code, const char* reason)
        { if (code != ERROR_SUCCESS) throw Failure{ reason, HRESULT_FROM_WIN32(code) }; }
        bool SameLuid(LUID a, LUID b) noexcept { return a.LowPart == b.LowPart && a.HighPart == b.HighPart; }

        struct Handle
        {
            HANDLE value = nullptr;
            ~Handle() { if (value) CloseHandle(value); }
            Handle() = default;
            Handle(const Handle&) = delete;
            Handle& operator=(const Handle&) = delete;
        };
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
                        result.whiteLevel = ReadWhiteLevel(monitor, result.luid);
                    return result;
                }
            }
            throw Failure{ "hardware_display_adapter_missing", E_FAIL };
        }

        struct State
        {
            TargetIdentity target{};
            Handle process;
            HMONITOR monitor = nullptr;
            RECT client{};
            SourceDescription source{};
            winrt::com_ptr<ID3D11Device> device;
            winrt::com_ptr<ID3D11DeviceContext> context;
            winrt::com_ptr<ID3D10Multithread> multithread;
            winrt::com_ptr<ID3D11Texture2D> latest;
            std::atomic<const char*> stopReason{ nullptr };
            std::atomic<HRESULT> hr{ S_OK };
            std::atomic<HRESULT> cleanupHr{ S_OK };
            std::atomic<bool> stopped{ false };
            std::atomic<std::uint64_t> version{ 0 }, emptyCallbacks{ 0 };
            std::atomic<LONGLONG> timestamp{ 0 };
            std::atomic<std::uint64_t> receivedQpc{ 0 };
            std::mutex callbackMutex;
            std::condition_variable callbacksFinished;
            UINT callbacks = 0;
            bool closing = false;

            void Stop(const char* reason, HRESULT error) noexcept
            {
                const char* expected = nullptr;
                if (stopReason.compare_exchange_strong(expected, reason))
                {
                    hr.store(error);
                    stopped.store(true);
                }
            }
            void CleanupFailure(HRESULT error) noexcept
            {
                HRESULT expected = S_OK;
                if (FAILED(error)) (void)cleanupHr.compare_exchange_strong(expected, error);
            }
        };
        struct CallbackScope
        {
            std::shared_ptr<State> state;
            bool entered = false;
            explicit CallbackScope(std::shared_ptr<State> item) : state(std::move(item))
            {
                std::lock_guard<std::mutex> lock(state->callbackMutex);
                if (!state->closing) { ++state->callbacks; entered = true; }
            }
            ~CallbackScope()
            {
                if (!entered) return;
                std::lock_guard<std::mutex> lock(state->callbackMutex);
                --state->callbacks;
                state->callbacksFinished.notify_all();
            }
        };

        void ValidateBasic(const State& state)
        {
            Require(WaitForSingleObject(state.process.value, 0) == WAIT_TIMEOUT, "target_process_exited");
            DWORD processId = 0;
            const DWORD thread = GetWindowThreadProcessId(state.target.window, &processId);
            Require(thread != 0 && IsWindow(state.target.window) && processId == state.target.processId &&
                GetAncestor(state.target.window, GA_ROOT) == state.target.window, "target_window_identity_changed");
            Require(IsWindowVisible(state.target.window) != FALSE, "target_window_hidden");
            Require(IsIconic(state.target.window) == FALSE, "target_window_minimized");
            Require(MonitorFromWindow(state.target.window, MONITOR_DEFAULTTONULL) == state.monitor, "target_monitor_changed");
            RECT client{};
            CheckWin32(GetClientRect(state.target.window, &client), "target_size_query_failed");
            Require(client.right - client.left == state.client.right - state.client.left &&
                client.bottom - client.top == state.client.bottom - state.client.top, "target_size_changed");
            if (state.device) Check(state.device->GetDeviceRemovedReason(), "capture_device_removed");
        }

        void FrameArrived(const std::weak_ptr<State>& weak, const Direct3D11CaptureFramePool& pool) noexcept
        {
            const auto state = weak.lock();
            if (!state) return;
            CallbackScope callback(state);
            if (!callback.entered || state->stopped.load()) return;
            Direct3D11CaptureFrame frame{ nullptr };
            try
            {
                frame = pool.TryGetNextFrame();
                if (!frame) { ++state->emptyCallbacks; return; }
                const auto size = frame.ContentSize();
                Require(size.Width == static_cast<int>(state->source.width) &&
                    size.Height == static_cast<int>(state->source.height), "capture_content_size_changed");
                const LONGLONG timestamp = frame.SystemRelativeTime().count();
                Require(timestamp > 0, "capture_timestamp_invalid");
                LARGE_INTEGER received{};
                CheckWin32(QueryPerformanceCounter(&received), "capture_receipt_clock_failed");
                Require(received.QuadPart > 0, "capture_receipt_clock_invalid");
                const auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                winrt::com_ptr<ID3D11Texture2D> surface;
                Check(access->GetInterface(__uuidof(ID3D11Texture2D), surface.put_void()), "capture_surface_unavailable");
                D3D11_TEXTURE2D_DESC description{};
                surface->GetDesc(&description);
                Require(description.Width == state->source.width && description.Height == state->source.height &&
                    description.Format == state->source.format && description.ArraySize == 1 && description.MipLevels == 1 &&
                    description.SampleDesc.Count == 1, "capture_surface_changed");
                winrt::com_ptr<ID3D11Device> surfaceDevice;
                surface->GetDevice(surfaceDevice.put());
                Require(surfaceDevice.as<IUnknown>() == state->device.as<IUnknown>(), "capture_surface_device_mismatch");
                {
                    GraphicsLock lock(state->multithread.get());
                    if (!state->stopped.load())
                    {
                        Require(timestamp > state->timestamp.load(), "capture_timestamp_not_increasing");
                        Require(state->version.load() != (std::numeric_limits<std::uint64_t>::max)(), "capture_version_exhausted");
                        state->context->CopyResource(state->latest.get(), surface.get());
                        Check(state->device->GetDeviceRemovedReason(), "capture_device_removed");
                        state->timestamp.store(timestamp);
                        state->receivedQpc.store(static_cast<std::uint64_t>(received.QuadPart));
                        ++state->version;
                    }
                }
                surface = nullptr;
                frame.Close();
                frame = nullptr;
            }
            catch (const Failure& error) { state->Stop(error.reason, error.hr); }
            catch (const winrt::hresult_error& error) { state->Stop("capture_frame_failed", error.code()); }
            catch (...) { state->Stop("capture_frame_failed", E_FAIL); }
            if (frame)
            {
                try { frame.Close(); }
                catch (const winrt::hresult_error& error)
                {
                    state->CleanupFailure(error.code());
                    state->Stop("capture_frame_close_failed", error.code());
                }
                catch (...)
                {
                    state->CleanupFailure(E_FAIL);
                    state->Stop("capture_frame_close_failed", E_FAIL);
                }
            }
        }
    }

    struct GameWindowCapture::Impl
    {
        DWORD ownerThread = GetCurrentThreadId();
        Options options{};
        Display display;
        std::shared_ptr<State> state;
        GraphicsCaptureItem item{ nullptr };
        Direct3D11CaptureFramePool pool{ nullptr };
        GraphicsCaptureSession session{ nullptr };
        winrt::event_token framesToken{}, closedToken{};
        bool framesSubscribed = false, closedSubscribed = false;
        Clock::time_point lastDisplayCheck = Clock::time_point::min();
    };

    GameWindowCapture::GameWindowCapture() noexcept = default;
    GameWindowCapture::~GameWindowCapture() { (void)Close(); }
    bool GameWindowCapture::Fail(const char* reason, HRESULT hr) noexcept
    {
        evidence_.reason = reason;
        evidence_.hr = hr;
        evidence_.stopped = true;
        if (impl_ && impl_->state) impl_->state->Stop(reason, hr);
        return false;
    }

    bool GameWindowCapture::Initialize(const TargetIdentity& target, const Options& options) noexcept
    {
        if (impl_ || closed_ || evidence_.stopped) return Fail("capture_not_fresh", E_UNEXPECTED);
        try
        {
            Require(target.window && target.processId && target.creationTime &&
                (options.frameRate == 30 || options.frameRate == 60), "capture_options_invalid");
            APTTYPE apartment{}; APTTYPEQUALIFIER qualifier{};
            Check(CoGetApartmentType(&apartment, &qualifier), "capture_apartment_uninitialized");
            Require(apartment == APTTYPE_MTA, "capture_requires_mta_worker");
            Require(GraphicsCaptureSession::IsSupported(), "windows_capture_unavailable");
            impl_ = std::make_unique<Impl>();
            auto& value = *impl_;
            value.options = options;
            value.state = std::make_shared<State>();
            auto& state = *value.state;
            state.target = target;
            state.process.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, target.processId);
            CheckWin32(state.process.value != nullptr, "target_process_open_failed");
            FILETIME creation{}, exit{}, kernel{}, user{};
            CheckWin32(GetProcessTimes(state.process.value, &creation, &exit, &kernel, &user), "target_process_time_failed");
            Require(((static_cast<std::uint64_t>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime) ==
                target.creationTime, "target_process_identity_mismatch");
            std::array<wchar_t, 32768> path{};
            DWORD length = static_cast<DWORD>(path.size());
            CheckWin32(QueryFullProcessImageNameW(state.process.value, 0, path.data(), &length), "target_process_name_failed");
            const wchar_t* name = wcsrchr(path.data(), L'\\');
            Require(_wcsicmp(name ? name + 1 : path.data(), L"ForzaHorizon6.exe") == 0, "target_is_not_fh6");
            state.monitor = MonitorFromWindow(target.window, MONITOR_DEFAULTTONULL);
            Require(state.monitor != nullptr, "target_monitor_missing");
            CheckWin32(GetClientRect(target.window, &state.client), "target_size_query_failed");
            Require(state.client.right > state.client.left && state.client.bottom > state.client.top, "target_size_invalid");
            ValidateBasic(state);
            value.display = SelectDisplay(state.monitor);
            const D3D_FEATURE_LEVEL requested[]{ D3D_FEATURE_LEVEL_11_0 };
            Check(D3D11CreateDevice(value.display.adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, 1, D3D11_SDK_VERSION, state.device.put(),
                nullptr, state.context.put()), "capture_device_creation_failed");
            state.multithread = state.device.as<ID3D10Multithread>();
            (void)state.multithread->SetMultithreadProtected(TRUE);
            Require(state.multithread->GetMultithreadProtected() != FALSE, "capture_multithread_protection_failed");
            const auto factory = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
            Check(factory->CreateForWindow(target.window, winrt::guid_of<GraphicsCaptureItem>(),
                reinterpret_cast<void**>(winrt::put_abi(value.item))), "capture_item_creation_failed");
            const auto size = value.item.Size();
            Require(size.Width > 0 && size.Height > 0, "capture_size_invalid");
            if (const char* reason = conversion::ValidateSourceGeometry(static_cast<UINT>(size.Width), static_cast<UINT>(size.Height)))
                throw Failure{ reason, E_INVALIDARG };
            source_.width = static_cast<UINT>(size.Width); source_.height = static_cast<UINT>(size.Height);
            source_.hdr = value.display.color == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            source_.format = source_.hdr ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_B8G8R8A8_UNORM;
            source_.referenceWhiteQueried = source_.hdr;
            source_.referenceWhiteNits = static_cast<float>(value.display.whiteLevel) * 80.0f / 1000.0f;
            state.source = source_;
            D3D11_TEXTURE2D_DESC texture{};
            texture.Width = source_.width; texture.Height = source_.height;
            texture.Format = source_.format; texture.ArraySize = 1; texture.MipLevels = 1;
            texture.SampleDesc.Count = 1; texture.Usage = D3D11_USAGE_DEFAULT;
            texture.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            Check(state.device->CreateTexture2D(&texture, nullptr, state.latest.put()), "capture_latest_texture_failed");
            const auto dxgi = state.device.as<IDXGIDevice>();
            winrt::com_ptr<IInspectable> projected;
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), projected.put()), "capture_projected_device_failed");
            value.pool = Direct3D11CaptureFramePool::CreateFreeThreaded(projected.as<IDirect3DDevice>(),
                source_.hdr ? DirectXPixelFormat::R16G16B16A16Float : DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            value.session = value.pool.CreateCaptureSession(value.item);
            const auto cursor = value.session.try_as<IGraphicsCaptureSession2>();
            const auto interval = value.session.try_as<IGraphicsCaptureSession5>();
            const auto secondary = value.session.try_as<IGraphicsCaptureSession6>();
            Require(cursor && interval && secondary, "required_capture_controls_unavailable");
            cursor.IsCursorCaptureEnabled(false);
            secondary.IncludeSecondaryWindows(false);
            const winrt::Windows::Foundation::TimeSpan cadence{ (10000000ll + options.frameRate - 1) / options.frameRate };
            interval.MinUpdateInterval(cadence);
            Require(!cursor.IsCursorCaptureEnabled() && !secondary.IncludeSecondaryWindows() &&
                interval.MinUpdateInterval() >= cadence, "capture_controls_not_applied");
            const std::weak_ptr<State> weak = value.state;
            value.framesToken = value.pool.FrameArrived([weak](const auto& pool, const auto&) { FrameArrived(weak, pool); });
            value.framesSubscribed = true;
            value.closedToken = value.item.Closed([weak](const auto&, const auto&)
            {
                if (const auto state = weak.lock())
                {
                    CallbackScope scope(state);
                    if (scope.entered) state->Stop("capture_item_closed", HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE));
                }
            });
            value.closedSubscribed = true;
            evidence_.initialized = true;
            evidence_.source = source_;
            evidence_.reason = "capture_initialized";
            if (!CheckTarget()) { (void)Close(); return false; }
            return true;
        }
        catch (const Failure& error) { Fail(error.reason, error.hr); }
        catch (const winrt::hresult_error& error) { Fail("capture_initialization_failed", error.code()); }
        catch (const std::bad_alloc&) { Fail("capture_allocation_failed", E_OUTOFMEMORY); }
        catch (...) { Fail("capture_initialization_failed", E_FAIL); }
        (void)Close();
        return false;
    }

    bool GameWindowCapture::Start() noexcept
    {
        if (!impl_ || !evidence_.initialized || evidence_.started || closed_)
            return Fail("capture_not_startable", E_UNEXPECTED);
        try
        {
            Require(impl_->ownerThread == GetCurrentThreadId(), "wrong_capture_worker");
            if (!CheckTarget()) return false;
            impl_->session.StartCapture();
            evidence_.started = true;
            evidence_.reason = "capture_started";
            return true;
        }
        catch (const Failure& error) { return Fail(error.reason, error.hr); }
        catch (const winrt::hresult_error& error) { return Fail("capture_start_failed", error.code()); }
        catch (...) { return Fail("capture_start_failed", E_FAIL); }
    }

    bool GameWindowCapture::CheckTarget() noexcept
    {
        if (!impl_ || !impl_->state || closed_) return Fail("capture_not_initialized", E_UNEXPECTED);
        try
        {
            auto& value = *impl_;
            Require(value.ownerThread == GetCurrentThreadId(), "wrong_capture_worker");
            if (value.state->stopped.load()) return false;
            ValidateBasic(*value.state);
            const auto now = Clock::now();
            if (value.lastDisplayCheck == Clock::time_point::min() ||
                now - value.lastDisplayCheck >= std::chrono::milliseconds(250))
            {
                Require(value.display.factory->IsCurrent() != FALSE, "display_configuration_changed");
                DXGI_OUTPUT_DESC1 description{};
                Check(value.display.output->GetDesc1(&description), "display_color_query_failed");
                Require(description.Monitor == value.state->monitor && description.ColorSpace == value.display.color,
                    "display_color_changed");
                if (source_.hdr)
                    Require(ReadWhiteLevel(value.state->monitor, value.display.luid) == value.display.whiteLevel,
                        "display_white_changed");
                value.lastDisplayCheck = now;
            }
            return true;
        }
        catch (const Failure& error) { return Fail(error.reason, error.hr); }
        catch (const winrt::hresult_error& error) { return Fail("capture_target_check_failed", error.code()); }
        catch (const std::bad_alloc&) { return Fail("capture_allocation_failed", E_OUTOFMEMORY); }
        catch (...) { return Fail("capture_target_check_failed", E_FAIL); }
    }

    ID3D11Device* GameWindowCapture::Device() const noexcept
    { return impl_ && impl_->state && !closed_ ? impl_->state->device.get() : nullptr; }
    const SourceDescription& GameWindowCapture::Source() const noexcept { return source_; }

    HRESULT GameWindowCapture::SubmitLatestLocked(ID3D11Texture2D* destination, FrameConsumer& consumer, FrameInfo& info) noexcept
    {
        info = {};
        if (!impl_ || !impl_->state || closed_ || !evidence_.started || impl_->ownerThread != GetCurrentThreadId())
            return E_UNEXPECTED;
        const auto state = impl_->state;
        if (state->stopped.load()) return FAILED(state->hr.load()) ? state->hr.load() : E_ABORT;
        if (!destination) return E_INVALIDARG;
        if (state->version.load() == 0) return S_FALSE;
        info.version = state->version.load();
        info.timestamp100ns = state->timestamp.load();
        info.receivedQpc = state->receivedQpc.load();
        const HRESULT hr = consumer.Submit(state->latest.get(), destination);
        if (FAILED(hr)) state->Stop("capture_conversion_failed", hr);
        return hr;
    }

    Evidence GameWindowCapture::Result() const noexcept
    {
        auto result = evidence_;
        if (impl_ && impl_->state)
        {
            const auto& state = *impl_->state;
            result.copiedFrames = state.version.load();
            result.emptyCallbacks = state.emptyCallbacks.load();
            if (SUCCEEDED(result.cleanupHr)) result.cleanupHr = state.cleanupHr.load();
            if (state.stopped.load())
            {
                result.stopped = true;
                result.reason = state.stopReason.load();
                result.hr = state.hr.load();
            }
        }
        return result;
    }

    HRESULT GameWindowCapture::Close() noexcept
    {
        if (closed_) return evidence_.cleanupHr;
        if (!impl_) { closed_ = true; evidence_.callbacksDrained = true; return evidence_.cleanupHr; }
        auto& value = *impl_;
        if (value.ownerThread != GetCurrentThreadId()) return E_UNEXPECTED;
        const auto remember = [this](HRESULT hr)
        { if (FAILED(hr) && SUCCEEDED(evidence_.cleanupHr)) evidence_.cleanupHr = hr; };
        const auto closeAction = [&remember](const auto& action)
        {
            try { action(); }
            catch (const winrt::hresult_error& error) { remember(error.code()); }
            catch (...) { remember(E_FAIL); }
        };
        if (value.state)
        {
            value.state->Stop("capture_stopped", S_OK);
            std::lock_guard<std::mutex> lock(value.state->callbackMutex);
            value.state->closing = true;
        }
        if (value.framesSubscribed) closeAction([&]() { value.pool.FrameArrived(value.framesToken); });
        if (value.closedSubscribed) closeAction([&]() { value.item.Closed(value.closedToken); });
        if (value.session) closeAction([&]() { value.session.Close(); });
        if (value.pool) closeAction([&]() { value.pool.Close(); });
        if (value.state)
        {
            std::unique_lock<std::mutex> lock(value.state->callbackMutex);
            evidence_.callbacksDrained = value.state->callbacksFinished.wait_for(lock, std::chrono::seconds(2),
                [&]() { return value.state->callbacks == 0; });
            if (!evidence_.callbacksDrained) remember(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
        }
        evidence_ = Result();
        value.session = nullptr; value.pool = nullptr; value.item = nullptr;
        impl_.reset();
        closed_ = true;
        return evidence_.cleanupHr;
    }
}
