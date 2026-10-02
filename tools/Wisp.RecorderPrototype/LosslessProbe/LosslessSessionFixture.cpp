#include "../NvencLosslessVideoSession.h"
#include "../HdrFrameConverter.h"
#include <mfapi.h>
#include <codecapi.h>
#include <d3d10.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <DirectXPackedVector.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <cwchar>
#include <iostream>
#include <limits>
#include <string>
#include <thread>
#include <vector>

namespace
{
    using namespace recorder;
    using Microsoft::WRL::ComPtr;
    using namespace DirectX::PackedVector;
    constexpr UINT SourceWidth = 3840, SourceHeight = 2160, Rate = 60;
    constexpr std::uint64_t MaximumEncodedBytes = 128ull * 1024 * 1024;
    struct Failure { const char* reason; HRESULT hr; };
    void Require(bool value, const char* reason) { if (!value) throw Failure{reason, E_FAIL}; }
    void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{reason, hr}; }
    void Win(bool value, const char* reason) { if (!value) throw Failure{reason, HRESULT_FROM_WIN32(GetLastError())}; }
    LONGLONG FrameTime(UINT frame) { return static_cast<LONGLONG>(frame) * 10000000 / Rate; }
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
        Deadline() : done_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
        {
            Win(done_.Get() != nullptr, "watchdog_event_failed");
            thread_ = std::thread([this] {
                if (WaitForSingleObject(done_.Get(), 30000) != WAIT_OBJECT_0)
                    TerminateProcess(GetCurrentProcess(), 14);
            });
        }
        ~Deadline() { SetEvent(done_.Get()); if (thread_.joinable()) thread_.join(); }
    private:
        Handle done_;
        std::thread thread_;
    };
    void RequireApplicationsClosed()
    {
        Handle processes(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
        Win(processes.Get() != INVALID_HANDLE_VALUE, "process_enumeration_failed");
        PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
        Win(Process32FirstW(processes.Get(), &entry) != FALSE, "process_enumeration_failed");
        do
        {
            Require(_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") != 0 && _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") != 0 && _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"Wisp.exe") != 0, "close_forza_and_wisp_before_fixture");
        } while (Process32NextW(processes.Get(), &entry));
        Require(GetLastError() == ERROR_NO_MORE_FILES, "process_enumeration_failed");
    }
    std::wstring LocalPath(const wchar_t* value)
    {
        const std::wstring input(value);
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
        for (size_t end = 2; end != std::wstring::npos; end = path.find(L'\\', end + 1))
        {
            const auto part = path.substr(0, end == 2 ? 3 : end);
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
    void Write(HANDLE file, const BYTE* bytes, DWORD length)
    {
        DWORD written = 0;
        Win(WriteFile(file, bytes, length, &written, nullptr) != FALSE, "fixture_write_failed");
        Require(written == length, "fixture_write_incomplete");
    }
    struct Options
    {
        bool hdr = false, entropy = false, cancel = false, earlyClose = false;
        UINT white = 0, width = 1920, height = 1080, frames = 16;
        const char* name = "sdr";
    };
    struct Rgb { double r, g, b; };
    const std::array<Rgb, 16> HdrColors{{{0,0,0},{.001,.001,.001},{.018,.018,.018},{.18,.18,.18},
        {1,1,1},{2,2,2},{4,4,4},{12.5,12.5,12.5},{4,0,0},{0,4,0},{0,0,4},
        {-.5,1,.2},{-1,-1,-1},{(std::numeric_limits<double>::quiet_NaN)(),1,1},
        {(std::numeric_limits<double>::infinity)(),1,1},{.0184,.0184,.0184}}};
    constexpr std::array<std::array<UINT, 3>, 16> SdrColors{{{0,0,0},{255,255,255},{255,0,0},{0,255,0},
        {0,0,255},{0,255,255},{255,0,255},{255,255,0},{10,10,10},{11,11,11},{32,32,32},
        {64,64,64},{128,128,128},{192,192,192},{217,109,23},{13,71,201}}};
    double Oetf(double value) { return value < .018 ? 4.5 * value : 1.099 * std::pow(value, .45) - .099; }
    double SrgbLinear(double value) { return value <= .04045 ? value / 12.92 : std::pow((value + .055) / 1.055, 2.4); }
    std::array<UINT, 3> Expected(const Options& options, UINT patch, UINT frame)
    {
        const UINT index = (patch + frame) % 16;
        std::array<double, 3> output{};
        if (!options.hdr)
            for (UINT channel = 0; channel < 3; ++channel) output[channel] = Oetf(SrgbLinear(SdrColors[index][channel] / 255.0));
        else
        {
            const auto color = HdrColors[index];
            const auto quantize = [&](double value) {
                const float source = static_cast<float>(value) * (static_cast<float>(options.white) / 80.0f);
                return static_cast<double>(XMConvertHalfToFloat(XMConvertFloatToHalf(source))) * 80.0 / options.white;
            };
            const Rgb linear{quantize(color.r), quantize(color.g), quantize(color.b)};
            const double luminance = .2126 * linear.r + .7152 * linear.g + .0722 * linear.b;
            if (std::isfinite(linear.r) && std::isfinite(linear.g) && std::isfinite(linear.b) &&
                std::isfinite(luminance) && luminance > 0)
            {
                const double neutral = luminance <= .75 ? luminance : 1 - 1 / (16 * luminance - 8);
                const double scale = neutral / luminance;
                output = {linear.r * scale, linear.g * scale, linear.b * scale};
                double compression = 1;
                for (const double channel : output)
                {
                    const double delta = channel - neutral;
                    if (delta > 0) compression = (std::min)(compression, (1 - neutral) / delta);
                    else if (delta < 0) compression = (std::min)(compression, -neutral / delta);
                }
                for (auto& channel : output) channel = Oetf(std::clamp(neutral + compression * (channel - neutral), 0.0, 1.0));
            }
        }
        std::array<UINT, 3> result{};
        for (UINT channel = 0; channel < 3; ++channel)
            result[channel] = static_cast<UINT>(std::floor(std::clamp(output[channel], 0.0, 1.0) * 255 + .5));
        return result;
    }

    class Frames final : public encoder::FrameWriter
    {
    public:
        explicit Frames(Options options) : options_(options) {}
        hdr::Evidence conversion;
        UINT filled = 0, oracleFrames = 0, patchComponents = 0, maxPatchError = 0;
        std::uint64_t oracleBytes = 0;
        void Initialize(ID3D11Device* device)
        {
            device->GetImmediateContext(&context_);
            static constexpr char shader[] = R"hlsl(
cbuffer Parameters : register(b0) { uint frame; uint sourceWidth; uint sourceHeight; uint entropy;
    float whiteScale; uint isHdr; uint reservedA; uint reservedB; };
float4 VS(uint id : SV_VertexID) : SV_Position {
    float2 p = float2((id << 1) & 2, id & 2); return float4(p * float2(2,-2) + float2(-1,1),0,1);
}
float4 PS(float4 position : SV_Position) : SV_Target {
    uint x = (uint)position.x, y = (uint)position.y;
    if (entropy != 0) {
        uint noise = (x + frame * 13) * 0x45d9f3b ^ (y + frame * 7) * 0x119de1f3;
        noise ^= noise >> 16; return float4(float3(noise & 255, (noise >> 8) & 255, (noise >> 16) & 255) / 255.0, 1);
    }
    uint patch = min(y / (sourceHeight / 4),3) * 4 + min(x / (sourceWidth / 4),3);
    uint index = (patch + frame) % 16;
    if (isHdr != 0) {
        static const float3 colors[16] = {float3(0,0,0),float3(.001,.001,.001),float3(.018,.018,.018),float3(.18,.18,.18),
            float3(1,1,1),float3(2,2,2),float3(4,4,4),float3(12.5,12.5,12.5),float3(4,0,0),float3(0,4,0),float3(0,0,4),
            float3(-.5,1,.2),float3(-1,-1,-1),float3(0,1,1),float3(0,1,1),float3(.0184,.0184,.0184)};
        float3 value = colors[index];
        if (index == 13) value.r = asfloat(0x7fc00000);
        if (index == 14) value.r = asfloat(0x7f800000);
        return float4(value * whiteScale, 1);
    }
    static const uint3 colors[16] = {uint3(0,0,0),uint3(255,255,255),uint3(255,0,0),uint3(0,255,0),uint3(0,0,255),
        uint3(0,255,255),uint3(255,0,255),uint3(255,255,0),uint3(10,10,10),uint3(11,11,11),uint3(32,32,32),
        uint3(64,64,64),uint3(128,128,128),uint3(192,192,192),uint3(217,109,23),uint3(13,71,201)};
    return float4(float3(colors[index]) / 255.0,1);
})hlsl";
            ComPtr<ID3DBlob> vertex, pixel, errors;
            constexpr UINT flags = D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_WARNINGS_ARE_ERRORS | D3DCOMPILE_OPTIMIZATION_LEVEL3;
            Check(D3DCompile(shader, std::strlen(shader), "synthetic_lossless_source", nullptr, nullptr, "VS", "vs_5_0", flags, 0, &vertex, &errors), "source_vertex_compile_failed");
            errors.Reset();
            Check(D3DCompile(shader, std::strlen(shader), "synthetic_lossless_source", nullptr, nullptr, "PS", "ps_5_0", flags, 0, &pixel, &errors), "source_pixel_compile_failed");
            Check(device->CreateVertexShader(vertex->GetBufferPointer(), vertex->GetBufferSize(), nullptr, &vertex_), "source_vertex_create_failed");
            Check(device->CreatePixelShader(pixel->GetBufferPointer(), pixel->GetBufferSize(), nullptr, &pixel_), "source_pixel_create_failed");
            D3D11_BUFFER_DESC constants{}; constants.ByteWidth = 32; constants.Usage = D3D11_USAGE_DEFAULT; constants.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            Check(device->CreateBuffer(&constants, nullptr, &constants_), "source_constants_failed");
            D3D11_RASTERIZER_DESC raster{}; raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
            Check(device->CreateRasterizerState(&raster, &rasterizer_), "source_rasterizer_failed");
            D3D11_TEXTURE2D_DESC texture{};
            texture.Width = SourceWidth; texture.Height = SourceHeight; texture.MipLevels = 1; texture.ArraySize = 1;
            texture.Format = options_.hdr ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_B8G8R8A8_UNORM;
            texture.SampleDesc.Count = 1; texture.Usage = D3D11_USAGE_DEFAULT;
            texture.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            Check(device->CreateTexture2D(&texture, nullptr, &source_), "source_texture_failed");
            Check(device->CreateRenderTargetView(source_.Get(), nullptr, &target_), "source_target_failed");
            texture.Width = options_.width; texture.Height = options_.height; texture.Format = DXGI_FORMAT_AYUV;
            texture.Usage = D3D11_USAGE_STAGING; texture.BindFlags = 0; texture.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            staging_.resize(options_.frames);
            for (auto& frame : staging_) Check(device->CreateTexture2D(&texture, nullptr, &frame), "oracle_staging_failed");
            conversion::OutputConfiguration output{options_.width, options_.height, Rate, 1, 1};
            const bool initialized = converter_.Initialize(device, SourceWidth, SourceHeight,
                options_.hdr ? hdr::SourceEncoding::LinearScRgbFp16 : hdr::SourceEncoding::SrgbBgra8,
                static_cast<float>(options_.white), output, hdr::OutputEncoding::PreparedRgbAyuv, conversion);
            if (!initialized) throw Failure{conversion.reason, conversion.hr};
        }
        HRESULT Fill(UINT index, ID3D11Texture2D* destination) noexcept override
        {
            try
            {
                Require(index == filled && index < options_.frames, "fixture_frame_order_invalid");
                struct Parameters { UINT frame, width, height, entropy; float whiteScale; UINT isHdr, a, b; };
                const Parameters values{index, SourceWidth, SourceHeight, options_.entropy ? 1u : 0u,
                    static_cast<float>(options_.white) / 80.0f, options_.hdr ? 1u : 0u, 0, 0};
                context_->UpdateSubresource(constants_.Get(), 0, nullptr, &values, 0, 0);
                const D3D11_VIEWPORT viewport{0, 0, static_cast<float>(SourceWidth), static_cast<float>(SourceHeight), 0, 1};
                context_->RSSetViewports(1, &viewport); context_->RSSetState(rasterizer_.Get());
                context_->IASetInputLayout(nullptr); context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                context_->VSSetShader(vertex_.Get(), nullptr, 0); context_->PSSetShader(pixel_.Get(), nullptr, 0);
                ID3D11Buffer* constants = constants_.Get(); context_->PSSetConstantBuffers(0, 1, &constants);
                ID3D11RenderTargetView* target = target_.Get(); context_->OMSetRenderTargets(1, &target, nullptr);
                context_->OMSetBlendState(nullptr, nullptr, 0xffffffff); context_->OMSetDepthStencilState(nullptr, 0);
                context_->Draw(3, 0); context_->OMSetRenderTargets(0, nullptr, nullptr);
                if (!converter_.Submit(source_.Get(), destination, conversion)) throw Failure{conversion.reason, conversion.hr};
                // Diagnostic copy only. No borrowed encoder surface is retained and no pixel is mapped here.
                context_->CopyResource(staging_[index].Get(), destination);
                ++filled;
                return S_OK;
            }
            catch (const Failure& error) { return error.hr; }
            catch (...) { return E_FAIL; }
        }
        void WriteOracle(HANDLE file)
        {
            Require(filled == options_.frames, "oracle_incomplete_input");
            const size_t frameBytes = static_cast<size_t>(options_.width) * options_.height * 3;
            std::vector<BYTE> rgb(frameBytes);
            for (UINT frame = 0; frame < options_.frames; ++frame)
            {
                D3D11_MAPPED_SUBRESOURCE mapped{};
                Check(context_->Map(staging_[frame].Get(), 0, D3D11_MAP_READ, 0, &mapped), "oracle_map_failed");
                bool validAlpha = true;
                for (UINT y = 0; y < options_.height; ++y)
                {
                    const auto* row = static_cast<const BYTE*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
                    for (UINT x = 0; x < options_.width; ++x)
                    {
                        const auto* pixel = row + static_cast<size_t>(x) * 4;
                        const size_t at = (static_cast<size_t>(y) * options_.width + x) * 3;
                        rgb[at] = pixel[0]; rgb[at + 1] = pixel[2]; rgb[at + 2] = pixel[1];
                        validAlpha = validAlpha && pixel[3] == 255;
                    }
                }
                context_->Unmap(staging_[frame].Get(), 0);
                Require(validAlpha, "oracle_alpha_mismatch");
                if (!options_.entropy)
                    for (UINT patch = 0; patch < 16; ++patch)
                    {
                        const UINT x = (patch % 4) * (options_.width / 4) + options_.width / 8;
                        const UINT y = (patch / 4) * (options_.height / 4) + options_.height / 8;
                        const auto expected = Expected(options_, patch, frame);
                        for (UINT channel = 0; channel < 3; ++channel)
                        {
                            const UINT actual = rgb[(static_cast<size_t>(y) * options_.width + x) * 3 + channel];
                            const UINT error = actual > expected[channel] ? actual - expected[channel] : expected[channel] - actual;
                            maxPatchError = (std::max)(maxPatchError, error); ++patchComponents;
                        }
                    }
                Write(file, rgb.data(), static_cast<DWORD>(rgb.size()));
                oracleBytes += rgb.size(); ++oracleFrames;
            }
            Require(options_.entropy || (patchComponents == options_.frames * 16 * 3 && maxPatchError <= 2), "cpu_color_patch_mismatch");
        }
    private:
        Options options_;
        ComPtr<ID3D11DeviceContext> context_;
        ComPtr<ID3D11Texture2D> source_;
        ComPtr<ID3D11RenderTargetView> target_;
        ComPtr<ID3D11VertexShader> vertex_;
        ComPtr<ID3D11PixelShader> pixel_;
        ComPtr<ID3D11Buffer> constants_;
        ComPtr<ID3D11RasterizerState> rasterizer_;
        std::vector<ComPtr<ID3D11Texture2D>> staging_;
        hdr::HdrFrameConverter converter_;
    };
    class Packets final : public encoder::PacketObserver
    {
    public:
        Packets(HANDLE file, Options options) : file_(file), options_(options) {}
        UINT packets = 0, largestPacket = 0, callbacks = 0;
        std::uint64_t bytesWritten = 0;
        bool configurationMatched = false;
        const char* reason = "not_started";
        HRESULT OnConfiguration(IMFMediaType* type, const encoder::EncodeConfig& config) noexcept override
        {
            ++callbacks;
            try
            {
                Require(type && !configurationMatched && config.width == options_.width && config.height == options_.height &&
                    config.frameRate == Rate && config.bitrate == 0 && config.chromaSiting == 0, "configuration_mismatch");
                const std::array<std::pair<GUID, UINT>, 5> required{{{MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_444},
                    {MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709}, {MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709},
                    {MF_MT_YUV_MATRIX, MFVideoTransferMatrix_Identity}, {MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_0_255}}};
                for (const auto& attribute : required)
                {
                    UINT32 value = 0; Check(type->GetUINT32(attribute.first, &value), "configuration_attribute_missing");
                    Require(value == attribute.second, "configuration_attribute_mismatch");
                }
                UINT32 length = 0; Check(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &length), "sequence_header_missing");
                Require(length && length <= 65536, "sequence_header_bounds_invalid");
                std::vector<BYTE> header(length);
                Check(type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, header.data(), length, nullptr), "sequence_header_read_failed");
                Write(file_, header.data(), length); bytesWritten += length; configurationMatched = true;
                return S_OK;
            }
            catch (const Failure& error) { reason = error.reason; return error.hr; }
            catch (...) { reason = "configuration_unexpected_failure"; return E_FAIL; }
        }
        HRESULT OnPacket(IMFMediaType*, IMFSample* sample) noexcept override
        {
            ++callbacks;
            try
            {
                Require(sample && configurationMatched && packets < options_.frames, "packet_count_invalid");
                LONGLONG time = 0, duration = 0;
                Check(sample->GetSampleTime(&time), "packet_time_missing"); Check(sample->GetSampleDuration(&duration), "packet_duration_missing");
                Require(time == FrameTime(packets) && duration == FrameTime(packets + 1) - time, "packet_timing_mismatch");
                ComPtr<IMFMediaBuffer> buffer; Check(sample->ConvertToContiguousBuffer(&buffer), "packet_buffer_failed");
                DWORD length = 0; Check(buffer->GetCurrentLength(&length), "packet_length_failed");
                largestPacket = (std::max)(largestPacket, static_cast<UINT>(length));
                Require(length && length <= lossless::MaximumPacketBytes && length <= MaximumEncodedBytes - bytesWritten, "fixture_packet_limit");
                std::vector<BYTE> payload(length); BYTE* source = nullptr;
                Check(buffer->Lock(&source, nullptr, nullptr), "packet_lock_failed");
                std::memcpy(payload.data(), source, length); Check(buffer->Unlock(), "packet_unlock_failed");
                Write(file_, payload.data(), length); bytesWritten += length; ++packets;
                return S_OK;
            }
            catch (const Failure& error) { reason = error.reason; return error.hr; }
            catch (...) { reason = "packet_unexpected_failure"; return E_FAIL; }
        }
    private:
        HANDLE file_;
        Options options_;
    };
    UINT Contracts()
    {
        UINT count = 0;
        const auto test = [&](bool value) { Require(value, "cpu_contract_failed"); ++count; };
        encoder::EncodeConfig config{1920, 1080, Rate, 0, 1, 1, 0};
        const lossless::Options options{3000, 0};
        test(lossless::ValidateConfiguration(config, options) == nullptr);
        auto invalid = config; invalid.bitrate = 1; test(lossless::ValidateConfiguration(invalid, options) != nullptr);
        invalid = config; invalid.chromaSiting = MFVideoChromaSubsampling_MPEG2; test(lossless::ValidateConfiguration(invalid, options) != nullptr);
        invalid = config; invalid.width = 4096; test(lossless::ValidateConfiguration(invalid, options) != nullptr);
        invalid = config; invalid.frameRate = 240; test(lossless::ValidateConfiguration(invalid, options) != nullptr);
        test(lossless::ValidateConfiguration(config, {99, 0}) != nullptr);
        test(lossless::ValidateConfiguration(config, {10001, 0}) != nullptr);
        test(lossless::ValidateConfiguration(config, {3000, -1}) != nullptr);
        LONGLONG duration = 0;
        test(lossless::ValidateFrameTime(60, 0, 0, 0, duration) && duration == 166666);
        test(lossless::ValidateFrameTime(60, 0, 1, 166666, duration) && duration == 166667);
        test(lossless::ValidateFrameTime(60, 0, 2, 333333, duration) && duration == 166667);
        test(lossless::ValidateFrameTime(30, 0, 0, 0, duration) && duration == 333333);
        test(lossless::ValidateFrameTime(30, 0, 2, 666666, duration) && duration == 333334);
        test(!lossless::ValidateFrameTime(60, 0, 1, 166667, duration) && duration == 0);
        test(!lossless::ValidateFrameTime(0, 0, 0, 0, duration));
        test(!lossless::ValidateFrameTime(60, -1, 0, 0, duration));
        test(!lossless::ValidateFrameTime(60, 0, (std::numeric_limits<UINT>::max)(), 0, duration));
        test(!lossless::ValidateFrameTime(60, (std::numeric_limits<LONGLONG>::max)() - 1, 0,
            (std::numeric_limits<LONGLONG>::max)() - 1, duration));
        lossless::NvencLosslessVideoSession inactive;
        test(!inactive.CanAcceptInput()); test(!inactive.Pump());
        test(SUCCEEDED(inactive.Close())); test(SUCCEEDED(inactive.Close()));
        struct NoPackets final : encoder::PacketObserver
        {
            UINT callbacks = 0;
            HRESULT OnConfiguration(IMFMediaType*, const encoder::EncodeConfig&) noexcept override { ++callbacks; return E_UNEXPECTED; }
            HRESULT OnPacket(IMFMediaType*, IMFSample*) noexcept override { ++callbacks; return E_UNEXPECTED; }
        } observer;
        std::atomic<bool> cancelled{true}; lossless::NvencLosslessVideoSession rejected;
        // Null-device rejection is deliberately CPU-only; real cancellation is a separate runtime boundary.
        test(!rejected.Initialize(nullptr, config, options, cancelled, observer));
        test(observer.callbacks == 0 && !rejected.Result().initialized); test(SUCCEEDED(rejected.Close()));
        D3D11_TEXTURE2D_DESC input{}, output{};
        input.Width = SourceWidth; input.Height = SourceHeight; input.MipLevels = input.ArraySize = input.SampleDesc.Count = 1;
        input.Format = DXGI_FORMAT_R16G16B16A16_FLOAT; input.Usage = D3D11_USAGE_DEFAULT; input.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        output = input; output.Width = 1920; output.Height = 1080; output.Format = DXGI_FORMAT_AYUV; output.BindFlags = D3D11_BIND_RENDER_TARGET;
        const conversion::OutputConfiguration geometry{1920, 1080, Rate, 1, 1};
        test(hdr::ValidateSurfaces(input, output, SourceWidth, SourceHeight, geometry,
            hdr::SourceEncoding::LinearScRgbFp16, hdr::OutputEncoding::PreparedRgbAyuv) == nullptr);
        test(hdr::ValidateSurfaces(input, output, SourceWidth, SourceHeight, geometry,
            hdr::SourceEncoding::LinearScRgbFp16, hdr::OutputEncoding::Bt709Nv12) != nullptr);
        output.Format = DXGI_FORMAT_NV12;
        test(hdr::ValidateSurfaces(input, output, SourceWidth, SourceHeight, geometry,
            hdr::SourceEncoding::LinearScRgbFp16, hdr::OutputEncoding::Bt709Nv12) == nullptr);
        test(hdr::ValidateSurfaces(input, output, SourceWidth, SourceHeight, geometry,
            hdr::SourceEncoding::LinearScRgbFp16, hdr::OutputEncoding::PreparedRgbAyuv) != nullptr);
        return count;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "--self-test: CPU-only configuration/time/inactive-state/surface contracts.\n"
            "--sdr|--hdr80|--hdr280|--entropy2160|--cancel|--early-close NEW_LOCAL_DIRECTORY\n"
            "16x1080p frames, or2x2160p entropy frames, through actual converter and NVENC session.\n"
            "Synthetic offscreen only; apps closed; no capture/audio/window.30s watchdog.\n"
            "Writes fixture.h264 and prepared.rgb oracle, under256MiB total. Independent RGB decode still required.\n";
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--self-test") == 0)
    {
        try { const auto count = Contracts(); std::cout << "{\"completed\":true,\"cpuContracts\":" << count << ",\"graphicsUsed\":false}\n"; return 0; }
        catch (...) { std::cout << "{\"completed\":false,\"reason\":\"cpu_contract_failed\",\"graphicsUsed\":false}\n"; return 3; }
    }
    if (argc != 3) return 2;
    Options options;
    if (wcscmp(argv[1], L"--sdr") == 0) {}
    else if (wcscmp(argv[1], L"--hdr80") == 0) { options.hdr = true; options.white = 80; options.name = "hdr80"; }
    else if (wcscmp(argv[1], L"--hdr280") == 0) { options.hdr = true; options.white = 280; options.name = "hdr280"; }
    else if (wcscmp(argv[1], L"--entropy2160") == 0)
    { options.entropy = true; options.width = 3840; options.height = 2160; options.frames = 2; options.name = "entropy2160"; }
    else if (wcscmp(argv[1], L"--cancel") == 0)
    { options.cancel = true; options.frames = 4; options.name = "cancel"; }
    else if (wcscmp(argv[1], L"--early-close") == 0)
    { options.earlyClose = true; options.frames = 4; options.name = "early_close"; }
    else return 2;
    std::unique_ptr<Deadline> deadline;
    bool com = false, mf = false, completed = false;
    const char* reason = "not_started";
    HRESULT failure = S_OK, shutdown = S_OK;
    lossless::Evidence evidence;
    UINT oracleFrames = 0, patchComponents = 0, patchError = 0;
    UINT earlyCloseOutstanding = 0, closeObserverCallbacks = 0;
    std::uint64_t oracleBytes = 0;
    try
    {
        deadline = std::make_unique<Deadline>(); RequireApplicationsClosed();
        const auto directory = LocalPath(argv[2]); const auto parent = HoldParents(directory);
        Win(CreateDirectoryW(directory.c_str(), nullptr) != FALSE, "new_output_directory_required");
        const auto owned = HoldParents(directory + L"\\fixture.h264");
        Handle output(CreateFileW((directory + L"\\fixture.h264").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
        Win(output.Get() != INVALID_HANDLE_VALUE, "new_encoded_file_failed");
        Handle oracle(CreateFileW((directory + L"\\prepared.rgb").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
        Win(oracle.Get() != INVALID_HANDLE_VALUE, "new_oracle_file_failed");
        Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "com_start_failed"); com = true;
        Check(MFStartup(MF_VERSION), "mf_start_failed"); mf = true;
        {
            ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter;
            Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "adapter_factory_failed");
            Check(factory->EnumAdapters1(0, &adapter), "adapter_lookup_failed");
            ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; ComPtr<ID3D10Multithread> multithread;
            const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0}; D3D_FEATURE_LEVEL selected{};
            Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION,
                &device, &selected, &context), "device_create_failed");
            Check(device.As(&multithread), "multithread_interface_failed"); multithread->SetMultithreadProtected(TRUE);
            Frames frames(options); frames.Initialize(device.Get()); Packets packets(output.Get(), options);
            encoder::EncodeConfig config{options.width, options.height, Rate, 0, 1, 1, 0};
            std::atomic<bool> cancelled{false}; lossless::NvencLosslessVideoSession session;
            try
            {
                if (!session.Initialize(device.Get(), config, {3000, 0}, cancelled, packets))
                    throw Failure{session.Result().reason, session.Result().hr};
                const ULONGLONG started = GetTickCount64(); UINT next = 0;
                while (next < options.frames)
                {
                    Require(GetTickCount64() - started < 15000, "submission_watchdog");
                    if (!session.Pump()) throw Failure{session.Result().reason, session.Result().hr};
                    if (session.CanAcceptInput())
                    {
                        const auto accepted = session.TrySubmit(next, FrameTime(next), frames);
                        Require(accepted != encoder::SubmitResult::Failed, session.Result().reason);
                        if (accepted == encoder::SubmitResult::Submitted) ++next;
                    }
                    else Sleep(1);
                }
                if (options.cancel || options.earlyClose)
                {
                    const ULONGLONG awaiting = GetTickCount64();
                    while (session.Result().encoded <= session.Result().outputSamples)
                    {
                        Require(GetTickCount64() - awaiting < 2000 && session.Result().outputSamples < options.frames,
                            "no_inflight_interval_observed");
                        if (!session.Pump()) throw Failure{session.Result().reason, session.Result().hr};
                        if (session.Result().encoded <= session.Result().outputSamples) Sleep(1);
                    }
                    earlyCloseOutstanding = session.Result().encoded - session.Result().outputSamples;
                    if (options.cancel)
                    {
                        cancelled.store(true);
                        Require(!session.Pump() && session.Result().hr == HRESULT_FROM_WIN32(ERROR_CANCELLED), "cancel_not_observed");
                    }
                }
                else
                {
                    if (!session.Drain()) throw Failure{session.Result().reason, session.Result().hr};
                    Require(packets.packets == options.frames && session.Result().outputSamples == options.frames, "output_frame_count_mismatch");
                }
            }
            catch (...)
            {
                const UINT beforeClose = packets.callbacks;
                session.Close(); evidence = session.Result();
                closeObserverCallbacks = packets.callbacks - beforeClose;
                throw;
            }
            const UINT beforeClose = packets.callbacks;
            const HRESULT closed = session.Close(); evidence = session.Result();
            closeObserverCallbacks = packets.callbacks - beforeClose;
            Check(closed, "session_cleanup_failed");
            Require(closeObserverCallbacks == 0, "observer_called_during_close");
            // These diagnostic maps occur only after encoder drain/close. The production path never reads pixels back.
            try { if (!options.cancel && !options.earlyClose) frames.WriteOracle(oracle.Get()); }
            catch (...)
            {
                oracleFrames = frames.oracleFrames; oracleBytes = frames.oracleBytes;
                patchComponents = frames.patchComponents; patchError = frames.maxPatchError; throw;
            }
            oracleFrames = frames.oracleFrames; oracleBytes = frames.oracleBytes;
            patchComponents = frames.patchComponents; patchError = frames.maxPatchError;
            Win(FlushFileBuffers(output.Get()) != FALSE && FlushFileBuffers(oracle.Get()) != FALSE, "fixture_flush_failed");
        }
        RequireApplicationsClosed(); completed = true;
        reason = options.cancel ? "cancelled_session_cleanup_passed" :
            options.earlyClose ? "early_session_cleanup_passed" : "session_and_prepared_color_passed";
    }
    catch (const Failure& error) { reason = error.reason; failure = error.hr; }
    catch (...) { reason = "unexpected_fixture_failure"; failure = E_FAIL; }
    if (mf) shutdown = MFShutdown();
    if (com) CoUninitialize();
    if (FAILED(shutdown)) { completed = false; reason = "mf_shutdown_failed"; }
    std::cout << std::boolalpha << "{\"completed\":" << completed << ",\"mode\":\"" << options.name << "\",\"reason\":\"" << reason
        << "\",\"hresult\":" << static_cast<UINT>(failure) << ",\"sessionReason\":\"" << evidence.reason
        << "\",\"sessionHresult\":" << static_cast<UINT>(evidence.hr) << ",\"nvencStatus\":" << evidence.nvencStatus
        << ",\"cleanupHresult\":" << static_cast<UINT>(evidence.cleanupHr) << ",\"mfShutdownHresult\":" << static_cast<UINT>(shutdown)
        << ",\"cleanupReason\":\"" << evidence.cleanupReason << "\",\"resourcesRetained\":" << evidence.resourcesRetained
        << ",\"closeObserverCallbacks\":" << closeObserverCallbacks
        << ",\"width\":" << options.width << ",\"height\":" << options.height << ",\"fps\":" << Rate
        << ",\"framesRequested\":" << options.frames << ",\"submitted\":" << evidence.submitted << ",\"encoded\":" << evidence.encoded
        << ",\"outputSamples\":" << evidence.outputSamples << ",\"peakInFlight\":" << evidence.peakInFlight
        << ",\"largestPacketBytes\":" << evidence.largestPacketBytes << ",\"packetLimitBytes\":" << lossless::MaximumPacketBytes
        << ",\"compressedBytes\":" << evidence.compressedBytes << ",\"drainComplete\":" << evidence.drainComplete
        << ",\"earlyCloseOutstanding\":" << earlyCloseOutstanding
        << ",\"oracleFrames\":" << oracleFrames << ",\"oracleBytes\":" << oracleBytes
        << ",\"cpuPatchComponents\":" << patchComponents << ",\"maxCpuPatchError\":" << patchError
        << ",\"requiresIndependentPixelDecode\":" << (!options.cancel && !options.earlyClose)
        << ",\"captureUsed\":false,\"audioUsed\":false}\n";
    return completed ? 0 : 3;
}
