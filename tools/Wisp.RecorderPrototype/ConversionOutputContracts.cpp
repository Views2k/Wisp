#include "ConversionOutput.h"
#include "GpuFrameConverter.h"
#include "HdrFrameConverter.h"

#include <array>
#include <cmath>
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
        test(ValidateSourceGeometry(1920, 1200) == nullptr);
        test(ValidateSourceGeometry(854, 480) == nullptr); // Square desktop pixels, not output SAR.
        test(ValidateSourceGeometry(3440, 1440) == nullptr);
        test(ValidateSourceGeometry(5120, 1440) == nullptr);
        test(ValidateSourceGeometry(7680, 1080) == nullptr);
        test(ValidateSourceGeometry(7680, 2160) != nullptr);
        test(ValidateSourceGeometry(5120, 2160) != nullptr);
        test(ValidateSourceGeometry(7682, 1080) != nullptr);
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
        constexpr std::array<std::array<UINT, 2>, 7> sources{{
            { 3840, 2160 }, { 3440, 1440 }, { 5120, 1440 }, { 1920, 1200 },
            { 1080, 1920 }, { 7680, 1080 }, { 854, 480 }
        }};
        for (const auto preset : presets)
        {
            for (const auto source : sources)
            {
                const auto fit = FitSourceToOutput(source[0], source[1], preset);
                test(fit.width > 0 && fit.height > 0 && fit.left >= 0 && fit.top >= 0 &&
                    fit.left + fit.width <= preset.width + 0.001f && fit.top + fit.height <= preset.height + 0.001f);
                test(std::abs(2 * fit.left + fit.width - preset.width) < 0.001f &&
                    std::abs(2 * fit.top + fit.height - preset.height) < 0.001f);
                const double displayedRatio = static_cast<double>(fit.width) * preset.pixelAspectNumerator /
                    (static_cast<double>(fit.height) * preset.pixelAspectDenominator);
                test(std::abs(displayedRatio / (static_cast<double>(source[0]) / source[1]) - 1) < 0.000001);
                test(fit.width == preset.width || fit.height == preset.height);
                test(ValidateSource(source[0], source[1], SourceEncoding::SdrBgraG22P709, preset) == nullptr);
                test(recorder::hdr::ValidateConfiguration(source[0], source[1],
                    recorder::hdr::SourceEncoding::SrgbBgra8, 0, preset) == nullptr);
                test(recorder::hdr::ValidateConfiguration(source[0], source[1],
                    recorder::hdr::SourceEncoding::LinearScRgbFp16, 80, preset) == nullptr);
            }
            const auto unchanged = FitSourceToOutput(3840, 2160, preset);
            test(unchanged.left == 0 && unchanged.top == 0 && unchanged.width == preset.width && unchanged.height == preset.height);
        }
        const auto ultrawide = FitSourceToOutput(3440, 1440, {});
        test(ultrawide.left == 0 && ultrawide.width == 1920 && std::abs(ultrawide.height - 803.720930f) < 0.001f);
        const auto superwide = FitSourceToOutput(5120, 1440, {});
        test(superwide.left == 0 && superwide.top == 270 && superwide.width == 1920 && superwide.height == 540);
        const auto taller = FitSourceToOutput(1920, 1200, {});
        test(taller.left == 96 && taller.top == 0 && taller.width == 1728 && taller.height == 1080);
        const auto fractional = FitSourceToOutput(1920, 1200, presets[1]);
        test(std::abs(fractional.left - 42.7f) < 0.001f && fractional.top == 0 &&
            std::abs(fractional.width - 768.6f) < 0.001f && fractional.height == 480);
        test(FitSourceToOutput(0, 0, {}).width == 0);
        output = {}; output.pixelAspectDenominator = 0;
        test(FitSourceToOutput(1920, 1080, output).width == 0);
        return passed ? checks : 0;
    }
}
