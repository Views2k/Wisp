#pragma once

#include "ConversionOutput.h"

#include <windows.h>
#include <d3d11_1.h>
#include <wrl/client.h>

namespace recorder::conversion
{
    constexpr UINT OutputWidth = 1920;
    constexpr UINT OutputHeight = 1080;
    constexpr UINT MediaFrameRate = 30;
    constexpr DXGI_COLOR_SPACE_TYPE InputColor = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
    constexpr DXGI_COLOR_SPACE_TYPE OutputColor = DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709;
    enum class SourceEncoding { SdrBgraG22P709, LinearScRgbFp16, Unknown };

    struct ConversionEvidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK;
        bool formatConversionSupported = false;
        bool colorStateVerified = false;
        bool autoProcessingDisabled = false;
        UINT submittedBlits = 0; // Submission only, not GPU completion or FPS.
        OutputConfiguration output{};
    };

    // CPU-only policy used by Initialize and fixture tests. HDR is deliberately
    // unavailable; format alone does not provide a validated tone-map policy.
    const char* ValidateSource(UINT width, UINT height, SourceEncoding encoding) noexcept;
    const char* ValidateSource(UINT width, UINT height, SourceEncoding encoding,
        const OutputConfiguration& output) noexcept;
    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height) noexcept;
    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const OutputConfiguration& configuration) noexcept;

    // Owns only processor/context resources. Caller owns texture lifetime and
    // completion ordering; Submit neither waits nor reads back pixels. Output
    // has the declared DXGI G22 contract only: mapping it to MFVideoTransFunc_709
    // is unverified and must not be silently assumed by an encoder integration.
    class GpuFrameConverter
    {
    public:
        bool Initialize(ID3D11Device* device, UINT sourceWidth, UINT sourceHeight,
            SourceEncoding encoding, ConversionEvidence& evidence) noexcept;
        bool Initialize(ID3D11Device* device, UINT sourceWidth, UINT sourceHeight,
            SourceEncoding encoding, const OutputConfiguration& output, ConversionEvidence& evidence) noexcept;
        bool Submit(ID3D11Texture2D* source, ID3D11Texture2D* destination,
            UINT frameIndex, ConversionEvidence& evidence) noexcept;
        // Must match the encoder/muxer, including the non-square 480p pixels.
        // Frame rate configures the processor; Submit does not generate frames.
        const OutputConfiguration& Output() const noexcept { return output_; }
    private:
        UINT sourceWidth_ = 0;
        UINT sourceHeight_ = 0;
        OutputConfiguration output_{};
        Microsoft::WRL::ComPtr<ID3D11Device> device_;
        Microsoft::WRL::ComPtr<ID3D11VideoDevice> videoDevice_;
        Microsoft::WRL::ComPtr<ID3D11VideoContext1> videoContext_;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> enumerator_;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessor> processor_;
    };
}
