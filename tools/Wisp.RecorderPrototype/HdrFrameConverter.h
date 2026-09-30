#pragma once

#include "ConversionOutput.h"

#include <windows.h>
#include <d3d11_3.h>
#include <wrl/client.h>

namespace recorder::hdr
{
    constexpr UINT OutputWidth = 1920;
    constexpr UINT OutputHeight = 1080;
    enum class SourceEncoding { LinearScRgbFp16, SrgbBgra8, Unknown };

    struct Evidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK;
        bool shadersCreated = false;
        bool planeViewsCreated = false;
        UINT submittedFrames = 0;
        conversion::OutputConfiguration output{};
    };

    const char* ValidateConfiguration(UINT width, UINT height, SourceEncoding encoding,
        float referenceWhiteNits) noexcept;
    const char* ValidateConfiguration(UINT width, UINT height, SourceEncoding encoding,
        float referenceWhiteNits, const conversion::OutputConfiguration& output) noexcept;
    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height) noexcept;
    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const conversion::OutputConfiguration& configuration) noexcept;
    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const conversion::OutputConfiguration& configuration, SourceEncoding encoding) noexcept;

    // Prototype appearance policy: normalize linear scRGB by the explicitly
    // supplied SDR-white level, apply luminance Reinhard, then neutral-axis
    // gamut compression. This is not a measured game paper-white/content peak.
    // Nonfinite sampled RGB and nonpositive luminance map to black. No input
    // invalid-pixel counts are collected by this converter.
    // SDR BGRA8 is explicitly sRGB: bilinear resize in source code values,
    // followed by the piecewise sRGB EOTF and BT.709 OETF. No tone mapping or
    // white scaling applies; referenceWhiteNits must be zero in SDR mode.
    //
    // Output: exact BT.709 OETF, limited-range BT.709 matrix, progressive NV12
    // with horizontally cosited / vertically centered chroma. Caller must
    // declare these attributes to the encoder and validate its actual output.
    //
    // Requires exclusive use of the recorder device's immediate context during
    // Submit. Mutates graphics state; does not preserve application rendering
    // state. Caller owns surface lifetime and GPU completion ordering. There
    // are no waits, readback, window access, display queries or capture here.
    class HdrFrameConverter
    {
    public:
        bool Initialize(ID3D11Device* device, UINT sourceWidth, UINT sourceHeight,
            SourceEncoding encoding, float referenceWhiteNits, Evidence& evidence) noexcept;
        bool Initialize(ID3D11Device* device, UINT sourceWidth, UINT sourceHeight,
            SourceEncoding encoding, float referenceWhiteNits,
            const conversion::OutputConfiguration& output, Evidence& evidence) noexcept;
        bool Submit(ID3D11Texture2D* source, ID3D11Texture2D* destination, Evidence& evidence) noexcept;
        // Caller must pass these exact geometry/SAR/cadence values to encoding
        // and muxing. Submit does not schedule frames or guarantee this rate.
        const conversion::OutputConfiguration& Output() const noexcept { return output_; }
    private:
        UINT sourceWidth_ = 0;
        UINT sourceHeight_ = 0;
        SourceEncoding encoding_ = SourceEncoding::Unknown;
        conversion::OutputConfiguration output_{};
        Microsoft::WRL::ComPtr<ID3D11Device3> device_;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
        Microsoft::WRL::ComPtr<ID3D11VertexShader> vertex_;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> luma_, chroma_;
        Microsoft::WRL::ComPtr<ID3D11Buffer> constants_;
        Microsoft::WRL::ComPtr<ID3D11SamplerState> sampler_;
        Microsoft::WRL::ComPtr<ID3D11RasterizerState> rasterizer_;
        Microsoft::WRL::ComPtr<ID3D11BlendState> blend_;
        Microsoft::WRL::ComPtr<ID3D11DepthStencilState> depth_;
    };
}
