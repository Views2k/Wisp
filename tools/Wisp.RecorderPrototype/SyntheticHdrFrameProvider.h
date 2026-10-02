#pragma once

#include "HardwareEncoder.h"
#include "HdrFrameConverter.h"
#include <array>

namespace recorder::synthetic
{
    constexpr UINT HdrPatchCount = 8;
    struct ExpectedPatch
    {
        UINT centerX = 0, centerY = 0;
        UINT luma = 0, chromaU = 0, chromaV = 0;
    };
    using ExpectedPatches = std::array<ExpectedPatch, HdrPatchCount>;
    struct ProviderEvidence
    {
        const char* reason = "not_started";
        HRESULT hr = S_OK;
        UINT sourceTexturesCreated = 0;
        UINT filledFrames = 0;
    };

    // Two generated 4K FP16 scRGB patterns, alternated by input frame number.
    // Only the existing 1080p converter is supported (at 30 or 60 media fps).
    // Reference white is an explicit synthetic parameter; no display/content
    // measurements or game/window capture occur. Constructing this object and
    // ExpectedFrame initialize no graphics resources.
    class SyntheticHdrFrameProvider final : public encoder::FixtureFrameProvider
    {
    public:
        explicit SyntheticHdrFrameProvider(float referenceWhiteNits) noexcept : referenceWhiteNits_(referenceWhiteNits) {}
        HRESULT Initialize(ID3D11Device* device, const encoder::EncodeConfig& configuration) noexcept override;
        HRESULT Fill(UINT frame, UINT surfaceSlot, ID3D11Texture2D* destination) noexcept override;
        bool ExpectedFrame(UINT frame, ExpectedPatches& patches) const noexcept;
        const ProviderEvidence& Evidence() const noexcept { return evidence_; }
        const hdr::Evidence& ConversionEvidence() const noexcept { return conversionEvidence_; }
        static UINT RunContractTests() noexcept;
    private:
        float referenceWhiteNits_ = 0;
        ProviderEvidence evidence_{};
        hdr::Evidence conversionEvidence_{};
        Microsoft::WRL::ComPtr<ID3D11Device> device_;
        std::array<Microsoft::WRL::ComPtr<ID3D11Texture2D>,2> sources_;
        hdr::HdrFrameConverter converter_;
    };
}
