#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <iostream>
#include <string>
#include <thread>
#include <vector>
#include "nvEncodeAPI.h"

namespace
{
    using Microsoft::WRL::ComPtr;
    constexpr UINT Width = 1280, Height = 720, Frames = 16, Fps = 60;
    constexpr std::uint64_t OutputLimit = 128ull * 1024 * 1024;
    constexpr std::size_t FrameBytes = static_cast<std::size_t>(Width) * Height * 3;
    struct Failure { const char* reason; std::uint32_t code; };
    struct FrameFailure
    {
        const char* reason;
        UINT expectedFrame;
        std::uint64_t outputTimeStamp;
        UINT pictureType;
        UINT hwEncodeStatus;
        UINT frameIdx;
        std::uint64_t outputDuration;
        UINT bitstreamBytes;
        UINT averageQp;
    };
    void Require(bool ok, const char* reason)
    { if (!ok) throw Failure{ reason, static_cast<std::uint32_t>(E_FAIL) }; }
    void Win(bool ok, const char* reason)
    { if (!ok) throw Failure{ reason, GetLastError() }; }
    void Check(HRESULT hr, const char* reason)
    { if (FAILED(hr)) throw Failure{ reason, static_cast<std::uint32_t>(hr) }; }
    void Nv(NVENCSTATUS status, const char* reason)
    { if (status != NV_ENC_SUCCESS) throw Failure{ reason, static_cast<std::uint32_t>(status) }; }

    class Handle
    {
    public:
        explicit Handle(HANDLE value = nullptr) : value_(value) {}
        ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        Handle(Handle&& other) noexcept : value_(other.value_) { other.value_ = nullptr; }
        HANDLE Get() const { return value_; }
    private:
        HANDLE value_;
    };

    class Deadline
    {
    public:
        Deadline() : finished_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
        {
            Win(finished_.Get() != nullptr, "watchdog_event_failed");
            thread_ = std::thread([this]
            {
                if (WaitForSingleObject(finished_.Get(), 15000) != WAIT_OBJECT_0)
                    TerminateProcess(GetCurrentProcess(), 14);
            });
        }
        ~Deadline() { SetEvent(finished_.Get()); if (thread_.joinable()) thread_.join(); }
        Deadline(const Deadline&) = delete;
        Deadline& operator=(const Deadline&) = delete;
    private:
        Handle finished_;
        std::thread thread_;
    };

    void RequireApplicationsClosed()
    {
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
        Win(snapshot.Get() != INVALID_HANDLE_VALUE, "process_enumeration_failed");
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        Win(Process32FirstW(snapshot.Get(), &entry) != FALSE, "process_enumeration_failed");
        do
        {
            Require(_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"Wisp.exe") != 0, "close_forza_and_wisp_before_fixture");
        } while (Process32NextW(snapshot.Get(), &entry));
        Require(GetLastError() == ERROR_NO_MORE_FILES, "process_enumeration_failed");
    }

    // Hold every ancestor against rename/replacement. This fixture only creates a new local directory.
    std::wstring LocalPath(const wchar_t* supplied)
    {
        const std::wstring input(supplied);
        Require(input.size() > 3 && input.size() < 220 && input[1] == L':' && input[2] == L'\\' &&
            ((input[0] >= L'A' && input[0] <= L'Z') || (input[0] >= L'a' && input[0] <= L'z')) &&
            input.find_first_of(L"\r\n\"<>|?*") == std::wstring::npos && input.find(L':', 2) == std::wstring::npos,
            "absolute_local_path_required");
        wchar_t full[260]{};
        const DWORD count = GetFullPathNameW(input.c_str(), static_cast<DWORD>(std::size(full)), full, nullptr);
        Require(count > 3 && count < std::size(full), "path_resolution_failed");
        std::wstring result(full, count);
        Require(result.back() != L'\\' && result.back() != L'.' && result.back() != L' ', "invalid_path_leaf");
        Require(GetDriveTypeW(result.substr(0, 3).c_str()) == DRIVE_FIXED, "fixed_local_drive_required");
        return result;
    }

    std::vector<Handle> HoldParents(const std::wstring& path)
    {
        std::vector<Handle> held;
        for (std::size_t end = 2; end != std::wstring::npos; end = path.find(L'\\', end + 1))
        {
            const std::wstring part = path.substr(0, end == 2 ? 3 : end);
            Handle directory(CreateFileW(part.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            Win(directory.Get() != INVALID_HANDLE_VALUE, "parent_directory_open_failed");
            BY_HANDLE_FILE_INFORMATION info{};
            Win(GetFileInformationByHandle(directory.Get(), &info) != FALSE, "parent_directory_stat_failed");
            Require((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
                (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0, "reparse_or_non_directory_parent");
            held.push_back(std::move(directory));
        }
        return held;
    }

    std::array<BYTE, 3> Pixel(UINT frame, UINT x, UINT y)
    {
        const auto byte = [](UINT value) { return static_cast<BYTE>(value & 255u); };
        if (y < Height / 4)
            return { byte((x + frame * 3) % 32), byte((x / 2 + frame * 5) % 64), byte((x / 4 + frame * 7) % 128) };
        if (y < Height / 2)
            return { byte(x + frame * 11), byte(y + frame * 17), byte(x + y + frame * 23) };
        if (y < Height * 3 / 4)
            return { byte(((x + frame) & 1) * 255), byte(((y + frame) & 1) * 255), byte(((x + y + frame) & 1) * 255) };
        // Deterministic fine detail and motion, including every byte value; no captured pixels.
        UINT noise = (x + frame * 13) * 0x45d9f3bu ^ (y + frame * 7) * 0x119de1f3u;
        noise ^= noise >> 16;
        return { byte(noise), byte(noise >> 8), byte(noise >> 16) };
    }

    void GenerateRgb(UINT frame, std::vector<BYTE>& rgb)
    {
        rgb.resize(FrameBytes);
        for (UINT y = 0; y < Height; ++y)
            for (UINT x = 0; x < Width; ++x)
            {
                const auto pixel = Pixel(frame, x, y);
                const std::size_t offset = (static_cast<std::size_t>(y) * Width + x) * 3;
                std::copy(pixel.begin(), pixel.end(), rgb.begin() + offset);
            }
    }

    class GpuPattern
    {
    public:
        void Initialize(ID3D11Device* device)
        {
            device->GetImmediateContext(&context_);
            constexpr char shader[] = R"hlsl(
cbuffer Pattern : register(b0) { uint frame; uint width; uint height; uint reserved; };
float4 VS(uint vertex : SV_VertexID) : SV_Position
{
    float2 uv = float2((vertex << 1) & 2, vertex & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
uint4 PS(float4 position : SV_Position) : SV_Target
{
    uint x = (uint)position.x, y = (uint)position.y;
    uint3 rgb;
    if (y < height / 4)
        rgb = uint3((x + frame * 3) % 32, (x / 2 + frame * 5) % 64, (x / 4 + frame * 7) % 128);
    else if (y < height / 2)
        rgb = uint3(x + frame * 11, y + frame * 17, x + y + frame * 23) & 255;
    else if (y < height * 3 / 4)
        rgb = uint3(((x + frame) & 1) * 255, ((y + frame) & 1) * 255, ((x + y + frame) & 1) * 255);
    else
    {
        uint noise = (x + frame * 13) * 0x45d9f3b ^ (y + frame * 7) * 0x119de1f3;
        noise ^= noise >> 16;
        rgb = uint3(noise, noise >> 8, noise >> 16) & 255;
    }
    // AYUV typed-view channels are V,U,Y,A. Identity GBR needs Y=G,U=B,V=R.
    return uint4(rgb.r, rgb.b, rgb.g, 255);
}
)hlsl";
            ComPtr<ID3DBlob> vertexCode, pixelCode, errors;
            constexpr UINT flags = D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_WARNINGS_ARE_ERRORS | D3DCOMPILE_OPTIMIZATION_LEVEL3;
            Check(D3DCompile(shader, sizeof(shader) - 1, nullptr, nullptr, nullptr, "VS", "vs_5_0", flags, 0,
                &vertexCode, &errors), "gpu_pattern_vertex_compile_failed");
            errors.Reset();
            Check(D3DCompile(shader, sizeof(shader) - 1, nullptr, nullptr, nullptr, "PS", "ps_5_0", flags, 0,
                &pixelCode, &errors), "gpu_pattern_pixel_compile_failed");
            Check(device->CreateVertexShader(vertexCode->GetBufferPointer(), vertexCode->GetBufferSize(), nullptr, &vertex_),
                "gpu_pattern_vertex_create_failed");
            Check(device->CreatePixelShader(pixelCode->GetBufferPointer(), pixelCode->GetBufferSize(), nullptr, &pixel_),
                "gpu_pattern_pixel_create_failed");
            D3D11_BUFFER_DESC buffer{};
            buffer.ByteWidth = 16;
            buffer.Usage = D3D11_USAGE_DEFAULT;
            buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            Check(device->CreateBuffer(&buffer, nullptr, &constants_), "gpu_pattern_constants_failed");
            D3D11_RASTERIZER_DESC raster{};
            raster.FillMode = D3D11_FILL_SOLID;
            raster.CullMode = D3D11_CULL_NONE;
            raster.DepthClipEnable = TRUE;
            Check(device->CreateRasterizerState(&raster, &raster_), "gpu_pattern_rasterizer_failed");
            D3D11_DEPTH_STENCIL_DESC depth{};
            depth.DepthEnable = FALSE;
            depth.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
            depth.DepthFunc = D3D11_COMPARISON_ALWAYS;
            Check(device->CreateDepthStencilState(&depth, &depth_), "gpu_pattern_depth_state_failed");
            D3D11_QUERY_DESC query{};
            query.Query = D3D11_QUERY_EVENT;
            Check(device->CreateQuery(&query, &completed_), "gpu_pattern_query_failed");
        }

        void Fill(ID3D11Device* device, ID3D11Texture2D* texture, UINT frame)
        {
            D3D11_RENDER_TARGET_VIEW_DESC view{};
            view.Format = DXGI_FORMAT_R8G8B8A8_UINT;
            view.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11RenderTargetView> target;
            Check(device->CreateRenderTargetView(texture, &view, &target), "ayuv_integer_render_target_failed");
            const std::array<UINT, 4> constants{ frame, Width, Height, 0 };
            // Only four scalar constants are uploaded. Every encoded pixel is generated by the GPU.
            context_->UpdateSubresource(constants_.Get(), 0, nullptr, constants.data(), 0, 0);
            context_->IASetInputLayout(nullptr);
            context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context_->VSSetShader(vertex_.Get(), nullptr, 0);
            context_->PSSetShader(pixel_.Get(), nullptr, 0);
            ID3D11Buffer* constantBuffer = constants_.Get();
            context_->PSSetConstantBuffers(0, 1, &constantBuffer);
            const D3D11_VIEWPORT viewport{ 0, 0, static_cast<FLOAT>(Width), static_cast<FLOAT>(Height), 0, 1 };
            context_->RSSetViewports(1, &viewport);
            context_->RSSetState(raster_.Get());
            context_->OMSetDepthStencilState(depth_.Get(), 0);
            context_->OMSetBlendState(nullptr, nullptr, 0xffffffff);
            ID3D11RenderTargetView* renderTarget = target.Get();
            context_->OMSetRenderTargets(1, &renderTarget, nullptr);
            context_->Draw(3, 0);
            context_->OMSetRenderTargets(0, nullptr, nullptr);
            context_->End(completed_.Get());
            context_->Flush();
            const ULONGLONG started = GetTickCount64();
            BOOL finished = FALSE;
            for (;;)
            {
                const HRESULT status = context_->GetData(completed_.Get(), &finished, sizeof(finished), D3D11_ASYNC_GETDATA_DONOTFLUSH);
                Check(status, "gpu_pattern_completion_failed");
                if (status == S_OK && finished) break;
                Require(GetTickCount64() - started < 5000, "gpu_pattern_completion_timeout");
                Sleep(1);
            }
            Check(device->GetDeviceRemovedReason(), "gpu_pattern_device_removed");
        }
        ~GpuPattern() { if (context_) context_->ClearState(); }
    private:
        ComPtr<ID3D11DeviceContext> context_;
        ComPtr<ID3D11VertexShader> vertex_;
        ComPtr<ID3D11PixelShader> pixel_;
        ComPtr<ID3D11Buffer> constants_;
        ComPtr<ID3D11RasterizerState> raster_;
        ComPtr<ID3D11DepthStencilState> depth_;
        ComPtr<ID3D11Query> completed_;
    };

    std::string Sha256(std::vector<BYTE>& bytes)
    {
        std::array<BYTE, 32> hash{};
        Require(BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, bytes.data(), static_cast<ULONG>(bytes.size()),
            hash.data(), static_cast<ULONG>(hash.size())) >= 0, "sha256_failed");
        constexpr char digits[] = "0123456789abcdef";
        std::string text;
        for (BYTE value : hash) { text += digits[value >> 4]; text += digits[value & 15]; }
        return text;
    }

    template<class T> T Export(HMODULE module, const char* name)
    {
        const FARPROC address = GetProcAddress(module, name);
        Require(address != nullptr, "driver_api_export_missing");
        T result{};
        static_assert(sizeof(result) == sizeof(address));
        std::memcpy(&result, &address, sizeof(result));
        return result;
    }

    struct Session
    {
        HMODULE module = nullptr;
        NV_ENCODE_API_FUNCTION_LIST api{};
        void* encoder = nullptr;
        std::array<NV_ENC_INPUT_PTR, Frames> inputs{};
        std::array<NV_ENC_OUTPUT_PTR, Frames> outputs{};
        std::array<ComPtr<ID3D11Texture2D>, Frames> textures;
        std::array<NV_ENC_REGISTERED_PTR, Frames> registered{};
        std::array<NV_ENC_INPUT_PTR, Frames> mapped{};
        NV_ENC_INPUT_PTR lockedInput = nullptr;
        NV_ENC_OUTPUT_PTR lockedOutput = nullptr;
        NVENCSTATUS Close() noexcept
        {
            NVENCSTATUS first = NV_ENC_SUCCESS;
            const auto note = [&](NVENCSTATUS status) { if (first == NV_ENC_SUCCESS) first = status; };
            if (encoder)
            {
                if (lockedInput) { note(api.nvEncUnlockInputBuffer(encoder, lockedInput)); lockedInput = nullptr; }
                if (lockedOutput) { note(api.nvEncUnlockBitstream(encoder, lockedOutput)); lockedOutput = nullptr; }
                for (auto& output : outputs)
                    if (output) { note(api.nvEncDestroyBitstreamBuffer(encoder, output)); output = nullptr; }
                for (auto& input : inputs)
                    if (input) { note(api.nvEncDestroyInputBuffer(encoder, input)); input = nullptr; }
                for (auto& input : mapped)
                    if (input) { note(api.nvEncUnmapInputResource(encoder, input)); input = nullptr; }
                for (auto& resource : registered)
                    if (resource) { note(api.nvEncUnregisterResource(encoder, resource)); resource = nullptr; }
                note(api.nvEncDestroyEncoder(encoder));
                encoder = nullptr;
                for (auto& texture : textures) texture.Reset();
            }
            return first;
        }
        ~Session() { Close(); if (module) FreeLibrary(module); }
    };

    void RequireCapabilities(Session& session, NV_ENC_BUFFER_FORMAT format)
    {
        for (NV_ENC_CAPS cap : { NV_ENC_CAPS_SUPPORT_LOSSLESS_ENCODE, NV_ENC_CAPS_SUPPORT_YUV444_ENCODE })
        {
            NV_ENC_CAPS_PARAM query{};
            query.version = NV_ENC_CAPS_PARAM_VER;
            query.capsToQuery = cap;
            int value = 0;
            Nv(session.api.nvEncGetEncodeCaps(session.encoder, NV_ENC_CODEC_H264_GUID, &query, &value), "capability_query_failed");
            Require(value == 1, "required_lossless_or_full_chroma_capability_missing");
        }
        std::uint32_t count = 0, written = 0;
        Nv(session.api.nvEncGetInputFormatCount(session.encoder, NV_ENC_CODEC_H264_GUID, &count), "format_count_failed");
        Require(count > 0 && count <= 64, "format_count_outside_bound");
        std::vector<NV_ENC_BUFFER_FORMAT> formats(count);
        Nv(session.api.nvEncGetInputFormats(session.encoder, NV_ENC_CODEC_H264_GUID, formats.data(), count, &written), "formats_failed");
        Require(written <= count, "format_result_outside_bound");
        formats.resize(written);
        Require(std::find(formats.begin(), formats.end(), format) != formats.end(), "required_full_chroma_input_unavailable");
        count = written = 0;
        Nv(session.api.nvEncGetEncodeProfileGUIDCount(session.encoder, NV_ENC_CODEC_H264_GUID, &count), "profile_count_failed");
        Require(count > 0 && count <= 64, "profile_count_outside_bound");
        std::vector<GUID> profiles(count);
        Nv(session.api.nvEncGetEncodeProfileGUIDs(session.encoder, NV_ENC_CODEC_H264_GUID, profiles.data(), count, &written), "profiles_failed");
        Require(written <= count, "profile_result_outside_bound");
        profiles.resize(written);
        Require(std::find(profiles.begin(), profiles.end(), NV_ENC_H264_PROFILE_HIGH_444_GUID) != profiles.end(), "high444_unavailable");
    }

    void Encode(const wchar_t* supplied, bool gpu)
    {
        Deadline deadline;
        RequireApplicationsClosed();
        const ULONGLONG started = GetTickCount64();
        const std::wstring directory = LocalPath(supplied);
        auto parents = HoldParents(directory);
        ULARGE_INTEGER available{};
        Win(GetDiskFreeSpaceExW(directory.substr(0, directory.find_last_of(L'\\')).c_str(), &available, nullptr, nullptr) != FALSE,
            "disk_space_query_failed");
        Require(available.QuadPart >= OutputLimit + 512ull * 1024 * 1024, "insufficient_fixture_disk_headroom");
        Win(CreateDirectoryW(directory.c_str(), nullptr) != FALSE, "new_output_directory_required");
        auto owned = HoldParents(directory + L"\\fixture.h264");
        Handle output(CreateFileW((directory + L"\\fixture.h264").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        Win(output.Get() != INVALID_HANDLE_VALUE, "new_output_file_required");

        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "dxgi_factory_failed");
        ComPtr<IDXGIAdapter1> adapter;
        Check(factory->EnumAdapters1(0, &adapter), "adapter_unavailable");
        DXGI_ADAPTER_DESC1 description{};
        Check(adapter->GetDesc1(&description), "adapter_description_failed");
        Require(description.VendorId == 0x10de && !(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE), "adapter_zero_not_nvidia_hardware");
        ComPtr<ID3D11Device> device;
        const D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
            static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION, &device, nullptr, nullptr), "d3d_device_failed");
        Session session;
        GpuPattern pattern;
        session.module = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(session.module != nullptr, "nvidia_driver_encode_library_unavailable");
        using GetVersion = NVENCSTATUS(NVENCAPI*)(std::uint32_t*);
        using CreateApi = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
        std::uint32_t version = 0;
        Nv(Export<GetVersion>(session.module, "NvEncodeAPIGetMaxSupportedVersion")(&version), "driver_api_version_failed");
        Require(version >= ((NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION), "driver_older_than_fixture_api");
        session.api.version = NV_ENCODE_API_FUNCTION_LIST_VER;
        Nv(Export<CreateApi>(session.module, "NvEncodeAPICreateInstance")(&session.api), "driver_api_creation_failed");
        const auto& api = session.api;
        Require(api.nvEncOpenEncodeSessionEx && api.nvEncDestroyEncoder && api.nvEncGetEncodeCaps &&
            api.nvEncGetInputFormatCount && api.nvEncGetInputFormats && api.nvEncGetEncodeProfileGUIDCount &&
            api.nvEncGetEncodeProfileGUIDs && api.nvEncGetEncodePresetConfigEx && api.nvEncInitializeEncoder &&
            api.nvEncCreateInputBuffer && api.nvEncDestroyInputBuffer && api.nvEncLockInputBuffer && api.nvEncUnlockInputBuffer &&
            api.nvEncCreateBitstreamBuffer && api.nvEncDestroyBitstreamBuffer && api.nvEncLockBitstream &&
            api.nvEncUnlockBitstream && api.nvEncEncodePicture, "driver_api_function_missing");
        if (gpu)
            Require(api.nvEncRegisterResource && api.nvEncUnregisterResource && api.nvEncMapInputResource &&
                api.nvEncUnmapInputResource, "driver_resource_api_function_missing");
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};
        open.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        open.apiVersion = NVENCAPI_VERSION;
        open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
        open.device = device.Get();
        Nv(api.nvEncOpenEncodeSessionEx(&open, &session.encoder), "session_open_failed");
        Require(session.encoder != nullptr, "session_missing");
        const auto format = gpu ? NV_ENC_BUFFER_FORMAT_AYUV : NV_ENC_BUFFER_FORMAT_YUV444;
        RequireCapabilities(session, format);
        if (gpu) pattern.Initialize(device.Get());
        NV_ENC_PRESET_CONFIG preset{};
        preset.version = NV_ENC_PRESET_CONFIG_VER;
        preset.presetCfg.version = NV_ENC_CONFIG_VER;
        Nv(api.nvEncGetEncodePresetConfigEx(session.encoder, NV_ENC_CODEC_H264_GUID, NV_ENC_PRESET_P1_GUID,
            NV_ENC_TUNING_INFO_LOSSLESS, &preset), "lossless_preset_failed");
        auto config = preset.presetCfg;
        config.profileGUID = NV_ENC_H264_PROFILE_HIGH_444_GUID;
        config.gopLength = Fps * 2;
        config.frameIntervalP = 1;
        config.frameFieldMode = NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME;
        config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CONSTQP;
        config.rcParams.constQP = {};
        config.rcParams.enableAQ = 0;
        config.rcParams.enableTemporalAQ = 0;
        config.rcParams.enableLookahead = 0;
        config.rcParams.lookaheadDepth = 0;
        config.rcParams.enableMinQP = 0;
        config.rcParams.enableMaxQP = 0;
        config.rcParams.enableInitialRCQP = 0;
        auto& h264 = config.encodeCodecConfig.h264Config;
        h264.chromaFormatIDC = 3;
        h264.qpPrimeYZeroTransformBypassFlag = 1;
        h264.separateColourPlaneFlag = 0;
        h264.disableDeblockingFilterIDC = 1;
        h264.inputBitDepth = NV_ENC_BIT_DEPTH_8;
        h264.outputBitDepth = NV_ENC_BIT_DEPTH_8;
        h264.idrPeriod = Fps * 2;
        h264.repeatSPSPPS = 1;
        h264.h264VUIParameters = {};
        auto& vui = h264.h264VUIParameters;
        vui.videoSignalTypePresentFlag = 1;
        vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
        vui.videoFullRangeFlag = 1;
        vui.colourDescriptionPresentFlag = 1;
        vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709;
        vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_RGB;
        NV_ENC_INITIALIZE_PARAMS init{};
        init.version = NV_ENC_INITIALIZE_PARAMS_VER;
        init.encodeGUID = NV_ENC_CODEC_H264_GUID;
        init.presetGUID = NV_ENC_PRESET_P1_GUID;
        init.encodeWidth = Width;
        init.encodeHeight = Height;
        init.darWidth = Width;
        init.darHeight = Height;
        init.frameRateNum = Fps;
        init.frameRateDen = 1;
        init.enablePTD = 1;
        init.enableEncodeAsync = 0;
        init.encodeConfig = &config;
        init.tuningInfo = NV_ENC_TUNING_INFO_LOSSLESS;
        RequireApplicationsClosed();
        Nv(api.nvEncInitializeEncoder(session.encoder, &init), "lossless_encoder_initialize_failed");
        std::vector<BYTE> rgb;
        std::array<std::string, Frames> hashes;
        for (UINT frame = 0; frame < Frames; ++frame)
        {
            RequireApplicationsClosed();
            NV_ENC_CREATE_BITSTREAM_BUFFER bitstream{};
            bitstream.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
            Nv(api.nvEncCreateBitstreamBuffer(session.encoder, &bitstream), "output_buffer_create_failed");
            session.outputs[frame] = bitstream.bitstreamBuffer;
            Require(bitstream.bitstreamBuffer != nullptr, "output_buffer_missing");
            GenerateRgb(frame, rgb);
            hashes[frame] = Sha256(rgb);
            NV_ENC_INPUT_PTR inputBuffer = nullptr;
            // Picture submission uses width when texture pitch is opaque; registration has a different zero rule.
            UINT inputPitch = Width;
            if (gpu)
            {
                D3D11_TEXTURE2D_DESC textureDescription{};
                textureDescription.Width = Width;
                textureDescription.Height = Height;
                textureDescription.MipLevels = 1;
                textureDescription.ArraySize = 1;
                textureDescription.Format = DXGI_FORMAT_AYUV;
                textureDescription.SampleDesc.Count = 1;
                textureDescription.Usage = D3D11_USAGE_DEFAULT;
                textureDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
                Check(device->CreateTexture2D(&textureDescription, nullptr, &session.textures[frame]), "ayuv_texture_create_failed");
                pattern.Fill(device.Get(), session.textures[frame].Get(), frame);
                RequireApplicationsClosed();
                NV_ENC_REGISTER_RESOURCE resource{};
                resource.version = NV_ENC_REGISTER_RESOURCE_VER;
                resource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
                resource.width = Width;
                resource.height = Height;
                resource.pitch = 0; // DirectX resources require zero here; the driver owns their row pitch.
                resource.resourceToRegister = session.textures[frame].Get();
                resource.bufferFormat = format;
                resource.bufferUsage = NV_ENC_INPUT_IMAGE;
                Nv(api.nvEncRegisterResource(session.encoder, &resource), "ayuv_resource_register_failed");
                session.registered[frame] = resource.registeredResource;
                Require(resource.registeredResource != nullptr, "ayuv_registered_resource_missing");
                NV_ENC_MAP_INPUT_RESOURCE mapped{};
                mapped.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
                mapped.registeredResource = resource.registeredResource;
                Nv(api.nvEncMapInputResource(session.encoder, &mapped), "ayuv_resource_map_failed");
                session.mapped[frame] = mapped.mappedResource;
                Require(mapped.mappedResource != nullptr, "ayuv_mapped_resource_missing");
                Require(mapped.mappedBufferFmt == format, "ayuv_mapped_format_changed");
                inputBuffer = mapped.mappedResource;
            }
            else
            {
                NV_ENC_CREATE_INPUT_BUFFER input{};
                input.version = NV_ENC_CREATE_INPUT_BUFFER_VER;
                input.width = Width;
                input.height = Height;
                input.bufferFmt = format;
                Nv(api.nvEncCreateInputBuffer(session.encoder, &input), "input_buffer_create_failed");
                session.inputs[frame] = input.inputBuffer;
                Require(input.inputBuffer != nullptr, "input_buffer_missing");
                NV_ENC_LOCK_INPUT_BUFFER locked{};
                locked.version = NV_ENC_LOCK_INPUT_BUFFER_VER;
                locked.inputBuffer = input.inputBuffer;
                Nv(api.nvEncLockInputBuffer(session.encoder, &locked), "input_buffer_lock_failed");
                session.lockedInput = input.inputBuffer;
                Require(locked.bufferDataPtr && locked.pitch >= Width && locked.pitch <= 16384, "input_pitch_outside_bound");
                auto* planes = static_cast<BYTE*>(locked.bufferDataPtr);
                const std::size_t planeSize = static_cast<std::size_t>(locked.pitch) * Height;
                // H.264 identity matrix uses G, B, R planes. No RGB->YCbCr conversion occurs here.
                for (UINT y = 0; y < Height; ++y)
                    for (UINT x = 0; x < Width; ++x)
                    {
                        const std::size_t source = (static_cast<std::size_t>(y) * Width + x) * 3;
                        const std::size_t target = static_cast<std::size_t>(y) * locked.pitch + x;
                        planes[target] = rgb[source + 1];
                        planes[planeSize + target] = rgb[source + 2];
                        planes[planeSize * 2 + target] = rgb[source];
                    }
                Nv(api.nvEncUnlockInputBuffer(session.encoder, input.inputBuffer), "input_buffer_unlock_failed");
                session.lockedInput = nullptr;
                inputBuffer = input.inputBuffer;
                inputPitch = locked.pitch;
            }
            NV_ENC_PIC_PARAMS picture{};
            picture.version = NV_ENC_PIC_PARAMS_VER;
            picture.inputWidth = Width;
            picture.inputHeight = Height;
            picture.inputPitch = inputPitch;
            picture.frameIdx = frame;
            picture.inputTimeStamp = frame;
            picture.inputDuration = 1;
            picture.inputBuffer = inputBuffer;
            picture.outputBitstream = bitstream.bitstreamBuffer;
            picture.bufferFmt = format;
            picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
            if (frame == 0) picture.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
            const auto status = api.nvEncEncodePicture(session.encoder, &picture);
            if (status != NV_ENC_ERR_NEED_MORE_INPUT) Nv(status, "encode_submission_failed");
        }
        NV_ENC_PIC_PARAMS eos{};
        eos.version = NV_ENC_PIC_PARAMS_VER;
        eos.encodePicFlags = NV_ENC_PIC_FLAG_EOS;
        Nv(api.nvEncEncodePicture(session.encoder, &eos), "encode_flush_failed");
        std::uint64_t total = 0;
        std::array<UINT, Frames> encodedSizes{};
        std::array<UINT, Frames> hardwareStatuses{};
        for (UINT frame = 0; frame < Frames; ++frame)
        {
            RequireApplicationsClosed();
            NV_ENC_LOCK_BITSTREAM locked{};
            locked.version = NV_ENC_LOCK_BITSTREAM_VER;
            locked.outputBitstream = session.outputs[frame];
            Nv(api.nvEncLockBitstream(session.encoder, &locked), "output_buffer_lock_failed");
            session.lockedOutput = session.outputs[frame];
            Require(locked.bitstreamBufferPtr && locked.bitstreamSizeInBytes > 0 &&
                locked.bitstreamSizeInBytes <= OutputLimit - total, "compressed_output_exceeds_bound");
            const char* frameFailure = nullptr;
            if (locked.outputTimeStamp != frame) frameFailure = "output_timestamp_mismatch";
            else if (locked.pictureType == NV_ENC_PIC_TYPE_B || locked.pictureType == NV_ENC_PIC_TYPE_BI)
                frameFailure = "unexpected_b_picture";
            if (frameFailure)
                throw FrameFailure{ frameFailure, frame, locked.outputTimeStamp, static_cast<UINT>(locked.pictureType),
                    locked.hwEncodeStatus, locked.frameIdx, locked.outputDuration, locked.bitstreamSizeInBytes, locked.frameAvgQP };
            // The checked lock return is NVENCSTATUS; hwEncodeStatus is separate hardware evidence.
            hardwareStatuses[frame] = locked.hwEncodeStatus;
            DWORD written = 0;
            Win(WriteFile(output.Get(), locked.bitstreamBufferPtr, locked.bitstreamSizeInBytes, &written, nullptr) != FALSE,
                "synthetic_output_write_failed");
            Require(written == locked.bitstreamSizeInBytes, "synthetic_output_write_incomplete");
            total += written;
            encodedSizes[frame] = written;
            Nv(api.nvEncUnlockBitstream(session.encoder, session.outputs[frame]), "output_buffer_unlock_failed");
            session.lockedOutput = nullptr;
        }
        Nv(session.Close(), "session_cleanup_failed");
        Win(FlushFileBuffers(output.Get()) != FALSE, "synthetic_output_flush_failed");
        RequireApplicationsClosed();
        std::cout << "{\"completed\":true,\"mode\":\"" << (gpu ? "gpu_ayuv_identity_gbr" : "synthetic_identity_gbr") << "\",\"width\":" << Width
            << ",\"height\":" << Height << ",\"frames\":" << Frames << ",\"fps\":" << Fps
            << ",\"codec\":\"h264\",\"chromaFormat\":3,\"matrix\":0,\"fullRange\":true,\"qp\":0,\"bFrames\":0"
            << ",\"bytes\":" << total << ",\"elapsedMs\":" << GetTickCount64() - started
            << ",\"gpuGeneratedPixels\":" << (gpu ? "true" : "false") << ",\"pixelReadbackUsed\":false"
            << ",\"captureStarted\":false,\"audioRecorded\":false,\"pixelEqualityVerified\":false,\"playbackVerified\":false,\"hardwareStatuses\":[";
        for (UINT frame = 0; frame < Frames; ++frame)
        {
            if (frame) std::cout << ',';
            std::cout << hardwareStatuses[frame];
        }
        std::cout << "],\"frameEvidence\":[";
        for (UINT frame = 0; frame < Frames; ++frame)
        {
            if (frame) std::cout << ',';
            std::cout << "{\"index\":" << frame << ",\"encodedBytes\":" << encodedSizes[frame]
                << ",\"sourceRgbSha256\":\"" << hashes[frame] << "\"}";
        }
        std::cout << "]}\n";
    }

    bool Verify(const wchar_t* supplied)
    {
        Deadline deadline;
        const auto path = LocalPath(supplied);
        auto held = HoldParents(path);
        Handle input(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
        Win(input.Get() != INVALID_HANDLE_VALUE, "decoded_file_open_failed");
        BY_HANDLE_FILE_INFORMATION info{};
        Win(GetFileInformationByHandle(input.Get(), &info) != FALSE, "decoded_file_stat_failed");
        Require((info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) == 0,
            "decoded_file_must_be_regular");
        LARGE_INTEGER length{};
        Win(GetFileSizeEx(input.Get(), &length) != FALSE, "decoded_size_failed");
        Require(length.QuadPart == static_cast<LONGLONG>(FrameBytes * Frames), "decoded_frame_count_or_size_mismatch");
        std::vector<BYTE> expected, actual(FrameBytes);
        std::uint64_t different = 0;
        UINT maxError = 0;
        std::array<std::uint64_t, Frames> differences{};
        std::array<std::string, Frames> expectedHashes, actualHashes;
        for (UINT frame = 0; frame < Frames; ++frame)
        {
            DWORD read = 0;
            Win(ReadFile(input.Get(), actual.data(), static_cast<DWORD>(actual.size()), &read, nullptr) != FALSE,
                "decoded_file_read_failed");
            Require(read == actual.size(), "decoded_file_read_incomplete");
            GenerateRgb(frame, expected);
            expectedHashes[frame] = Sha256(expected);
            actualHashes[frame] = Sha256(actual);
            for (std::size_t sample = 0; sample < FrameBytes; ++sample)
            {
                if (expected[sample] != actual[sample]) ++differences[frame];
                const int error = static_cast<int>(expected[sample]) - actual[sample];
                maxError = std::max(maxError, static_cast<UINT>(error < 0 ? -error : error));
            }
            different += differences[frame];
        }
        std::cout << "{\"completed\":true,\"mode\":\"decoded_rgb_byte_comparison\",\"frames\":" << Frames
            << ",\"samplesCompared\":" << FrameBytes * Frames << ",\"differentSamples\":" << different
            << ",\"maximumAbsoluteError\":" << maxError << ",\"pixelEqualityVerified\":" << (different == 0 ? "true" : "false")
            << ",\"playbackVerified\":false,\"gpuUsed\":false,\"frameEvidence\":[";
        for (UINT frame = 0; frame < Frames; ++frame)
        {
            if (frame) std::cout << ',';
            std::cout << "{\"index\":" << frame << ",\"differentSamples\":" << differences[frame]
                << ",\"sourceRgbSha256\":\"" << expectedHashes[frame] << "\",\"decodedRgbSha256\":\"" << actualHashes[frame] << "\"}";
        }
        std::cout << "]}\n";
        return different == 0;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && std::wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "Wisp synthetic RGB lossless feasibility fixture.\n"
            "--encode NEW_ABSOLUTE_LOCAL_DIRECTORY:16 generated720p60 frames, adapter0,15s watchdog,128MiB cap.\n"
            "--encode-gpu NEW_ABSOLUTE_LOCAL_DIRECTORY: same patterns generated only by an AYUV GPU pixel shader.\n"
            "Forza and Wisp must be closed. No screen/game capture, audio or settings changes.\n"
            "--verify ABSOLUTE_DECODED_RGB24_FILE: CPU-only exact byte comparison against generated source.\n"
            "Neither encoder success nor byte equality proves Wisp playback or gameplay performance.\n";
        return 0;
    }
    try
    {
        Require(argc == 3, "invalid_arguments");
        if (std::wcscmp(argv[1], L"--encode") == 0) Encode(argv[2], false);
        else if (std::wcscmp(argv[1], L"--encode-gpu") == 0) Encode(argv[2], true);
        else if (std::wcscmp(argv[1], L"--verify") == 0) return Verify(argv[2]) ? 0 : 3;
        else Require(false, "invalid_arguments");
        return 0;
    }
    catch (const FrameFailure& error)
    {
        std::cout << "{\"completed\":false,\"reason\":\"" << error.reason
            << "\",\"expectedFrame\":" << error.expectedFrame << ",\"inputTimeStamp\":" << error.expectedFrame
            << ",\"inputTimeStampUnits\":\"opaque_frame_index\",\"inputDuration\":1,\"outputTimeStamp\":" << error.outputTimeStamp
            << ",\"pictureType\":" << error.pictureType << ",\"hwEncodeStatus\":" << error.hwEncodeStatus
            << ",\"frameIdx\":" << error.frameIdx << ",\"outputDuration\":" << error.outputDuration
            << ",\"bitstreamBytes\":" << error.bitstreamBytes << ",\"averageQp\":" << error.averageQp << "}\n";
    }
    catch (const Failure& error)
    { std::cout << "{\"completed\":false,\"reason\":\"" << error.reason << "\",\"code\":" << error.code << "}\n"; }
    catch (...)
    { std::cout << "{\"completed\":false,\"reason\":\"unexpected_fixture_failure\"}\n"; }
    return 1;
}
