#include "HdrFrameConverter.h"

#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <cmath>
#include <new>
#ifdef WISP_HDR_FIXTURE
#include <iostream>
#endif

namespace recorder::hdr
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        struct Failure { const char* reason; HRESULT hr; };
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_FAIL }; }
        bool SameIdentity(IUnknown* left, IUnknown* right)
        {
            ComPtr<IUnknown> a, b;
            Check(left->QueryInterface(IID_PPV_ARGS(&a)), "resource_identity_query_failed");
            Check(right->QueryInterface(IID_PPV_ARGS(&b)), "resource_identity_query_failed");
            return a.Get() == b.Get();
        }
        // SDR capture retains its display codes. HDR uses an independently
        // implemented Rec.2020 Reinhard transform matching OBS SDR output.
        // The equations and reference links are documented beside the interface.
        constexpr char Shader[] = R"hlsl(
Texture2D<float4> sourceFrame : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Configuration : register(b0)
{
    float2 outputSize; float whiteScale; float sourceIsSrgb;
    float2 contentOrigin; float2 contentSize;
};
float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}
float SrgbCode(float value)
{
    return value <= 0.0031308 ? 12.92 * value : 1.055 * pow(max(value, 0), 1.0 / 2.4) - 0.055;
}
float SrgbLinear(float value)
{
    return value <= 0.04045 ? value / 12.92 : pow(max((value + 0.055) / 1.055, 0), 2.4);
}
float3 HdrDisplayCode(float3 scene)
{
    // Standard D65 primary conversions. Map in the wider gamut before the
    // final SDR gamut clamp, preserving saturation instead of pulling every
    // out-of-gamut channel toward neutral grey.
    float3 wide = float3(
        dot(scene, float3(0.627403895935, 0.329283038378, 0.043313065687)),
        dot(scene, float3(0.069097289358, 0.919540395075, 0.011362315566)),
        dot(scene, float3(0.016391438875, 0.088013307877, 0.895595253248)));
    // Negative values outside the working gamut carry no positive light.
    // Clamp before division so an invalid -1 cannot become a bright pixel.
    wide = max(wide, 0);
    float3 shaped = pow(wide / (1 + wide), 1.0 / 2.4);
    wide = float3(SrgbLinear(shaped.r), SrgbLinear(shaped.g), SrgbLinear(shaped.b));
    float3 narrow = float3(
        dot(wide, float3(1.660491002108, -0.587641138789, -0.072849863319)),
        dot(wide, float3(-0.124550474522, 1.132899897126, -0.008349422604)),
        dot(wide, float3(-0.018150763355, -0.100578898008, 1.118729661363)));
    narrow = saturate(narrow);
    return float3(SrgbCode(narrow.r), SrgbCode(narrow.g), SrgbCode(narrow.b));
}
float3 CodeRgb(float2 position)
{
    position = clamp(position, float2(0.5, 0.5), outputSize - 0.5);
    if (any(position < contentOrigin) || any(position >= contentOrigin + contentSize)) return float3(0, 0, 0);
    float2 uv = (position - contentOrigin) / contentSize;
    float3 rgb = sourceFrame.SampleLevel(linearClamp, uv, 0).rgb;
    if (!all(isfinite(rgb))) return float3(0, 0, 0);
    // Captured SDR already contains display-encoded values. Preserve them.
    if (sourceIsSrgb > 0.5)
        return rgb;
    return HdrDisplayCode(rgb * whiteScale);
}
float PSY(float4 position : SV_Position) : SV_Target
{
    float y = dot(CodeRgb(position.xy), float3(0.2126, 0.7152, 0.0722));
    return (16 + 219 * y) / 255;
}
float2 PSUV(float4 position : SV_Position) : SV_Target
{
    float2 origin = floor(position.xy) * 2 + 0.5;
    float3 rgb = 0;
    // Left-sited horizontal [1 2 1]/4, vertically centered [1 1]/2.
    // Filter nonlinear RGB before the linear YCbCr matrix, not HDR radiance.
    [unroll] for (int row = 0; row < 2; ++row)
        [unroll] for (int col = -1; col <= 1; ++col)
            rgb += CodeRgb(origin + float2(col, row)) * (col == 0 ? 0.25 : 0.125);
    float y = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return float2(128 + 112 * (rgb.b - y) / 0.9278,
        128 + 112 * (rgb.r - y) / 0.7874) / 255;
}
uint4 PSFullColor(float4 position : SV_Position) : SV_Target
{
    uint3 rgb = (uint3)floor(saturate(CodeRgb(position.xy)) * 255 + 0.5);
    // AYUV view order is V,U,Y,A; identity GBR requires Y=G,U=B,V=R.
    return uint4(rgb.r, rgb.b, rgb.g, 255);
}
float PqCode(float nits)
{
    // SMPTE ST2084 absolute light, normalized to10000cd/m2. No SDR tone curve.
    float p = pow(saturate(nits / 10000.0), 2610.0 / 16384.0);
    return pow((3424.0 / 4096.0 + (2413.0 / 128.0) * p) /
        (1 + (2392.0 / 128.0) * p), 2523.0 / 32.0);
}
float3 PqRgb(float2 position)
{
    position = clamp(position, float2(0.5, 0.5), outputSize - 0.5);
    if (any(position < contentOrigin) || any(position >= contentOrigin + contentSize)) return float3(0, 0, 0);
    float3 scene = sourceFrame.SampleLevel(linearClamp, (position - contentOrigin) / contentSize, 0).rgb;
    if (!all(isfinite(scene))) return float3(0, 0, 0);
    if (sourceIsSrgb > 0.5)
        scene = float3(SrgbLinear(scene.r), SrgbLinear(scene.g), SrgbLinear(scene.b));
    // scRGB1.0 means80nits; changing Windows SDR brightness is already in the
    // captured FP16 light values and must not divide the exposure here.
    float3 nits = 80 * float3(
        dot(scene, float3(0.627403895935, 0.329283038378, 0.043313065687)),
        dot(scene, float3(0.069097289358, 0.919540395075, 0.011362315566)),
        dot(scene, float3(0.016391438875, 0.088013307877, 0.895595253248)));
    return float3(PqCode(nits.r), PqCode(nits.g), PqCode(nits.b));
}
float PSPqY(float4 position : SV_Position) : SV_Target
{
    float y = dot(PqRgb(position.xy), float3(0.2627, 0.6780, 0.0593));
    return floor(64 + 876 * saturate(y) + 0.5) * 64 / 65535.0;
}
float2 PSPqUV(float4 position : SV_Position) : SV_Target
{
    float2 origin = floor(position.xy) * 2 + 0.5;
    float3 rgb = 0;
    [unroll] for (int row = 0; row < 2; ++row)
        [unroll] for (int col = -1; col <= 1; ++col)
            rgb += PqRgb(origin + float2(col, row)) * (col == 0 ? 0.25 : 0.125);
    float y = dot(rgb, float3(0.2627, 0.6780, 0.0593));
    float2 code = float2(512 + 448 * (rgb.b - y) / 0.9407, 512 + 448 * (rgb.r - y) / 0.7373);
    return floor(clamp(code,64,960) + 0.5) * 64 / 65535.0;
}
uint PSPqPlanar(float4 position : SV_Position) : SV_Target
{
    uint plane = (uint)position.y / (uint)outputSize.y;
    position.y -= plane * outputSize.y;
    uint3 rgb = (uint3)floor(saturate(PqRgb(position.xy)) * 1023 + 0.5);
    // Y,U,V planes hold G,B,R for identity matrix. NVENC requires MSB10.
    return (plane == 0 ? rgb.g : plane == 1 ? rgb.b : rgb.r) << 6;
}
)hlsl";
        ComPtr<ID3DBlob> Compile(const char* entry, const char* profile)
        {
            ComPtr<ID3DBlob> code, errors;
            const UINT flags = D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_IEEE_STRICTNESS |
                D3DCOMPILE_WARNINGS_ARE_ERRORS | D3DCOMPILE_OPTIMIZATION_LEVEL3;
            const HRESULT compiled = D3DCompile(Shader, sizeof(Shader) - 1, nullptr, nullptr, nullptr, entry, profile,
                flags, 0, &code, &errors);
#ifdef WISP_HDR_FIXTURE
            if (FAILED(compiled) && errors)
                std::cerr.write(static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize());
#endif
            Check(compiled, "hdr_shader_compilation_failed");
            return code;
        }
    }

    const char* ValidateConfiguration(UINT width, UINT height, SourceEncoding encoding,
        float referenceWhiteNits) noexcept
    {
        return ValidateConfiguration(width, height, encoding, referenceWhiteNits, {});
    }

    const char* ValidateConfiguration(UINT width, UINT height, SourceEncoding encoding,
        float referenceWhiteNits, const conversion::OutputConfiguration& output) noexcept
    {
        if (encoding != SourceEncoding::LinearScRgbFp16 && encoding != SourceEncoding::SrgbBgra8)
            return "source_color_encoding_unknown";
        if (encoding == SourceEncoding::SrgbBgra8 && referenceWhiteNits != 0)
            return "sdr_reference_white_must_be_zero";
        if (encoding == SourceEncoding::LinearScRgbFp16 &&
            (!std::isfinite(referenceWhiteNits) || (referenceWhiteNits != 0 &&
                (referenceWhiteNits < 10 || referenceWhiteNits > 1000))))
            return "reference_white_out_of_bounds";
        if (const char* reason = conversion::ValidateSourceGeometry(width, height)) return reason;
        return conversion::ValidateOutputConfiguration(output);
    }

    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height) noexcept
    {
        return ValidateSurfaces(input, output, width, height, {});
    }

    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const conversion::OutputConfiguration& configuration) noexcept
    {
        return ValidateSurfaces(input, output, width, height, configuration, SourceEncoding::LinearScRgbFp16);
    }

    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const conversion::OutputConfiguration& configuration, SourceEncoding encoding) noexcept
    {
        return ValidateSurfaces(input, output, width, height, configuration, encoding, OutputEncoding::Bt709Nv12);
    }

    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const conversion::OutputConfiguration& configuration, SourceEncoding encoding, OutputEncoding outputEncoding) noexcept
    {
        if (const char* reason = conversion::ValidateOutputConfiguration(configuration)) return reason;
        if (encoding != SourceEncoding::LinearScRgbFp16 && encoding != SourceEncoding::SrgbBgra8)
            return "source_color_encoding_unknown";
        const auto sourceFormat = encoding == SourceEncoding::SrgbBgra8 ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R16G16B16A16_FLOAT;
        if (input.Format != sourceFormat || input.Width != width || input.Height != height ||
            input.MipLevels != 1 || input.ArraySize != 1 || input.SampleDesc.Count != 1 || input.SampleDesc.Quality != 0 ||
            input.Usage != D3D11_USAGE_DEFAULT || input.CPUAccessFlags != 0 || !(input.BindFlags & D3D11_BIND_SHADER_RESOURCE))
            return "source_surface_incompatible";
        if (outputEncoding != OutputEncoding::Bt709Nv12 && outputEncoding != OutputEncoding::PreparedRgbAyuv &&
            outputEncoding != OutputEncoding::Bt2020PqP010 && outputEncoding != OutputEncoding::PreparedPqGbrPlanar16)
            return "output_color_encoding_unknown";
        const auto outputFormat = outputEncoding == OutputEncoding::Bt709Nv12 ? DXGI_FORMAT_NV12 :
            outputEncoding == OutputEncoding::Bt2020PqP010 ? DXGI_FORMAT_P010 :
            outputEncoding == OutputEncoding::PreparedPqGbrPlanar16 ? DXGI_FORMAT_R16_UINT : DXGI_FORMAT_AYUV;
        const UINT targetHeight = configuration.height * (outputEncoding == OutputEncoding::PreparedPqGbrPlanar16 ? 3 : 1);
        if (output.Format != outputFormat || output.Width != configuration.width || output.Height != targetHeight ||
            output.MipLevels != 1 || output.ArraySize != 1 || output.SampleDesc.Count != 1 || output.SampleDesc.Quality != 0 ||
            output.Usage != D3D11_USAGE_DEFAULT || output.CPUAccessFlags != 0 || !(output.BindFlags & D3D11_BIND_RENDER_TARGET))
            return "destination_surface_incompatible";
        return nullptr;
    }

    bool HdrFrameConverter::Initialize(ID3D11Device* device, UINT width, UINT height,
        SourceEncoding encoding, float referenceWhiteNits, Evidence& evidence) noexcept
    {
        return Initialize(device, width, height, encoding, referenceWhiteNits, {}, evidence);
    }

    bool HdrFrameConverter::Initialize(ID3D11Device* device, UINT width, UINT height,
        SourceEncoding encoding, float referenceWhiteNits,
        const conversion::OutputConfiguration& output, Evidence& evidence) noexcept
    {
        return Initialize(device, width, height, encoding, referenceWhiteNits, output, OutputEncoding::Bt709Nv12, evidence);
    }

    bool HdrFrameConverter::Initialize(ID3D11Device* device, UINT width, UINT height,
        SourceEncoding encoding, float referenceWhiteNits,
        const conversion::OutputConfiguration& output, OutputEncoding outputEncoding, Evidence& evidence) noexcept
    {
        try
        {
            if (const char* reason = ValidateConfiguration(width, height, encoding, referenceWhiteNits, output))
                throw Failure{ reason, E_INVALIDARG };
            Require(device && !device_, "invalid_converter_initialization");
            Require(outputEncoding == OutputEncoding::Bt709Nv12 || outputEncoding == OutputEncoding::PreparedRgbAyuv ||
                outputEncoding == OutputEncoding::Bt2020PqP010 || outputEncoding == OutputEncoding::PreparedPqGbrPlanar16,
                "output_color_encoding_unknown");
            Require(device->GetFeatureLevel() >= D3D_FEATURE_LEVEL_11_0, "feature_level_11_required");
            ComPtr<IDXGIDevice> dxgiDevice;
            Check(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)), "device_dxgi_query_failed");
            ComPtr<IDXGIAdapter> adapter;
            Check(dxgiDevice->GetAdapter(&adapter), "device_adapter_query_failed");
            ComPtr<IDXGIAdapter1> adapter1;
            Check(adapter.As(&adapter1), "device_adapter_query_failed");
            DXGI_ADAPTER_DESC1 description{};
            Check(adapter1->GetDesc1(&description), "adapter_description_failed");
            Require(!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE), "software_adapter_refused");
            Check(device->QueryInterface(IID_PPV_ARGS(&device_)), "d3d11_device3_required");
            device_->GetImmediateContext(&context_);
            sourceWidth_ = width; sourceHeight_ = height;
            encoding_ = encoding;
            output_ = output;
            outputEncoding_ = outputEncoding;
            evidence.output = output_;
            evidence.outputEncoding = outputEncoding_;
            const auto vertexCode = Compile("VSMain", "vs_5_0");
            Check(device_->CreateVertexShader(vertexCode->GetBufferPointer(), vertexCode->GetBufferSize(), nullptr, &vertex_), "vertex_shader_creation_failed");
            if (outputEncoding_ == OutputEncoding::Bt709Nv12 || outputEncoding_ == OutputEncoding::Bt2020PqP010)
            {
                const auto lumaCode = Compile(outputEncoding_ == OutputEncoding::Bt2020PqP010 ? "PSPqY" : "PSY", "ps_5_0");
                const auto chromaCode = Compile(outputEncoding_ == OutputEncoding::Bt2020PqP010 ? "PSPqUV" : "PSUV", "ps_5_0");
                Check(device_->CreatePixelShader(lumaCode->GetBufferPointer(), lumaCode->GetBufferSize(), nullptr, &luma_), "luma_shader_creation_failed");
                Check(device_->CreatePixelShader(chromaCode->GetBufferPointer(), chromaCode->GetBufferSize(), nullptr, &chroma_), "chroma_shader_creation_failed");
            }
            else
            {
                const auto fullColorCode = Compile(outputEncoding_ == OutputEncoding::PreparedPqGbrPlanar16 ? "PSPqPlanar" : "PSFullColor", "ps_5_0");
                Check(device_->CreatePixelShader(fullColorCode->GetBufferPointer(), fullColorCode->GetBufferSize(), nullptr, &fullColor_),
                    "full_color_shader_creation_failed");
            }
            evidence.shadersCreated = true;
            const bool sdr = encoding_ == SourceEncoding::SrgbBgra8;
            const auto fit = conversion::FitSourceToOutput(width, height, output_);
            const float values[]{ static_cast<float>(output_.width), static_cast<float>(output_.height),
                sdr ? 1.0f : 80.0f / AutomaticSdrReferenceNits, sdr ? 1.0f : 0.0f,
                fit.left, fit.top, fit.width, fit.height };
            D3D11_BUFFER_DESC buffer{};
            buffer.ByteWidth = sizeof(values); buffer.Usage = D3D11_USAGE_IMMUTABLE; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            const D3D11_SUBRESOURCE_DATA initial{ values, 0, 0 };
            Check(device_->CreateBuffer(&buffer, &initial, &constants_), "shader_constants_creation_failed");
            D3D11_SAMPLER_DESC sampler{};
            sampler.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
            sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            sampler.MaxAnisotropy = 1;
            sampler.ComparisonFunc = D3D11_COMPARISON_NEVER;
            Check(device_->CreateSamplerState(&sampler, &sampler_), "sampler_creation_failed");
            D3D11_RASTERIZER_DESC rasterizer{};
            rasterizer.FillMode = D3D11_FILL_SOLID; rasterizer.CullMode = D3D11_CULL_NONE; rasterizer.DepthClipEnable = TRUE;
            Check(device_->CreateRasterizerState(&rasterizer, &rasterizer_), "rasterizer_creation_failed");
            D3D11_BLEND_DESC blend{};
            blend.RenderTarget[0].SrcBlend = blend.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
            blend.RenderTarget[0].DestBlend = blend.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ZERO;
            blend.RenderTarget[0].BlendOp = blend.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
            blend.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
            Check(device_->CreateBlendState(&blend, &blend_), "blend_state_creation_failed");
            D3D11_DEPTH_STENCIL_DESC depth{};
            depth.DepthFunc = D3D11_COMPARISON_ALWAYS;
            depth.StencilReadMask = depth.StencilWriteMask = D3D11_DEFAULT_STENCIL_READ_MASK;
            depth.FrontFace.StencilFailOp = depth.FrontFace.StencilDepthFailOp = depth.FrontFace.StencilPassOp = D3D11_STENCIL_OP_KEEP;
            depth.FrontFace.StencilFunc = D3D11_COMPARISON_ALWAYS;
            depth.BackFace = depth.FrontFace;
            Check(device_->CreateDepthStencilState(&depth, &depth_), "depth_state_creation_failed");
            evidence.reason = "hdr_shader_converter_initialized";
            return true;
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }
        *this = HdrFrameConverter{};
        return false;
    }

    bool HdrFrameConverter::Submit(ID3D11Texture2D* source, ID3D11Texture2D* destination, Evidence& evidence) noexcept
    {
        try
        {
            Require(source && destination && device_ && context_ && (chroma_ || fullColor_), "invalid_conversion_arguments");
            Require(!SameIdentity(source, destination), "aliased_conversion_surfaces");
            ComPtr<ID3D11Device> inputDevice, outputDevice;
            source->GetDevice(&inputDevice); destination->GetDevice(&outputDevice);
            Require(inputDevice && outputDevice && SameIdentity(inputDevice.Get(), device_.Get()) &&
                SameIdentity(outputDevice.Get(), device_.Get()), "cross_device_conversion_refused");
            D3D11_TEXTURE2D_DESC input{}, output{};
            source->GetDesc(&input); destination->GetDesc(&output);
            if (const char* reason = ValidateSurfaces(input, output, sourceWidth_, sourceHeight_, output_, encoding_, outputEncoding_))
                throw Failure{ reason, E_INVALIDARG };
            Check(device_->GetDeviceRemovedReason(), "d3d11_device_removed");
            ComPtr<ID3D11ShaderResourceView> sourceView;
            Check(device_->CreateShaderResourceView(source, nullptr, &sourceView), "hdr_source_view_failed");
            D3D11_RENDER_TARGET_VIEW_DESC1 view{};
            view.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            view.Format = outputEncoding_ == OutputEncoding::Bt709Nv12 ? DXGI_FORMAT_R8_UNORM :
                outputEncoding_ == OutputEncoding::Bt2020PqP010 ? DXGI_FORMAT_R16_UNORM :
                outputEncoding_ == OutputEncoding::PreparedPqGbrPlanar16 ? DXGI_FORMAT_R16_UINT : DXGI_FORMAT_R8G8B8A8_UINT;
            ComPtr<ID3D11RenderTargetView1> yView, uvView;
            Check(device_->CreateRenderTargetView1(destination, &view, &yView), outputEncoding_ == OutputEncoding::Bt709Nv12
                ? "nv12_luma_view_failed" : "full_color_view_failed");
            if (outputEncoding_ == OutputEncoding::Bt709Nv12 || outputEncoding_ == OutputEncoding::Bt2020PqP010)
            {
                view.Format = outputEncoding_ == OutputEncoding::Bt2020PqP010 ? DXGI_FORMAT_R16G16_UNORM : DXGI_FORMAT_R8G8_UNORM;
                view.Texture2D.PlaneSlice = 1;
                Check(device_->CreateRenderTargetView1(destination, &view, &uvView), "nv12_chroma_view_failed");
            }
            evidence.planeViewsCreated = true;
            context_->IASetInputLayout(nullptr);
            context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context_->VSSetShader(vertex_.Get(), nullptr, 0);
            context_->GSSetShader(nullptr, nullptr, 0); context_->HSSetShader(nullptr, nullptr, 0); context_->DSSetShader(nullptr, nullptr, 0);
            context_->RSSetState(rasterizer_.Get());
            context_->OMSetBlendState(blend_.Get(), nullptr, 0xffffffff);
            context_->OMSetDepthStencilState(depth_.Get(), 0);
            ID3D11ShaderResourceView* inputs[]{ sourceView.Get() };
            ID3D11Buffer* buffers[]{ constants_.Get() };
            ID3D11SamplerState* samplers[]{ sampler_.Get() };
            context_->PSSetShaderResources(0, 1, inputs);
            context_->PSSetConstantBuffers(0, 1, buffers);
            context_->PSSetSamplers(0, 1, samplers);
            const D3D11_VIEWPORT yViewport{ 0, 0, static_cast<float>(output_.width),
                static_cast<float>(output_.height * (outputEncoding_ == OutputEncoding::PreparedPqGbrPlanar16 ? 3 : 1)), 0, 1 };
            context_->RSSetViewports(1, &yViewport);
            ID3D11RenderTargetView* yTarget[]{ yView.Get() };
            context_->OMSetRenderTargets(1, yTarget, nullptr);
            context_->PSSetShader((outputEncoding_ == OutputEncoding::Bt709Nv12 || outputEncoding_ == OutputEncoding::Bt2020PqP010)
                ? luma_.Get() : fullColor_.Get(), nullptr, 0);
            context_->Draw(3, 0);
            if (outputEncoding_ == OutputEncoding::Bt709Nv12 || outputEncoding_ == OutputEncoding::Bt2020PqP010)
            {
                const D3D11_VIEWPORT uvViewport{ 0, 0, static_cast<float>(output_.width / 2), static_cast<float>(output_.height / 2), 0, 1 };
                context_->RSSetViewports(1, &uvViewport);
                ID3D11RenderTargetView* uvTarget[]{ uvView.Get() };
                context_->OMSetRenderTargets(1, uvTarget, nullptr);
                context_->PSSetShader(chroma_.Get(), nullptr, 0);
                context_->Draw(3, 0);
            }
            ID3D11ShaderResourceView* noInput[]{ nullptr };
            context_->PSSetShaderResources(0, 1, noInput);
            context_->OMSetRenderTargets(0, nullptr, nullptr);
            Check(device_->GetDeviceRemovedReason(), "d3d11_device_removed");
            ++evidence.submittedFrames;
            evidence.reason = "hdr_conversion_submitted";
            return true;
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }
        return false;
    }
}
