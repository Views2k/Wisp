#include "HdrFrameConverter.h"

#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <cmath>
#include <new>

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
        // BT.709-6 OETF and matrix. Tone mapping and gamut compression are an
        // explicit appearance policy, not an assertion of recovered metadata.
        constexpr char Shader[] = R"hlsl(
Texture2D<float4> sourceFrame : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Configuration : register(b0) { float2 outputSize; float whiteScale; float sourceIsSrgb; };
float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}
float Oetf(float value)
{
    return value < 0.018 ? 4.5 * value : 1.099 * pow(max(value, 0), 0.45) - 0.099;
}
float SrgbEotf(float value)
{
    return value <= 0.04045 ? value / 12.92 : pow(max((value + 0.055) / 1.055, 0), 2.4);
}
float3 CodeRgb(float2 position)
{
    float2 uv = clamp(position, float2(0.5, 0.5), outputSize - 0.5) / outputSize;
    float3 rgb = sourceFrame.SampleLevel(linearClamp, uv, 0).rgb;
    if (!all(isfinite(rgb))) return float3(0, 0, 0);
    if (sourceIsSrgb > 0.5)
        return float3(Oetf(SrgbEotf(rgb.r)), Oetf(SrgbEotf(rgb.g)), Oetf(SrgbEotf(rgb.b)));
    rgb *= whiteScale;
    const float3 weights = float3(0.2126, 0.7152, 0.0722);
    float luminance = dot(rgb, weights);
    if (!isfinite(luminance) || luminance <= 0) return float3(0, 0, 0);
    float mappedLuminance = luminance / (1 + luminance);
    float3 chroma = rgb / (1 + luminance) - mappedLuminance;
    float amount = 1;
    [unroll] for (int channel = 0; channel < 3; ++channel)
    {
        if (chroma[channel] > 0) amount = min(amount, (1 - mappedLuminance) / chroma[channel]);
        if (chroma[channel] < 0) amount = min(amount, -mappedLuminance / chroma[channel]);
    }
    // Only guards floating-point roundoff after the explicit gamut mapping.
    float3 mapped = saturate(mappedLuminance + amount * chroma);
    return float3(Oetf(mapped.r), Oetf(mapped.g), Oetf(mapped.b));
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
)hlsl";
        ComPtr<ID3DBlob> Compile(const char* entry, const char* profile)
        {
            ComPtr<ID3DBlob> code, errors;
            const UINT flags = D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_IEEE_STRICTNESS |
                D3DCOMPILE_WARNINGS_ARE_ERRORS | D3DCOMPILE_OPTIMIZATION_LEVEL3;
            Check(D3DCompile(Shader, sizeof(Shader) - 1, nullptr, nullptr, nullptr, entry, profile,
                flags, 0, &code, &errors), "hdr_shader_compilation_failed");
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
            (!std::isfinite(referenceWhiteNits) || referenceWhiteNits < 10 || referenceWhiteNits > 1000))
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
        if (const char* reason = conversion::ValidateOutputConfiguration(configuration)) return reason;
        if (encoding != SourceEncoding::LinearScRgbFp16 && encoding != SourceEncoding::SrgbBgra8)
            return "source_color_encoding_unknown";
        const auto sourceFormat = encoding == SourceEncoding::SrgbBgra8 ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R16G16B16A16_FLOAT;
        if (input.Format != sourceFormat || input.Width != width || input.Height != height ||
            input.MipLevels != 1 || input.ArraySize != 1 || input.SampleDesc.Count != 1 || input.SampleDesc.Quality != 0 ||
            input.Usage != D3D11_USAGE_DEFAULT || input.CPUAccessFlags != 0 || !(input.BindFlags & D3D11_BIND_SHADER_RESOURCE))
            return "source_surface_incompatible";
        if (output.Format != DXGI_FORMAT_NV12 || output.Width != configuration.width || output.Height != configuration.height ||
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
        try
        {
            if (const char* reason = ValidateConfiguration(width, height, encoding, referenceWhiteNits, output))
                throw Failure{ reason, E_INVALIDARG };
            Require(device && !device_, "invalid_converter_initialization");
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
            evidence.output = output_;
            const auto vertexCode = Compile("VSMain", "vs_5_0");
            const auto lumaCode = Compile("PSY", "ps_5_0");
            const auto chromaCode = Compile("PSUV", "ps_5_0");
            Check(device_->CreateVertexShader(vertexCode->GetBufferPointer(), vertexCode->GetBufferSize(), nullptr, &vertex_), "vertex_shader_creation_failed");
            Check(device_->CreatePixelShader(lumaCode->GetBufferPointer(), lumaCode->GetBufferSize(), nullptr, &luma_), "luma_shader_creation_failed");
            Check(device_->CreatePixelShader(chromaCode->GetBufferPointer(), chromaCode->GetBufferSize(), nullptr, &chroma_), "chroma_shader_creation_failed");
            evidence.shadersCreated = true;
            const bool sdr = encoding_ == SourceEncoding::SrgbBgra8;
            const float values[]{ static_cast<float>(output_.width), static_cast<float>(output_.height),
                sdr ? 1.0f : 80.0f / referenceWhiteNits, sdr ? 1.0f : 0.0f };
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
            Require(source && destination && device_ && context_ && chroma_, "invalid_conversion_arguments");
            Require(!SameIdentity(source, destination), "aliased_conversion_surfaces");
            ComPtr<ID3D11Device> inputDevice, outputDevice;
            source->GetDevice(&inputDevice); destination->GetDevice(&outputDevice);
            Require(inputDevice && outputDevice && SameIdentity(inputDevice.Get(), device_.Get()) &&
                SameIdentity(outputDevice.Get(), device_.Get()), "cross_device_conversion_refused");
            D3D11_TEXTURE2D_DESC input{}, output{};
            source->GetDesc(&input); destination->GetDesc(&output);
            if (const char* reason = ValidateSurfaces(input, output, sourceWidth_, sourceHeight_, output_, encoding_))
                throw Failure{ reason, E_INVALIDARG };
            Check(device_->GetDeviceRemovedReason(), "d3d11_device_removed");
            ComPtr<ID3D11ShaderResourceView> sourceView;
            Check(device_->CreateShaderResourceView(source, nullptr, &sourceView), "hdr_source_view_failed");
            D3D11_RENDER_TARGET_VIEW_DESC1 view{};
            view.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            view.Format = DXGI_FORMAT_R8_UNORM;
            ComPtr<ID3D11RenderTargetView1> yView, uvView;
            Check(device_->CreateRenderTargetView1(destination, &view, &yView), "nv12_luma_view_failed");
            view.Format = DXGI_FORMAT_R8G8_UNORM; view.Texture2D.PlaneSlice = 1;
            Check(device_->CreateRenderTargetView1(destination, &view, &uvView), "nv12_chroma_view_failed");
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
            const D3D11_VIEWPORT yViewport{ 0, 0, static_cast<float>(output_.width), static_cast<float>(output_.height), 0, 1 };
            context_->RSSetViewports(1, &yViewport);
            ID3D11RenderTargetView* yTarget[]{ yView.Get() };
            context_->OMSetRenderTargets(1, yTarget, nullptr);
            context_->PSSetShader(luma_.Get(), nullptr, 0);
            context_->Draw(3, 0);
            const D3D11_VIEWPORT uvViewport{ 0, 0, static_cast<float>(output_.width / 2), static_cast<float>(output_.height / 2), 0, 1 };
            context_->RSSetViewports(1, &uvViewport);
            ID3D11RenderTargetView* uvTarget[]{ uvView.Get() };
            context_->OMSetRenderTargets(1, uvTarget, nullptr);
            context_->PSSetShader(chroma_.Get(), nullptr, 0);
            context_->Draw(3, 0);
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
