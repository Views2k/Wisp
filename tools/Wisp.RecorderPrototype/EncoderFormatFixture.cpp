#include "HardwareEncoder.h"
#include <mfapi.h>
#include <dxgi1_2.h>
#include <d3d10.h>
#include <array>
#include <iostream>
#include <cwchar>

using Microsoft::WRL::ComPtr;

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "--format-fixture: five bounded hardware format negotiations; no frames, files or capture.\n";
        return 0;
    }
    if (argc != 2 || wcscmp(argv[1], L"--format-fixture") != 0) return 2;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 3;
    hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) { CoUninitialize(); return 3; }
    int result = 0;
    {
        ComPtr<IDXGIFactory1> factory;
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<ID3D10Multithread> multithread;
        if (SUCCEEDED(hr)) hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr)) hr = factory->EnumAdapters1(0, &adapter);
        const D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL selected{};
        if (SUCCEEDED(hr)) hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 2, D3D11_SDK_VERSION, &device, &selected, &context);
        if (SUCCEEDED(hr)) hr = device.As(&multithread);
        if (SUCCEEDED(hr)) (void)multithread->SetMultithreadProtected(TRUE);
        if (FAILED(hr)) result = 3;
        else
        {
            using namespace recorder::encoder;
            std::array<EncodeConfig, 5> cases{};
            const UINT chroma = MFVideoChromaSubsampling_MPEG2 | MFVideoChromaSubsampling_ProgressiveChroma;
            cases[1].chromaSiting = chroma;
            cases[2].frameRate = 60;
            cases[3].frameRate = 60; cases[3].bitrate = 16000000;
            cases[4] = cases[3]; cases[4].chromaSiting = chroma;
            std::cout << std::boolalpha << "{\"mode\":\"format_negotiation_only\",\"cases\":[";
            bool first = true;
            for (const auto& configuration : cases)
            {
                HardwareSession session;
                Evidence evidence;
                const bool accepted = InitializeHardwareH264(device.Get(), 0, configuration, session, evidence);
                const HRESULT closed = session.Close();
                if (!first) std::cout << ',';
                first = false;
                std::cout << "{\"frameRate\":" << configuration.frameRate << ",\"bitrate\":" << configuration.bitrate
                    << ",\"chromaSiting\":" << configuration.chromaSiting << ",\"accepted\":" << accepted
                    << ",\"reason\":\"" << evidence.reason << "\",\"hresult\":" << static_cast<UINT>(evidence.hr)
                    << ",\"cleanupHresult\":" << static_cast<UINT>(FAILED(evidence.cleanupHr) ? evidence.cleanupHr : closed)
                    << ",\"hardwareCandidates\":" << evidence.hardwareCandidates << '}';
                if (FAILED(closed) || FAILED(evidence.cleanupHr) || evidence.hardwareCandidates != 1)
                {
                    result = 4;
                    break;
                }
            }
            std::cout << "],\"captureUsed\":false,\"framesSubmitted\":0,\"filesWritten\":false}\n";
        }
    }
    const HRESULT stopped = MFShutdown();
    CoUninitialize();
    return FAILED(stopped) ? 5 : result;
}
