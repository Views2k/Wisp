#include "GpuFrameConverter.h"

#include <d3d10_1.h>
#include <dxgi1_2.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cwchar>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    using namespace recorder::conversion;
    using Microsoft::WRL::ComPtr;
    using Clock = std::chrono::steady_clock;
    constexpr UINT SourceWidth = 3840, SourceHeight = 2160;
    constexpr UINT Tolerance = 2;
    std::atomic<bool> cancelled{ false };
    struct Failure { const char* reason; HRESULT hr; };
    void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
    void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_FAIL }; }
    BOOL WINAPI Cancel(DWORD signal) noexcept
    {
        if (signal != CTRL_C_EVENT && signal != CTRL_BREAK_EVENT) return FALSE;
        cancelled.store(true);
        return TRUE;
    }
    bool ForzaRunning()
    {
        const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        Check(snapshot == INVALID_HANDLE_VALUE ? HRESULT_FROM_WIN32(GetLastError()) : S_OK, "process_enumeration_failed");
        struct Close { HANDLE value; ~Close() { CloseHandle(value); } } close{ snapshot };
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        Check(Process32FirstW(snapshot, &entry) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "process_enumeration_failed");
        do
        {
            if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0 || _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") == 0 || _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") == 0) return true;
        } while (Process32NextW(snapshot, &entry));
        Check(GetLastError() == ERROR_NO_MORE_FILES ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "process_enumeration_failed");
        return false;
    }
    enum class Mode { Help, SelfTest, Fixture };
    struct Options { Mode mode = Mode::Help; UINT adapter = 0; UINT timeoutMs = 10000; };
    bool Number(const wchar_t* text, UINT minimum, UINT maximum, UINT& result) noexcept
    {
        if (!text || !*text) return false;
        UINT value = 0;
        for (; *text; ++text)
        {
            if (*text < L'0' || *text > L'9') return false;
            const UINT digit = static_cast<UINT>(*text - L'0');
            if (value > (maximum - digit) / 10) return false;
            value = value * 10 + digit;
        }
        if (value < minimum || value > maximum) return false;
        result = value;
        return true;
    }
    bool Parse(int argc, const wchar_t* const* argv, Options& options) noexcept
    {
        options = {};
        if (argc == 1) return true;
        if (argc == 2 && wcscmp(argv[1], L"--help") == 0) return true;
        if (argc == 2 && wcscmp(argv[1], L"--self-test") == 0) { options.mode = Mode::SelfTest; return true; }
        if (argc < 2 || wcscmp(argv[1], L"--conversion-fixture") != 0) return false;
        options.mode = Mode::Fixture;
        UINT seen = 0;
        for (int i = 2; i < argc; i += 2)
        {
            if (i + 1 >= argc) return false;
            if (wcscmp(argv[i], L"--adapter-index") == 0)
            {
                if ((seen & 1) || !Number(argv[i + 1], 0, 15, options.adapter)) return false;
                seen |= 1;
            }
            else if (wcscmp(argv[i], L"--timeout-ms") == 0)
            {
                if ((seen & 2) || !Number(argv[i + 1], 1000, 30000, options.timeoutMs)) return false;
                seen |= 2;
            }
            else return false;
        }
        return true;
    }
    struct Color { BYTE red, green, blue; };
    constexpr std::array<Color, 8> Colors{{ {0,0,0}, {255,255,255}, {128,128,128}, {255,0,0},
        {0,255,0}, {0,0,255}, {255,255,0}, {255,0,255} }};
    struct Yuv { UINT y, u, v; };
    Yuv Reference(Color color)
    {
        // Microsoft Recommended 8-Bit YUV Formats, full-range RGB -> BT.709
        // limited YCbCr. This is a code-value matrix reference, not HDR mapping.
        const double r = color.red / 255.0, g = color.green / 255.0, b = color.blue / 255.0;
        const double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return { static_cast<UINT>(std::floor(16.0 + 219.0 * luma + 0.5)),
            static_cast<UINT>(std::floor(128.0 + 112.0 * (b - luma) / (1.0 - 0.0722) + 0.5)),
            static_cast<UINT>(std::floor(128.0 + 112.0 * (r - luma) / (1.0 - 0.2126) + 0.5)) };
    }
    UINT ColorAt(UINT patch, UINT frame) noexcept { return (patch + frame * 3) % static_cast<UINT>(Colors.size()); }
    void FillPattern(std::vector<BYTE>& pixels, UINT frame)
    {
        for (UINT y = 0; y < SourceHeight; ++y)
            for (UINT x = 0; x < SourceWidth; ++x)
            {
                const UINT patch = (y / (SourceHeight / 2)) * 4 + x / (SourceWidth / 4);
                const auto color = Colors[ColorAt(patch, frame)];
                const size_t offset = (static_cast<size_t>(y) * SourceWidth + x) * 4;
                pixels[offset] = color.blue;
                pixels[offset + 1] = color.green;
                pixels[offset + 2] = color.red;
                pixels[offset + 3] = 255;
            }
    }
    struct PatchResult
    {
        UINT frame = 0, patch = 0, color = 0, maxError = 0;
        double meanY = 0, meanU = 0, meanV = 0;
    };
    PatchResult CheckPatch(const D3D11_MAPPED_SUBRESOURCE& mapped, UINT frame, UINT patch)
    {
        PatchResult result;
        result.frame = frame; result.patch = patch; result.color = ColorAt(patch, frame);
        const auto expected = Reference(Colors[result.color]);
        // Centers are chroma aligned; 32x32 region is far from scaled boundaries.
        const UINT left = (patch % 4) * (OutputWidth / 4) + OutputWidth / 8 - 16;
        const UINT top = (patch / 4) * (OutputHeight / 2) + OutputHeight / 4 - 16;
        const auto* data = static_cast<const BYTE*>(mapped.pData);
        const auto error = [&](UINT observed, UINT reference)
        {
            result.maxError = (std::max)(result.maxError, observed > reference ? observed - reference : reference - observed);
        };
        for (UINT y = top; y < top + 32; ++y)
            for (UINT x = left; x < left + 32; ++x)
            {
                const UINT observed = data[static_cast<size_t>(y) * mapped.RowPitch + x];
                result.meanY += observed; error(observed, expected.y);
            }
        const size_t uvBase = static_cast<size_t>(mapped.RowPitch) * OutputHeight;
        for (UINT y = top / 2; y < (top + 32) / 2; ++y)
            for (UINT x = left; x < left + 32; x += 2)
            {
                const size_t offset = uvBase + static_cast<size_t>(y) * mapped.RowPitch + x;
                const UINT u = data[offset], v = data[offset + 1];
                result.meanU += u; result.meanV += v; error(u, expected.u); error(v, expected.v);
            }
        result.meanY /= 1024; result.meanU /= 256; result.meanV /= 256;
        return result;
    }
    UINT SelfTest()
    {
        UINT passed = 0;
        Options options;
        const wchar_t* defaults[]{ L"fixture" };
        if (!Parse(1, defaults, options) || options.mode != Mode::Help) return 0;
        ++passed;
        const wchar_t* valid[]{ L"fixture", L"--conversion-fixture", L"--adapter-index", L"15", L"--timeout-ms", L"30000" };
        if (!Parse(6, valid, options) || options.adapter != 15 || options.timeoutMs != 30000) return 0;
        ++passed;
        const wchar_t* invalid[][6]{ {L"fixture",L"--capture"}, {L"fixture",L"--conversion-fixture",L"--adapter-index",L"16"},
            {L"fixture",L"--conversion-fixture",L"--timeout-ms",L"999"}, {L"fixture",L"--conversion-fixture",L"--timeout-ms",L"30001"},
            {L"fixture",L"--conversion-fixture",L"--adapter-index",L"1",L"--adapter-index",L"1"},
            {L"fixture",L"--self-test",L"--conversion-fixture"}, {L"fixture",L"--conversion-fixture",L"--hdr"},
            {L"fixture",L"--conversion-fixture",L"--timeout-ms"}, {L"fixture",L"--conversion-fixture",L"--adapter-index",L"-1"},
            {L"fixture",L"--conversion-fixture",L"--adapter-index",L"1.5"} };
        for (const auto& args : invalid)
        {
            int argc = 0; while (argc < 6 && args[argc]) ++argc;
            if (Parse(argc, args, options)) return 0;
            ++passed;
        }
        if (ValidateSource(3840,2160,SourceEncoding::SdrBgraG22P709) ||
            ValidateSource(1920,1080,SourceEncoding::SdrBgraG22P709) ||
            ValidateSource(1920,1200,SourceEncoding::SdrBgraG22P709)) return 0;
        ++passed;
        if (!ValidateSource(3840,2160,SourceEncoding::LinearScRgbFp16) ||
            !ValidateSource(3840,2160,SourceEncoding::Unknown) || !ValidateSource(1921,1080,SourceEncoding::SdrBgraG22P709) ||
            !ValidateSource(7680,2160,SourceEncoding::SdrBgraG22P709)) return 0;
        ++passed;
        GpuFrameConverter converter;
        ConversionEvidence evidence;
        if (converter.Initialize(nullptr,3840,2160,SourceEncoding::LinearScRgbFp16,evidence) ||
            std::strcmp(evidence.reason,"hdr_tone_mapping_not_implemented") != 0) return 0;
        ++passed;
        const auto black = Reference(Colors[0]), white = Reference(Colors[1]), red = Reference(Colors[3]);
        if (black.y != 16 || black.u != 128 || black.v != 128 || white.y != 235 || white.u != 128 || white.v != 128 ||
            red.y != 63 || red.u != 102 || red.v != 240) return 0;
        ++passed;
        D3D11_TEXTURE2D_DESC input{}, output{};
        input.Width = SourceWidth; input.Height = SourceHeight; input.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        input.ArraySize = input.MipLevels = input.SampleDesc.Count = 1;
        output = input; output.Width = OutputWidth; output.Height = OutputHeight;
        output.Format = DXGI_FORMAT_NV12; output.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (ValidateSurfaces(input, output, SourceWidth, SourceHeight)) return 0;
        ++passed;
        input.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        if (!ValidateSurfaces(input, output, SourceWidth, SourceHeight)) return 0;
        ++passed;
        input.Format = DXGI_FORMAT_B8G8R8A8_UNORM; output.SampleDesc.Count = 2;
        if (!ValidateSurfaces(input, output, SourceWidth, SourceHeight)) return 0;
        ++passed;
        return passed;
    }

    int Run(const Options& options)
    {
        ConversionEvidence evidence;
        std::array<PatchResult, 16> patches{};
        UINT checkedPatches = 0, completedGpuFrames = 0, maximumError = 0;
        bool completed = false;
        const auto started = Clock::now();
        const auto deadline = started + std::chrono::milliseconds(options.timeoutMs);
        auto lastGameCheck = started - std::chrono::seconds(1);
        const auto guard = [&]()
        {
            Require(!cancelled.load(), "cancelled");
            Require(Clock::now() < deadline, "conversion_deadline_reached");
            if (Clock::now() - lastGameCheck >= std::chrono::milliseconds(100))
            {
                Require(!ForzaRunning(), "forza_running");
                lastGameCheck = Clock::now();
            }
        };
        try
        {
            guard();
            ComPtr<IDXGIFactory1> factory;
            Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "adapter_factory_failed");
            ComPtr<IDXGIAdapter1> adapter;
            Check(factory->EnumAdapters1(options.adapter, &adapter), "selected_adapter_unavailable");
            DXGI_ADAPTER_DESC1 description{};
            Check(adapter->GetDesc1(&description), "adapter_description_failed");
            Require(!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE), "software_adapter_refused");
            ComPtr<ID3D11Device> device;
            ComPtr<ID3D11DeviceContext> context;
            const D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
            D3D_FEATURE_LEVEL selected{};
            Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION, &device, &selected, &context), "hardware_device_creation_failed");
            ComPtr<ID3D10Multithread> multithread;
            Check(device.As(&multithread), "multithread_interface_missing");
            (void)multithread->SetMultithreadProtected(TRUE);
            GpuFrameConverter converter;
            if (!converter.Initialize(device.Get(), SourceWidth, SourceHeight, SourceEncoding::SdrBgraG22P709, evidence))
                throw Failure{ evidence.reason, evidence.hr };
            guard();
            D3D11_TEXTURE2D_DESC inputDescription{};
            inputDescription.Width = SourceWidth; inputDescription.Height = SourceHeight;
            inputDescription.MipLevels = inputDescription.ArraySize = inputDescription.SampleDesc.Count = 1;
            inputDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            inputDescription.Usage = D3D11_USAGE_DEFAULT;
            inputDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
            ComPtr<ID3D11Texture2D> input;
            Check(device->CreateTexture2D(&inputDescription, nullptr, &input), "synthetic_input_creation_failed");
            D3D11_TEXTURE2D_DESC outputDescription = inputDescription;
            outputDescription.Width = OutputWidth; outputDescription.Height = OutputHeight;
            outputDescription.Format = DXGI_FORMAT_NV12;
            ComPtr<ID3D11Texture2D> output;
            Check(device->CreateTexture2D(&outputDescription, nullptr, &output), "nv12_output_creation_failed");
            D3D11_TEXTURE2D_DESC stagingDescription = outputDescription;
            stagingDescription.Usage = D3D11_USAGE_STAGING;
            stagingDescription.BindFlags = 0;
            stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> staging;
            Check(device->CreateTexture2D(&stagingDescription, nullptr, &staging), "synthetic_readback_creation_failed");
            D3D11_QUERY_DESC queryDescription{ D3D11_QUERY_EVENT, 0 };
            ComPtr<ID3D11Query> completion;
            Check(device->CreateQuery(&queryDescription, &completion), "gpu_completion_query_failed");
            std::vector<BYTE> pixels(static_cast<size_t>(SourceWidth) * SourceHeight * 4);
            for (UINT frame = 0; frame < 2; ++frame)
            {
                guard();
                FillPattern(pixels, frame);
                context->UpdateSubresource(input.Get(), 0, nullptr, pixels.data(), SourceWidth * 4, 0);
                if (!converter.Submit(input.Get(), output.Get(), frame, evidence)) throw Failure{ evidence.reason, evidence.hr };
                // Readback is confined to generated fixture pixels; the reusable
                // converter has no staging resource, Map or CPU conversion path.
                context->CopyResource(staging.Get(), output.Get());
                context->End(completion.Get());
                context->Flush();
                for (;;)
                {
                    guard();
                    Check(device->GetDeviceRemovedReason(), "d3d11_device_removed");
                    BOOL ready = FALSE;
                    const HRESULT hr = context->GetData(completion.Get(), &ready, sizeof(ready), D3D11_ASYNC_GETDATA_DONOTFLUSH);
                    Check(hr, "gpu_completion_query_failed");
                    if (hr == S_OK && ready) break;
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                }
                ++completedGpuFrames;
                D3D11_MAPPED_SUBRESOURCE mapped{};
                Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped), "synthetic_readback_map_failed");
                struct Unmap { ID3D11DeviceContext* context; ID3D11Texture2D* texture; ~Unmap() { context->Unmap(texture,0); } } unmap{ context.Get(), staging.Get() };
                Require(mapped.pData && mapped.RowPitch >= OutputWidth && mapped.RowPitch <= 65536, "nv12_readback_layout_invalid");
                for (UINT patch = 0; patch < 8; ++patch)
                {
                    const auto result = CheckPatch(mapped, frame, patch);
                    patches[checkedPatches++] = result;
                    maximumError = (std::max)(maximumError, result.maxError);
                }
            }
            Require(checkedPatches == 16 && completedGpuFrames == 2, "synthetic_conversion_incomplete");
            Require(maximumError <= Tolerance, "synthetic_code_values_outside_tolerance");
            guard();
            completed = true;
            evidence.reason = "synthetic_sdr_conversion_completed";
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - started).count();
        std::cout << std::boolalpha << std::fixed << std::setprecision(3)
            << "{\"mode\":\"synthetic_sdr_bgra_to_nv12\",\"completed\":" << completed
            << ",\"reason\":\"" << evidence.reason << "\",\"hresult\":" << static_cast<UINT>(evidence.hr)
            << ",\"sourceWidth\":" << SourceWidth << ",\"sourceHeight\":" << SourceHeight
            << ",\"outputWidth\":" << OutputWidth << ",\"outputHeight\":" << OutputHeight
            << ",\"configuredMediaFrameRate\":" << MediaFrameRate << ",\"inputColor\":\"RGB_FULL_G22_NONE_P709\""
            << ",\"outputColor\":\"YCBCR_STUDIO_G22_LEFT_P709\",\"formatConversionSupported\":" << evidence.formatConversionSupported
            << ",\"colorStateVerified\":" << evidence.colorStateVerified << ",\"autoProcessingDisabled\":" << evidence.autoProcessingDisabled
            << ",\"submittedBlits\":" << evidence.submittedBlits << ",\"completedGpuFrames\":" << completedGpuFrames
            << ",\"checkedPatches\":" << checkedPatches << ",\"maximumCodeError\":" << maximumError
            << ",\"allowedCodeError\":" << Tolerance << ",\"elapsedMs\":" << elapsed
            << ",\"hdrSupported\":false,\"toneMappingUsed\":false,\"independentTransferCurveVerified\":false"
            << ",\"encoderColorContractReady\":false"
            << ",\"captureUsed\":false,\"softwareConversionFallback\":false,\"encoderIntegrationVerified\":false"
            << ",\"realtimePerformanceVerified\":false,\"patches\":[";
        for (UINT index = 0; index < checkedPatches; ++index)
        {
            if (index) std::cout << ',';
            const auto& patch = patches[index];
            const auto expected = Reference(Colors[patch.color]);
            std::cout << "{\"frame\":" << patch.frame << ",\"patch\":" << patch.patch << ",\"colorIndex\":" << patch.color
                << ",\"expectedY\":" << expected.y << ",\"expectedU\":" << expected.u << ",\"expectedV\":" << expected.v
                << ",\"meanY\":" << patch.meanY << ",\"meanU\":" << patch.meanU << ",\"meanV\":" << patch.meanV
                << ",\"maximumCodeError\":" << patch.maxError << '}';
        }
        std::cout << "]}\n";
        return completed ? 0 : 3;
    }
}

int wmain(int argc, wchar_t** argv)
{
    Options options;
    if (!Parse(argc, argv, options)) { std::cout << "{\"completed\":false,\"reason\":\"invalid_arguments\"}\n"; return 2; }
    if (options.mode == Mode::Help)
    {
        std::cout << "Synthetic SDR GPU conversion; no capture, files or windows.\n"
            "Default/help initialize no graphics resources. --self-test is CPU-only.\n"
            "--conversion-fixture [--adapter-index 0..15] [--timeout-ms 1000..30000]\n"
            "Defaults: adapter 0, 10000 ms; two generated 3840x2160 patterns -> 1920x1080 NV12.\n"
            "HDR/FP16 is unsupported; no tone-map, independent transfer-curve or performance claim.\n"
            "Requires Forza closed. Ctrl+C cancels. Use an external API-call watchdog.\n";
        return 0;
    }
    if (options.mode == Mode::SelfTest)
    {
        const UINT passed = SelfTest();
        std::cout << "{\"mode\":\"cpu_contracts\",\"passed\":" << passed << ",\"graphicsInitialized\":false}\n";
        return passed ? 0 : 1;
    }
    if (!SetConsoleCtrlHandler(Cancel, TRUE)) { std::cout << "{\"completed\":false,\"reason\":\"cancel_handler_failed\"}\n"; return 3; }
    const int result = Run(options);
    (void)SetConsoleCtrlHandler(Cancel, FALSE);
    return result;
}
