#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <tlhelp32.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <iostream>
#include <thread>
#include <vector>
#include "nvEncodeAPI.h"

namespace
{
    using Microsoft::WRL::ComPtr;
    struct Failure { const char* reason; std::uint32_t code; };
    void Require(bool ok, const char* reason)
    { if (!ok) throw Failure{ reason, static_cast<std::uint32_t>(E_FAIL) }; }
    void Check(HRESULT hr, const char* reason)
    { if (FAILED(hr)) throw Failure{ reason, static_cast<std::uint32_t>(hr) }; }
    void CheckNv(NVENCSTATUS status, const char* reason)
    { if (status != NV_ENC_SUCCESS) throw Failure{ reason, static_cast<std::uint32_t>(status) }; }

    class Handle
    {
    public:
        explicit Handle(HANDLE value) : value_(value) {}
        ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        HANDLE Get() const { return value_; }
    private:
        HANDLE value_;
    };

    void RequireApplicationsClosed()
    {
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
        Check(snapshot.Get() == INVALID_HANDLE_VALUE ? HRESULT_FROM_WIN32(GetLastError()) : S_OK,
            "process_enumeration_failed");
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        Check(Process32FirstW(snapshot.Get(), &entry) ? S_OK : HRESULT_FROM_WIN32(GetLastError()),
            "process_enumeration_failed");
        do
        {
            Require(_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"Wisp.exe") != 0, "close_forza_and_wisp_before_probe");
        } while (Process32NextW(snapshot.Get(), &entry));
        Check(GetLastError() == ERROR_NO_MORE_FILES ? S_OK : HRESULT_FROM_WIN32(GetLastError()),
            "process_enumeration_failed");
    }

    class Deadline
    {
    public:
        Deadline() : finished_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
        {
            Require(finished_.Get() != nullptr, "watchdog_event_failed");
            thread_ = std::thread([this]
            {
                if (WaitForSingleObject(finished_.Get(), 15000) != WAIT_OBJECT_0)
                {
                    // A wedged driver call must not leave a diagnostic running indefinitely.
                    TerminateProcess(GetCurrentProcess(), 14);
                }
            });
        }
        ~Deadline() { SetEvent(finished_.Get()); if (thread_.joinable()) thread_.join(); }
        Deadline(const Deadline&) = delete;
        Deadline& operator=(const Deadline&) = delete;
    private:
        Handle finished_;
        std::thread thread_;
    };

    template<class T> T Export(HMODULE module, const char* name)
    {
        const FARPROC address = GetProcAddress(module, name);
        Require(address != nullptr, "driver_api_export_missing");
        T result{};
        static_assert(sizeof(result) == sizeof(address));
        std::memcpy(&result, &address, sizeof(result));
        return result;
    }

    struct NvSession
    {
        HMODULE module = nullptr;
        NV_ENCODE_API_FUNCTION_LIST api{};
        void* session = nullptr;
        NVENCSTATUS Close() noexcept
        {
            const NVENCSTATUS status = session && api.nvEncDestroyEncoder ? api.nvEncDestroyEncoder(session) : NV_ENC_SUCCESS;
            session = nullptr;
            return status;
        }
        ~NvSession() { Close(); if (module) FreeLibrary(module); }
    };

    struct Capability { const char* name; NVENCSTATUS status; int value; };
    Capability Query(NvSession& nv, GUID codec, const char* name, NV_ENC_CAPS key)
    {
        NV_ENC_CAPS_PARAM params{};
        params.version = NV_ENC_CAPS_PARAM_VER;
        params.capsToQuery = key;
        int value = 0;
        const NVENCSTATUS status = nv.api.nvEncGetEncodeCaps(nv.session, codec, &params, &value);
        return { name, status, value };
    }

    struct CodecEvidence
    {
        const char* name = "";
        bool present = false, fullChromaProfile = false;
        bool inputNv12 = false, input444 = false, input444TenBit = false;
        bool inputBgra = false, inputRgba = false, inputRgbTenBit = false, inputAyuv = false;
        std::vector<Capability> capabilities;
        NVENCSTATUS presetStatus = NV_ENC_ERR_UNSUPPORTED_PARAM;
        NV_ENC_PRESET_CONFIG preset{};
    };

    CodecEvidence Inspect(NvSession& nv, GUID codec, GUID fullChromaProfile,
        const char* name, const std::vector<GUID>& available)
    {
        RequireApplicationsClosed();
        CodecEvidence result;
        result.name = name;
        result.present = std::find(available.begin(), available.end(), codec) != available.end();
        if (!result.present) return result;
        result.capabilities = {
            Query(nv, codec, "lossless", NV_ENC_CAPS_SUPPORT_LOSSLESS_ENCODE),
            Query(nv, codec, "fullChroma", NV_ENC_CAPS_SUPPORT_YUV444_ENCODE),
            Query(nv, codec, "tenBit", NV_ENC_CAPS_SUPPORT_10BIT_ENCODE),
            Query(nv, codec, "asyncEncode", NV_ENC_CAPS_ASYNC_ENCODE_SUPPORT),
            Query(nv, codec, "maximumWidth", NV_ENC_CAPS_WIDTH_MAX),
            Query(nv, codec, "maximumHeight", NV_ENC_CAPS_HEIGHT_MAX)
        };
        std::uint32_t count = 0, written = 0;
        CheckNv(nv.api.nvEncGetEncodeProfileGUIDCount(nv.session, codec, &count), "profile_count_failed");
        Require(count > 0 && count <= 64, "profile_count_outside_bound");
        std::vector<GUID> profiles(count);
        CheckNv(nv.api.nvEncGetEncodeProfileGUIDs(nv.session, codec, profiles.data(), count, &written), "profiles_failed");
        Require(written <= count, "profile_result_outside_bound");
        profiles.resize(written);
        result.fullChromaProfile = std::find(profiles.begin(), profiles.end(), fullChromaProfile) != profiles.end();
        count = written = 0;
        CheckNv(nv.api.nvEncGetInputFormatCount(nv.session, codec, &count), "input_format_count_failed");
        Require(count > 0 && count <= 64, "input_format_count_outside_bound");
        std::vector<NV_ENC_BUFFER_FORMAT> formats(count);
        CheckNv(nv.api.nvEncGetInputFormats(nv.session, codec, formats.data(), count, &written), "input_formats_failed");
        Require(written <= count, "input_format_result_outside_bound");
        formats.resize(written);
        const auto has = [&](NV_ENC_BUFFER_FORMAT format) { return std::find(formats.begin(), formats.end(), format) != formats.end(); };
        result.inputNv12 = has(NV_ENC_BUFFER_FORMAT_NV12);
        result.input444 = has(NV_ENC_BUFFER_FORMAT_YUV444);
        result.input444TenBit = has(NV_ENC_BUFFER_FORMAT_YUV444_10BIT);
        result.inputBgra = has(NV_ENC_BUFFER_FORMAT_ARGB);
        result.inputRgba = has(NV_ENC_BUFFER_FORMAT_ABGR);
        result.inputRgbTenBit = has(NV_ENC_BUFFER_FORMAT_ARGB10) || has(NV_ENC_BUFFER_FORMAT_ABGR10);
        result.inputAyuv = has(NV_ENC_BUFFER_FORMAT_AYUV);
        result.preset.version = NV_ENC_PRESET_CONFIG_VER;
        result.preset.presetCfg.version = NV_ENC_CONFIG_VER;
        result.presetStatus = nv.api.nvEncGetEncodePresetConfigEx(nv.session, codec,
            NV_ENC_PRESET_P1_GUID, NV_ENC_TUNING_INFO_LOSSLESS, &result.preset);
        return result;
    }

    void Print(const CodecEvidence& value)
    {
        std::cout << "{\"codec\":\"" << value.name << "\",\"present\":" << value.present;
        if (value.present)
        {
            std::cout << ",\"fullChromaProfile\":" << value.fullChromaProfile
                << ",\"nv12Input\":" << value.inputNv12 << ",\"yuv444Input\":" << value.input444
                << ",\"yuv444TenBitInput\":" << value.input444TenBit << ",\"bgraInput\":" << value.inputBgra
                << ",\"rgbaInput\":" << value.inputRgba << ",\"rgbTenBitInput\":" << value.inputRgbTenBit
                << ",\"ayuvInput\":" << value.inputAyuv
                << ",\"capabilities\":[";
            bool comma = false;
            for (const auto& cap : value.capabilities)
            {
                if (comma) std::cout << ',';
                comma = true;
                std::cout << "{\"name\":\"" << cap.name << "\",\"status\":" << static_cast<unsigned>(cap.status) << ",\"value\":";
                if (cap.status == NV_ENC_SUCCESS) std::cout << cap.value;
                else std::cout << "null";
                std::cout << '}';
            }
            std::cout << "],\"losslessP1PresetStatus\":" << static_cast<unsigned>(value.presetStatus);
            if (value.presetStatus == NV_ENC_SUCCESS)
            {
                const auto& cfg = value.preset.presetCfg;
                std::cout << ",\"presetRateControl\":" << static_cast<unsigned>(cfg.rcParams.rateControlMode)
                    << ",\"presetQpI\":" << cfg.rcParams.constQP.qpIntra
                    << ",\"presetQpP\":" << cfg.rcParams.constQP.qpInterP
                    << ",\"presetQpB\":" << cfg.rcParams.constQP.qpInterB
                    << ",\"presetFrameIntervalP\":" << cfg.frameIntervalP;
                if (std::strcmp(value.name, "h264") == 0)
                    std::cout << ",\"presetTransformBypass\":" << (cfg.encodeCodecConfig.h264Config.qpPrimeYZeroTransformBypassFlag != 0)
                        << ",\"presetChromaFormat\":" << cfg.encodeCodecConfig.h264Config.chromaFormatIDC;
                else std::cout << ",\"presetChromaFormat\":" << cfg.encodeCodecConfig.hevcConfig.chromaFormatIDC;
            }
        }
        std::cout << '}';
    }

    void Probe(UINT adapterIndex)
    {
        RequireApplicationsClosed();
        Deadline deadline;
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "dxgi_factory_failed");
        ComPtr<IDXGIAdapter1> adapter;
        Check(factory->EnumAdapters1(adapterIndex, &adapter), "adapter_unavailable");
        DXGI_ADAPTER_DESC1 description{};
        Check(adapter->GetDesc1(&description), "adapter_description_failed");
        Require(description.VendorId == 0x10de && !(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE),
            "selected_adapter_is_not_nvidia_hardware");
        ComPtr<ID3D11Device> device;
        const D3D_FEATURE_LEVEL requested[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            requested, static_cast<UINT>(std::size(requested)), D3D11_SDK_VERSION,
            &device, nullptr, nullptr), "d3d_device_failed");
        RequireApplicationsClosed();
        NvSession nv;
        nv.module = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(nv.module != nullptr, "nvidia_driver_encode_library_unavailable");
        using GetVersion = NVENCSTATUS(NVENCAPI*)(std::uint32_t*);
        using CreateApi = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
        std::uint32_t maxVersion = 0;
        CheckNv(Export<GetVersion>(nv.module, "NvEncodeAPIGetMaxSupportedVersion")(&maxVersion), "driver_api_version_failed");
        Require(maxVersion >= ((NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION), "driver_older_than_probe_api");
        nv.api.version = NV_ENCODE_API_FUNCTION_LIST_VER;
        CheckNv(Export<CreateApi>(nv.module, "NvEncodeAPICreateInstance")(&nv.api), "driver_api_creation_failed");
        Require(nv.api.nvEncOpenEncodeSessionEx && nv.api.nvEncDestroyEncoder && nv.api.nvEncGetEncodeGUIDCount &&
            nv.api.nvEncGetEncodeGUIDs && nv.api.nvEncGetEncodeCaps && nv.api.nvEncGetEncodeProfileGUIDCount &&
            nv.api.nvEncGetEncodeProfileGUIDs && nv.api.nvEncGetInputFormatCount && nv.api.nvEncGetInputFormats &&
            nv.api.nvEncGetEncodePresetConfigEx, "driver_api_function_missing");
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS params{};
        params.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        params.apiVersion = NVENCAPI_VERSION;
        params.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
        params.device = device.Get();
        CheckNv(nv.api.nvEncOpenEncodeSessionEx(&params, &nv.session), "encode_session_open_failed");
        Require(nv.session != nullptr, "encode_session_missing");
        std::uint32_t count = 0, written = 0;
        CheckNv(nv.api.nvEncGetEncodeGUIDCount(nv.session, &count), "codec_count_failed");
        Require(count > 0 && count <= 16, "codec_count_outside_bound");
        std::vector<GUID> codecs(count);
        CheckNv(nv.api.nvEncGetEncodeGUIDs(nv.session, codecs.data(), count, &written), "codecs_failed");
        Require(written <= count, "codec_result_outside_bound");
        codecs.resize(written);
        const auto h264 = Inspect(nv, NV_ENC_CODEC_H264_GUID, NV_ENC_H264_PROFILE_HIGH_444_GUID, "h264", codecs);
        const auto hevc = Inspect(nv, NV_ENC_CODEC_HEVC_GUID, NV_ENC_HEVC_PROFILE_FREXT_GUID, "hevc", codecs);
        RequireApplicationsClosed();
        CheckNv(nv.Close(), "session_cleanup_failed");
        std::cout << std::boolalpha << "{\"completed\":true,\"mode\":\"capability_only\",\"adapterIndex\":" << adapterIndex
            << ",\"probeApiMajor\":" << NVENCAPI_MAJOR_VERSION << ",\"probeApiMinor\":" << NVENCAPI_MINOR_VERSION
            << ",\"maximumApiVersion\":" << maxVersion
            << ",\"encoderInitialized\":false,\"framesSubmitted\":0,\"captureStarted\":false,\"filesWritten\":false"
            << ",\"fp16InputDefinedInApi\":false,\"pixelEqualityVerified\":false,\"playbackVerified\":false,\"codecs\":[";
        Print(h264); std::cout << ','; Print(hevc); std::cout << "]}\n";
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && std::wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "Wisp NVENC lossless capability probe. --probe [--adapter-index 0..15]\n"
            "Requires Forza and Wisp closed. Headless, 15-second watchdog.\n"
            "Opens a driver session only; no encoder initialization, frames, capture, audio, output files or settings changes.\n"
            "Reports capabilities, not pixel fidelity, gameplay performance or playback compatibility.\n";
        return 0;
    }
    UINT adapterIndex = 0;
    if ((argc != 2 && argc != 4) || std::wcscmp(argv[1], L"--probe") != 0)
    { std::cout << "{\"completed\":false,\"reason\":\"invalid_arguments\"}\n"; return 2; }
    if (argc == 4)
    {
        if (std::wcscmp(argv[2], L"--adapter-index") != 0 || !*argv[3])
        { std::cout << "{\"completed\":false,\"reason\":\"invalid_arguments\"}\n"; return 2; }
        for (const wchar_t* digit = argv[3]; *digit; ++digit)
        {
            if (*digit < L'0' || *digit > L'9' || adapterIndex > 1)
            { std::cout << "{\"completed\":false,\"reason\":\"invalid_adapter_index\"}\n"; return 2; }
            adapterIndex = adapterIndex * 10 + static_cast<UINT>(*digit - L'0');
        }
        if (adapterIndex > 15)
        { std::cout << "{\"completed\":false,\"reason\":\"invalid_adapter_index\"}\n"; return 2; }
    }
    try { Probe(adapterIndex); return 0; }
    catch (const Failure& error)
    { std::cout << "{\"completed\":false,\"reason\":\"" << error.reason << "\",\"code\":" << error.code << "}\n"; }
    catch (...)
    { std::cout << "{\"completed\":false,\"reason\":\"unexpected_probe_failure\"}\n"; }
    return 1;
}
