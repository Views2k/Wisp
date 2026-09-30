#include "SyntheticHdrFrameProvider.h"

#include <DirectXPackedVector.h>
#include <algorithm>
#include <cmath>
#include <limits>
#include <new>
#include <vector>

namespace recorder::synthetic
{
    using namespace DirectX::PackedVector;
    namespace
    {
        constexpr UINT SourceWidth = 3840, SourceHeight = 2160;
        constexpr UINT RequiredChroma = MFVideoChromaSubsampling_MPEG2 | MFVideoChromaSubsampling_ProgressiveChroma;
        struct Failure { const char* reason; HRESULT hr; };
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{reason,hr}; }
        void Require(bool value, const char* reason) { if (!value) throw Failure{reason,E_INVALIDARG}; }
        struct Rgb { double r, g, b; };
        constexpr std::array<Rgb,HdrPatchCount> Colors{{ {0,0,0}, {.18,.18,.18}, {1,1,1}, {4,4,4},
            {4,0,0}, {0,4,0}, {0,0,4}, {-.5,1,.2} }};
        using Pixel = std::array<HALF,4>;
        Pixel Pack(Rgb color)
        {
            return { XMConvertFloatToHalf(static_cast<float>(color.r)), XMConvertFloatToHalf(static_cast<float>(color.g)),
                XMConvertFloatToHalf(static_cast<float>(color.b)), XMConvertFloatToHalf(1.0f) };
        }
        UINT ColorIndex(UINT patch, UINT pattern) noexcept { return (patch + pattern * 3) % HdrPatchCount; }
        double Oetf(double linear)
        {
            return linear < .018 ? 4.5 * linear : 1.099 * std::pow(linear,.45) - .099;
        }
        ExpectedPatch Reference(UINT patch, UINT pattern, float referenceWhite)
        {
            const auto half = Pack(Colors[ColorIndex(patch,pattern)]);
            const double scale = 80.0 / referenceWhite;
            std::array<double,3> color{ XMConvertHalfToFloat(half[0]) * scale,
                XMConvertHalfToFloat(half[1]) * scale, XMConvertHalfToFloat(half[2]) * scale };
            const double luminance = .2126 * color[0] + .7152 * color[1] + .0722 * color[2];
            if (luminance > 0)
            {
                const double neutral = luminance <= .75 ? luminance : 1 - .0625 / (luminance - .5);
                double compression = 1;
                for (auto& channel : color) channel *= neutral / luminance;
                for (const auto channel : color)
                {
                    const double offset = channel - neutral;
                    if (offset > 0) compression = (std::min)(compression,(1 - neutral) / offset);
                    else if (offset < 0) compression = (std::min)(compression,-neutral / offset);
                }
                for (auto& channel : color) channel = Oetf(std::clamp(neutral + compression * (channel-neutral),0.0,1.0));
            }
            else color = {};
            const double y = .2126 * color[0] + .7152 * color[1] + .0722 * color[2];
            return { (patch % 4) * (hdr::OutputWidth / 4) + hdr::OutputWidth / 8,
                (patch / 4) * (hdr::OutputHeight / 2) + hdr::OutputHeight / 4,
                static_cast<UINT>(std::floor(16 + 219 * y + .5)),
                static_cast<UINT>(std::floor(128 + 112 * (color[2]-y) / .9278 + .5)),
                static_cast<UINT>(std::floor(128 + 112 * (color[0]-y) / .7874 + .5)) };
        }
        bool Supported(const encoder::EncodeConfig& configuration, float referenceWhite) noexcept
        {
            return encoder::ValidateConfiguration(configuration) == nullptr &&
                configuration.width == hdr::OutputWidth && configuration.height == hdr::OutputHeight &&
                (configuration.chromaSiting == RequiredChroma || configuration.chromaSiting == MFVideoChromaSubsampling_MPEG2) &&
                hdr::ValidateConfiguration(SourceWidth,SourceHeight,hdr::SourceEncoding::LinearScRgbFp16,referenceWhite) == nullptr;
        }
    }

    HRESULT SyntheticHdrFrameProvider::Initialize(ID3D11Device* device, const encoder::EncodeConfig& configuration) noexcept
    {
        try
        {
            Require(device && !device_, "invalid_hdr_provider_initialization");
            Require(Supported(configuration,referenceWhiteNits_), "hdr_provider_configuration_unsupported");
            device_ = device;
            const conversion::OutputConfiguration output{ configuration.width, configuration.height,
                configuration.frameRate, configuration.pixelAspectNumerator, configuration.pixelAspectDenominator };
            if (!converter_.Initialize(device,SourceWidth,SourceHeight,hdr::SourceEncoding::LinearScRgbFp16,
                referenceWhiteNits_,output,conversionEvidence_)) throw Failure{conversionEvidence_.reason,conversionEvidence_.hr};
            std::vector<Pixel> pixels(static_cast<size_t>(SourceWidth) * SourceHeight);
            D3D11_TEXTURE2D_DESC description{};
            description.Width = SourceWidth; description.Height = SourceHeight;
            description.MipLevels = description.ArraySize = description.SampleDesc.Count = 1;
            description.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            for (UINT pattern = 0; pattern < sources_.size(); ++pattern)
            {
                for (UINT patch = 0; patch < HdrPatchCount; ++patch)
                {
                    const auto pixel = Pack(Colors[ColorIndex(patch,pattern)]);
                    const UINT left = (patch % 4) * (SourceWidth / 4), top = (patch / 4) * (SourceHeight / 2);
                    for (UINT y = top; y < top + SourceHeight / 2; ++y)
                    {
                        const auto first = pixels.begin() + static_cast<size_t>(y) * SourceWidth + left;
                        std::fill(first,first + SourceWidth / 4,pixel);
                    }
                }
                const D3D11_SUBRESOURCE_DATA initial{pixels.data(),SourceWidth * static_cast<UINT>(sizeof(Pixel)),0};
                Check(device_->CreateTexture2D(&description,&initial,&sources_[pattern]), "synthetic_hdr_source_creation_failed");
                ++evidence_.sourceTexturesCreated;
            }
            evidence_.reason = "synthetic_hdr_provider_initialized";
            return S_OK;
        }
        catch (const Failure& failure) { evidence_.reason = failure.reason; evidence_.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence_.reason = "allocation_failed"; evidence_.hr = E_OUTOFMEMORY; }
        catch (...) { evidence_.reason = "unexpected_native_failure"; evidence_.hr = E_FAIL; }
        converter_ = hdr::HdrFrameConverter{};
        for (auto& source : sources_) source.Reset();
        device_.Reset();
        return evidence_.hr;
    }

    HRESULT SyntheticHdrFrameProvider::Fill(UINT frame, UINT surfaceSlot, ID3D11Texture2D* destination) noexcept
    {
        if (!device_ || !destination || surfaceSlot >= encoder::PoolSize || !sources_[frame % 2])
        {
            evidence_.reason = "invalid_hdr_provider_frame"; evidence_.hr = E_INVALIDARG;
            return evidence_.hr;
        }
        // The encoder holds the device's entire multithread critical section
        // across this call and releases it before handing the surface to MFT.
        if (!converter_.Submit(sources_[frame % 2].Get(),destination,conversionEvidence_))
        {
            evidence_.reason = conversionEvidence_.reason; evidence_.hr = conversionEvidence_.hr;
            return evidence_.hr;
        }
        ++evidence_.filledFrames;
        evidence_.reason = "synthetic_hdr_frame_submitted";
        return S_OK;
    }

    bool SyntheticHdrFrameProvider::ExpectedFrame(UINT frame, ExpectedPatches& patches) const noexcept
    {
        patches = {};
        if (hdr::ValidateConfiguration(SourceWidth,SourceHeight,hdr::SourceEncoding::LinearScRgbFp16,referenceWhiteNits_)) return false;
        for (UINT patch = 0; patch < HdrPatchCount; ++patch) patches[patch] = Reference(patch,frame % 2,referenceWhiteNits_);
        return true;
    }

    UINT SyntheticHdrFrameProvider::RunContractTests() noexcept
    {
        UINT passed = 0;
        encoder::EncodeConfig configuration;
        configuration.chromaSiting = RequiredChroma;
        if (!Supported(configuration,80)) return 0;
        configuration.frameRate = 60;
        if (!Supported(configuration,80)) return 0;
        ++passed;
        configuration.chromaSiting = MFVideoChromaSubsampling_MPEG2;
        if (!Supported(configuration,80)) return 0;
        ++passed;
        configuration.width = 1280; configuration.height = 720;
        if (Supported(configuration,80)) return 0;
        ++passed;
        configuration = {};
        if (Supported(configuration,80)) return 0;
        configuration.chromaSiting = RequiredChroma;
        if (Supported(configuration,0) || Supported(configuration,(std::numeric_limits<float>::quiet_NaN)())) return 0;
        ++passed;
        SyntheticHdrFrameProvider provider(80);
        ExpectedPatches first, second, repeat;
        if (!provider.ExpectedFrame(0,first) || !provider.ExpectedFrame(1,second) || !provider.ExpectedFrame(2,repeat)) return 0;
        if (first[0].luma != 16 || first[0].chromaU != 128 || first[0].chromaV != 128 ||
            first[1].luma != 106 || first[1].chromaU != 128 || first[1].chromaV != 128 ||
            first[2].luma != 221 || first[2].chromaU != 128 || first[2].chromaV != 128 ||
            first[3].luma != 233 || first[3].chromaU != 128 || first[3].chromaV != 128) return 0;
        ++passed;
        for (UINT patch = 0; patch < HdrPatchCount; ++patch)
        {
            if (first[patch].centerX >= hdr::OutputWidth || first[patch].centerY >= hdr::OutputHeight ||
                (first[patch].centerX & 1) || (first[patch].centerY & 1) ||
                first[patch].luma != repeat[patch].luma || first[patch].chromaU != repeat[patch].chromaU ||
                first[patch].chromaV != repeat[patch].chromaV) return 0;
            const auto& colorMatch = first[ColorIndex(patch,1)];
            if (second[patch].luma != colorMatch.luma || second[patch].chromaU != colorMatch.chromaU ||
                second[patch].chromaV != colorMatch.chromaV) return 0;
        }
        ++passed;
        if (first[0].luma == second[0].luma || !(first[1].luma < first[2].luma && first[2].luma < first[3].luma)) return 0;
        ++passed;
        if (provider.Fill(0,0,nullptr) != E_INVALIDARG || provider.Evidence().filledFrames != 0) return 0;
        return passed + 1;
    }
}
