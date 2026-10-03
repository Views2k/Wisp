#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <DirectXPackedVector.h>
#include <tlhelp32.h>
#include <VersionHelpers.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

// Diagnostic DLL only. Never linked into Wisp or the recorder. The only CPU
// readback is 32 owned 4x4 patch interiors; no desktop-sized image is mapped.
namespace
{
    using Microsoft::WRL::ComPtr;
    constexpr UINT Count = 32, Tile = 4, Samples = 3;
    struct Failure { int code; };
    void Need(bool value, int code) { if (!value) throw Failure{code}; }
    void Check(HRESULT hr, int code) { Need(SUCCEEDED(hr), code); }
    struct Handle
    {
        HANDLE value;
        ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    };
    struct PhysicalCoordinates
    {
        DPI_AWARENESS_CONTEXT previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        PhysicalCoordinates() { Need(previous != nullptr, 3); }
        ~PhysicalCoordinates() { (void)SetThreadDpiAwarenessContext(previous); }
    };
    void AppsClosed()
    {
        Handle snapshot{CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)};
        Need(snapshot.value != INVALID_HANDLE_VALUE, 10);
        PROCESSENTRY32W item{}; item.dwSize = sizeof(item);
        Need(Process32FirstW(snapshot.value, &item) != FALSE, 10);
        do
        {
            Need(_wcsicmp(item.szExeFile, L"Wisp.exe") != 0 &&
                _wcsicmp(item.szExeFile, L"Wisp.Recorder.exe") != 0 &&
                _wcsicmp(item.szExeFile, L"ForzaHorizon6.exe") != 0 &&
                _wcsicmp(item.szExeFile, L"ForzaHorizon5.exe") != 0 &&
                _wcsicmp(item.szExeFile, L"ForzaHorizon4.exe") != 0 &&
                _wcsicmp(item.szExeFile, L"ForzaMotorsport.exe") != 0, 11);
        } while (Process32NextW(snapshot.value, &item));
        Need(GetLastError() == ERROR_NO_MORE_FILES, 10);
    }
    bool Same(const RECT& a, const RECT& b)
    { return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom; }
    struct Display
    {
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<IDXGIOutput6> output;
        DXGI_OUTPUT_DESC1 desc{};
    };
    Display Find(HMONITOR preferred, bool requirePreferred)
    {
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), 20);
        Display fallback;
        for (UINT a = 0; a < 32; ++a)
        {
            ComPtr<IDXGIAdapter1> adapter;
            const HRESULT ah = factory->EnumAdapters1(a, &adapter);
            if (ah == DXGI_ERROR_NOT_FOUND) break;
            Check(ah, 21);
            for (UINT o = 0; o < 32; ++o)
            {
                ComPtr<IDXGIOutput> base;
                const HRESULT oh = adapter->EnumOutputs(o, &base);
                if (oh == DXGI_ERROR_NOT_FOUND) break;
                Check(oh, 22);
                ComPtr<IDXGIOutput6> output;
                if (FAILED(base.As(&output))) continue;
                DXGI_OUTPUT_DESC1 desc{};
                Check(output->GetDesc1(&desc), 23);
                if (!desc.AttachedToDesktop || desc.Rotation != DXGI_MODE_ROTATION_IDENTITY ||
                    desc.ColorSpace != DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020) continue;
                Display found{adapter, output, desc};
                if (desc.Monitor == preferred) return found;
                if (!fallback.output) fallback = found;
            }
        }
        Need(!requirePreferred && fallback.output, 24);
        return fallback;
    }
    void Guard(HWND owner, HWND foreground, const RECT& original, const POINT* centers,
        const DXGI_OUTPUT_DESC1& display)
    {
        Need(owner && foreground && GetForegroundWindow() == foreground, 30);
        DWORD pid = 0; GetWindowThreadProcessId(owner, &pid);
        Need(pid == GetCurrentProcessId() && GetAncestor(owner, GA_ROOT) == owner &&
            IsWindowVisible(owner) && !IsIconic(owner), 31);
        RECT bounds{}; Need(GetWindowRect(owner, &bounds) && Same(bounds, original), 32);
        Need(bounds.left >= display.DesktopCoordinates.left && bounds.top >= display.DesktopCoordinates.top &&
            bounds.right <= display.DesktopCoordinates.right && bounds.bottom <= display.DesktopCoordinates.bottom, 33);
        RECT client{}; POINT origin{};
        Need(GetClientRect(owner, &client) && ClientToScreen(owner, &origin), 34);
        for (UINT i = 0; i < Count; ++i)
        {
            const LONG x = centers[i].x, y = centers[i].y;
            Need(x >= origin.x + 8 && y >= origin.y + 8 &&
                static_cast<long long>(x) + 8 < static_cast<long long>(origin.x) + client.right &&
                static_cast<long long>(y) + 8 < static_cast<long long>(origin.y) + client.bottom, 35);
            // Check every sampled pixel and a surrounding occlusion margin.
            for (LONG dy = -8; dy <= 8; ++dy) for (LONG dx = -8; dx <= 8; ++dx)
                Need(GetAncestor(WindowFromPoint({x + dx, y + dy}), GA_ROOT) == owner, 36);
            for (UINT j = 0; j < i; ++j)
                Need(std::abs(static_cast<long long>(x) - centers[j].x) >= 16 ||
                    std::abs(static_cast<long long>(y) - centers[j].y) >= 16, 37);
        }
        AppsClosed();
    }
    struct HeldFrame
    {
        IDXGIOutputDuplication* duplication;
        bool held = false;
        ~HeldFrame() { if (held) (void)duplication->ReleaseFrame(); }
        void Release() { if (held) { held = false; Check(duplication->ReleaseFrame(), 48); } }
    };
}

extern "C" __declspec(dllexport) UINT __cdecl WispPlaybackWindows10Gate() noexcept
{
    return IsWindows10OrGreater() ? 1u : 0u;
}

extern "C" __declspec(dllexport) int __cdecl WispFindPlaybackHdrMonitor(HWND foreground, RECT* bounds) noexcept
{
    try
    {
        PhysicalCoordinates coordinates;
        Need(bounds && foreground && foreground == GetForegroundWindow(), 1);
        AppsClosed();
        *bounds = Find(MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST), false).desc.DesktopCoordinates;
        return 0;
    }
    catch (const Failure& e) { return e.code; }
    catch (...) { return 99; }
}

extern "C" __declspec(dllexport) int __cdecl WispReadPlaybackTiles(HWND owner, HWND foreground,
    const POINT* centers, UINT count, float* means, UINT* metadata) noexcept
{
    try
    {
        PhysicalCoordinates coordinates;
        Need(centers && means && metadata && count == Count, 1);
        std::fill(means, means + Count * 3, 0.0f);
        std::fill(metadata, metadata + 3, 0u);
        RECT original{}; Need(GetWindowRect(owner, &original) != FALSE, 2);
        const auto display = Find(MonitorFromWindow(owner, MONITOR_DEFAULTTONULL), true);
        Guard(owner, foreground, original, centers, display.desc);
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
        Check(D3D11CreateDevice(display.adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 1, D3D11_SDK_VERSION, &device, nullptr, &context), 40);
        ComPtr<IDXGIOutputDuplication> duplication;
        const DXGI_FORMAT format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        Check(display.output->DuplicateOutput1(device.Get(), 0, 1, &format, &duplication), 41);
        DXGI_OUTDUPL_DESC dup{}; duplication->GetDesc(&dup);
        Need(dup.ModeDesc.Format == format && dup.Rotation == DXGI_MODE_ROTATION_IDENTITY &&
            dup.ModeDesc.Width == static_cast<UINT>(display.desc.DesktopCoordinates.right - display.desc.DesktopCoordinates.left) &&
            dup.ModeDesc.Height == static_cast<UINT>(display.desc.DesktopCoordinates.bottom - display.desc.DesktopCoordinates.top), 42);
        D3D11_TEXTURE2D_DESC stage{};
        stage.Width = Count * Tile; stage.Height = Tile; stage.MipLevels = 1; stage.ArraySize = 1;
        stage.Format = format; stage.SampleDesc.Count = 1; stage.Usage = D3D11_USAGE_STAGING;
        stage.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;
        Check(device->CreateTexture2D(&stage, nullptr, &staging), 43);
        LARGE_INTEGER began{}; Need(QueryPerformanceCounter(&began), 44);
        const auto deadline = GetTickCount64() + 5000;
        LONGLONG previous = began.QuadPart;
        std::array<double, Count * 3> totals{};
        UINT samples = 0;
        while (samples < Samples)
        {
            Need(GetTickCount64() < deadline, 45);
            Guard(owner, foreground, original, centers, display.desc);
            DXGI_OUTPUT_DESC1 current{}; Check(display.output->GetDesc1(&current), 23);
            Need(current.ColorSpace == display.desc.ColorSpace && current.Monitor == display.desc.Monitor &&
                Same(current.DesktopCoordinates, display.desc.DesktopCoordinates), 46);
            DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
            const HRESULT acquired = duplication->AcquireNextFrame(20, &info, &resource);
            if (acquired == DXGI_ERROR_WAIT_TIMEOUT) continue;
            Check(acquired, 47);
            HeldFrame frame{duplication.Get(), true};
            if (info.LastPresentTime.QuadPart <= previous) continue;
            Guard(owner, foreground, original, centers, display.desc);
            ComPtr<ID3D11Texture2D> source; Check(resource.As(&source), 49);
            D3D11_TEXTURE2D_DESC actual{}; source->GetDesc(&actual);
            Need(actual.Format == format && actual.Width == dup.ModeDesc.Width &&
                actual.Height == dup.ModeDesc.Height && actual.ArraySize == 1 && actual.SampleDesc.Count == 1, 50);
            for (UINT i = 0; i < Count; ++i)
            {
                const UINT x = static_cast<UINT>(centers[i].x - display.desc.DesktopCoordinates.left - 2);
                const UINT y = static_cast<UINT>(centers[i].y - display.desc.DesktopCoordinates.top - 2);
                const D3D11_BOX box{x, y, 0, x + Tile, y + Tile, 1};
                context->CopySubresourceRegion(staging.Get(), 0, i * Tile, 0, 0, source.Get(), 0, &box);
            }
            context->Flush();
            D3D11_MAPPED_SUBRESOURCE map{};
            for (;;)
            {
                Guard(owner, foreground, original, centers, display.desc);
                Need(GetTickCount64() < deadline, 45);
                const HRESULT mapped = context->Map(staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &map);
                if (mapped == DXGI_ERROR_WAS_STILL_DRAWING) { Sleep(1); continue; }
                Check(mapped, 51); break;
            }
            std::array<double, Count * 3> sample{};
            bool valid = map.pData && map.RowPitch >= Count * Tile * 8 && map.RowPitch <= 65536;
            if (valid)
                for (UINT i = 0; i < Count; ++i) for (UINT y = 0; y < Tile; ++y) for (UINT x = 0; x < Tile; ++x)
                {
                    const auto* pixel = reinterpret_cast<const DirectX::PackedVector::HALF*>(
                        static_cast<const BYTE*>(map.pData) + y * map.RowPitch + (i * Tile + x) * 8);
                    for (UINT c = 0; c < 3; ++c)
                    {
                        const float value = DirectX::PackedVector::XMConvertHalfToFloat(pixel[c]);
                        if (!std::isfinite(value) || value < -10 || value > 125) valid = false;
                        sample[i * 3 + c] += value / static_cast<double>(Tile * Tile);
                    }
                }
            context->Unmap(staging.Get(), 0);
            Guard(owner, foreground, original, centers, display.desc);
            Need(valid, 52);
            frame.Release();
            for (UINT i = 0; i < Count * 3; ++i) totals[i] += sample[i];
            previous = info.LastPresentTime.QuadPart; ++samples;
        }
        Guard(owner, foreground, original, centers, display.desc);
        for (UINT i = 0; i < Count * 3; ++i) means[i] = static_cast<float>(totals[i] / Samples);
        metadata[0] = static_cast<UINT>(format); metadata[1] = static_cast<UINT>(display.desc.ColorSpace); metadata[2] = samples;
        return 0;
    }
    catch (const Failure& e) { return e.code; }
    catch (...) { return 99; }
}
