#include "ConversionOutput.h"
#include "GpuFrameConverter.h"
#include "HdrFrameConverter.h"

#include <array>
#include <limits>

namespace recorder::conversion
{
    UINT RunOutputContractTests() noexcept
    {
        UINT checks = 0;
        bool passed = true;
        const auto test = [&](bool condition) { ++checks; passed = passed && condition; };
        constexpr std::array<OutputConfiguration, 6> presets{
            OutputConfiguration{ 640, 360, 30, 1, 1 },
            OutputConfiguration{ 854, 480, 30, 1280, 1281 },
            OutputConfiguration{ 1280, 720, 30, 1, 1 },
            OutputConfiguration{ 1920, 1080, 30, 1, 1 },
            OutputConfiguration{ 2560, 1440, 30, 1, 1 },
            OutputConfiguration{ 3840, 2160, 30, 1, 1 }
        };
        D3D11_TEXTURE2D_DESC sdr{};
        sdr.Width = 3840; sdr.Height = 2160;
        sdr.MipLevels = 1; sdr.ArraySize = 1; sdr.SampleDesc.Count = 1;
        sdr.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        sdr.Usage = D3D11_USAGE_DEFAULT; sdr.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        auto hdr = sdr;
        hdr.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        auto destination = sdr;
        destination.Format = DXGI_FORMAT_NV12;
        destination.BindFlags = D3D11_BIND_RENDER_TARGET;
        for (auto output : presets)
        {
            test(ValidateOutputConfiguration(output) == nullptr);
            test(static_cast<std::uint64_t>(output.width) * output.pixelAspectNumerator * 9 ==
                static_cast<std::uint64_t>(output.height) * output.pixelAspectDenominator * 16);
            test(ValidateSource(3840, 2160, SourceEncoding::SdrBgraG22P709, output) == nullptr);
            test(recorder::hdr::ValidateConfiguration(3840, 2160,
                recorder::hdr::SourceEncoding::LinearScRgbFp16, 80, output) == nullptr);
            destination.Width = output.width; destination.Height = output.height;
            test(ValidateSurfaces(sdr, destination, 3840, 2160, output) == nullptr);
            test(recorder::hdr::ValidateSurfaces(hdr, destination, 3840, 2160, output) == nullptr);
            output.frameRate = 60;
            test(ValidateOutputConfiguration(output) == nullptr);
            destination.Width += 2;
            test(ValidateSurfaces(sdr, destination, 3840, 2160, output) != nullptr);
            test(recorder::hdr::ValidateSurfaces(hdr, destination, 3840, 2160, output) != nullptr);
            auto invalid = output;
            invalid.pixelAspectNumerator += 1;
            test(ValidateOutputConfiguration(invalid) != nullptr);
        }
        OutputConfiguration output;
        test(output.width == 1920 && output.height == 1080 && output.frameRate == 30 &&
            output.pixelAspectNumerator == 1 && output.pixelAspectDenominator == 1);
        destination.Width = 1920; destination.Height = 1080;
        test(ValidateSurfaces(sdr, destination, 3840, 2160) == nullptr);
        test(recorder::hdr::ValidateSurfaces(hdr, destination, 3840, 2160) == nullptr);
        test(ValidateSource(3840, 2160, SourceEncoding::SdrBgraG22P709) == nullptr);
        test(recorder::hdr::ValidateConfiguration(3840, 2160,
            recorder::hdr::SourceEncoding::LinearScRgbFp16, 80) == nullptr);
        output.width = 853; output.height = 480;
        test(ValidateOutputConfiguration(output) != nullptr);
        output.width = 854; output.height = 481;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.width = 0;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.height = (std::numeric_limits<UINT>::max)();
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.width = 7680; output.height = 4320;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.width = 800; output.height = 450;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.frameRate = 24;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.frameRate = 0;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.pixelAspectDenominator = 0;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.width = 854; output.height = 480;
        test(ValidateOutputConfiguration(output) != nullptr);
        output = {}; output.pixelAspectNumerator = 1280; output.pixelAspectDenominator = 1281;
        test(ValidateOutputConfiguration(output) != nullptr);
        test(ValidateSourceGeometry(3840, 2160) == nullptr && ValidateSourceGeometry(640, 360) == nullptr);
        test(ValidateSourceGeometry(1921, 1080) != nullptr);
        test(ValidateSourceGeometry(1920, 1081) != nullptr);
        test(ValidateSourceGeometry(7680, 4320) != nullptr);
        test(ValidateSourceGeometry(1920, 1200) != nullptr);
        test(ValidateSourceGeometry(854, 480) != nullptr); // No source SAR was declared.
        test(ValidateSourceGeometry(0, 0) != nullptr);
        test(ValidateSourceGeometry((std::numeric_limits<UINT>::max)(), 1080) != nullptr);
        test(ValidateSource(1920, 1080, SourceEncoding::LinearScRgbFp16, {}) != nullptr);
        test(recorder::hdr::ValidateConfiguration(1920, 1080,
            recorder::hdr::SourceEncoding::Unknown, 80, {}) != nullptr);
        test(recorder::hdr::ValidateConfiguration(1920, 1080,
            recorder::hdr::SourceEncoding::LinearScRgbFp16, (std::numeric_limits<float>::quiet_NaN)(), {}) != nullptr);
        destination.Width = 1280; destination.Height = 720;
        test(ValidateSurfaces(sdr, destination, 3840, 2160) != nullptr);
        test(recorder::hdr::ValidateSurfaces(hdr, destination, 3840, 2160) != nullptr);
        // No device is supplied; rejection must precede any resource use.
        GpuFrameConverter sdrConverter;
        ConversionEvidence sdrEvidence;
        test(!sdrConverter.Initialize(nullptr, 3840, 2160, SourceEncoding::SdrBgraG22P709, presets[1], sdrEvidence));
        recorder::hdr::HdrFrameConverter hdrConverter;
        recorder::hdr::Evidence hdrEvidence;
        test(!hdrConverter.Initialize(nullptr, 3840, 2160,
            recorder::hdr::SourceEncoding::LinearScRgbFp16, 80, presets[1], hdrEvidence));
        return passed ? checks : 0;
    }
}
