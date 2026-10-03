#pragma once

#include "ConversionOutput.h"

#include <windows.h>
#include <d3d11_3.h>
#include <wrl/client.h>

namespace recorder::hdr
{
    constexpr UINT OutputWidth = 1920;
    constexpr UINT OutputHeight = 1080;
    constexpr float AutomaticSdrReferenceNits = 300.0f;
    enum class SourceEncoding { LinearScRgbFp16, SrgbBgra8, Unknown };
    enum class OutputEncoding { Bt709Nv12, PreparedRgbAyuv, Bt2020PqP010, PreparedPqGbrPlanar16 };

    struct Evidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK;
        bool shadersCreated = false;
        bool planeViewsCreated = false;
        UINT submittedFrames = 0;
        conversion::OutputConfiguration output{};
        OutputEncoding outputEncoding = OutputEncoding::Bt709Nv12;
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
    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const conversion::OutputConfiguration& configuration, SourceEncoding encoding,
        OutputEncoding outputEncoding) noexcept;

    // The Bt709Nv12 / PreparedRgbAyuv HDR-to-SDR policy uses absolute
    // 80-nit scRGB linear units. The explicit PQ output policies are below.
    // Normalize against 300 nits, convert D65 Rec.709 primaries to Rec.2020,
    // apply per-channel Reinhard followed by gamma 2.4 display shaping,
    // decode sRGB, convert back to Rec.709, clamp the SDR gamut and encode sRGB.
    // This independently implements the math used by OBS 32.2.2 SDR display
    // capture (its default SDR reference is 300 nits); no OBS source is copied.
    // References: obsproject/obs-studio tag 32.2.2:
    // plugins/win-capture/duplicator-monitor-capture.c (render),
    // libobs/data/{opaque,color}.effect; wiki/High-precision-color-spaces-(including-HDR).
    // referenceWhiteNits describes the source's Windows SDR-content white only.
    // Zero means unavailable. It does not set HDR exposure: that setting controls SDR
    // windows, not the absolute radiance of an HDR game. No user slider is needed.
    // Nonfinite RGB maps to black; negative Rec.2020 light is clamped before
    // division. HDR highlights retain ordered levels instead of a fixed knee.
    // An SDR recording cannot reproduce a monitor's physical HDR peak luminance.
    // No input invalid-pixel counts are collected by this converter.
    // SDR BGRA8 is explicitly sRGB: bilinear resize in source code values,
    // preserving those display-encoded values. No tone mapping, transfer
    // conversion or white scaling applies; referenceWhiteNits must be zero.
    // The entire source is fitted without cropping into the selected output,
    // accounting for output pixel aspect. Unused pixels are neutral black.
    //
    // HDR-to-SDR output uses the piecewise sRGB display transfer after mapping. NV12
    // uses the limited-range BT.709 matrix with horizontally cosited / vertically
    // centered chroma. Existing SDR video BT.709 metadata remains unchanged;
    // it does not require remapping captured sRGB codes through a camera OETF.
    // Explicit PreparedRgbAyuv instead preserves full-color CodeRgb output:
    // clamp to [0,1], quantize floor(value*255+0.5), pack Y=G,U=B,V=R,A=255.
    // This is eight-bit prepared SDR after scaling/appearance mapping, not
    // original HDR pixels. The encoder must use full-range identity GBR.
    // Bt2020PqP010 instead retains absolute HDR light: scRGB 1.0=80nits,
    // linear 709->2020, ST2084, limited 10-bit P010 with filtered 4:2:0 chroma.
    // PreparedPqGbrPlanar16 performs the same primary/PQ conversion without
    // subsampling, rounding PQ RGB to 10 bits and storing G,B,R in an R16_UINT
    // W-by-3H atlas, with samples shifted left 6 for NVENC's planar 10-bit input.
    // Both HDR modes clamp only outside 0..10000 nits and Rec.2020's RGB gamut;
    // no SDR brightness normalization or highlight tone curve is applied.
    // Codec losslessness preserves these prepared PQ10 pixels, not source FP16.
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
        bool Initialize(ID3D11Device* device, UINT sourceWidth, UINT sourceHeight,
            SourceEncoding encoding, float referenceWhiteNits,
            const conversion::OutputConfiguration& output, OutputEncoding outputEncoding, Evidence& evidence) noexcept;
        bool Submit(ID3D11Texture2D* source, ID3D11Texture2D* destination, Evidence& evidence) noexcept;
        // Caller must pass these exact geometry/SAR/cadence values to encoding
        // and muxing. Submit does not schedule frames or guarantee this rate.
        const conversion::OutputConfiguration& Output() const noexcept { return output_; }
    private:
        UINT sourceWidth_ = 0;
        UINT sourceHeight_ = 0;
        SourceEncoding encoding_ = SourceEncoding::Unknown;
        OutputEncoding outputEncoding_ = OutputEncoding::Bt709Nv12;
        conversion::OutputConfiguration output_{};
        Microsoft::WRL::ComPtr<ID3D11Device3> device_;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
        Microsoft::WRL::ComPtr<ID3D11VertexShader> vertex_;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> luma_, chroma_, fullColor_;
        Microsoft::WRL::ComPtr<ID3D11Buffer> constants_;
        Microsoft::WRL::ComPtr<ID3D11SamplerState> sampler_;
        Microsoft::WRL::ComPtr<ID3D11RasterizerState> rasterizer_;
        Microsoft::WRL::ComPtr<ID3D11BlendState> blend_;
        Microsoft::WRL::ComPtr<ID3D11DepthStencilState> depth_;
    };
}
