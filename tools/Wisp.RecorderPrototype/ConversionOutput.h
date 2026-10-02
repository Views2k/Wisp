#pragma once

#include <windows.h>
#include <cstdint>

namespace recorder::conversion
{
    struct OutputConfiguration
    {
        UINT width = 1920, height = 1080, frameRate = 30;
        UINT pixelAspectNumerator = 1, pixelAspectDenominator = 1;
    };

    // NV12 requires even dimensions. These are the same requested presets as
    // the encoder; successful validation is not a hardware capability claim.
    // 854x480 uses SAR 1280:1281 to retain exact 16:9 display geometry.
    inline const char* ValidateOutputConfiguration(const OutputConfiguration& value) noexcept
    {
        const bool preset = (value.width == 640 && value.height == 360) ||
            (value.width == 854 && value.height == 480) ||
            (value.width == 1280 && value.height == 720) ||
            (value.width == 1920 && value.height == 1080) ||
            (value.width == 2560 && value.height == 1440) ||
            (value.width == 3840 && value.height == 2160);
        if (!preset) return "output_dimensions_unsupported";
        if (value.frameRate != 30 && value.frameRate != 60) return "output_frame_rate_unsupported";
        const bool fractional = value.width == 854;
        if (value.pixelAspectNumerator != (fractional ? 1280u : 1u) ||
            value.pixelAspectDenominator != (fractional ? 1281u : 1u)) return "output_pixel_aspect_unsupported";
        return nullptr;
    }

    inline const char* ValidateSourceGeometry(UINT width, UINT height) noexcept
    {
        // Wider desktop shapes retain the existing maximum pixel allocation.
        if (width < 2 || height < 2 || width > 7680 || height > 2160 || (width & 1) || (height & 1) ||
            static_cast<std::uint64_t>(width) * height > 3840ull * 2160)
            return "source_dimensions_unsupported";
        return nullptr;
    }

    struct ContentRectangle
    {
        float left = 0, top = 0, width = 0, height = 0;
    };

    // Desktop source pixels are square. Fit the complete source into the
    // selected output's display aspect, including its declared pixel aspect.
    // Fractional edges avoid introducing a second resize distortion; shaders
    // sample pixel centers and emit black outside this centered rectangle.
    inline ContentRectangle FitSourceToOutput(UINT sourceWidth, UINT sourceHeight,
        const OutputConfiguration& output) noexcept
    {
        if (ValidateSourceGeometry(sourceWidth, sourceHeight) || ValidateOutputConfiguration(output)) return {};
        const auto sourceRatio = static_cast<std::uint64_t>(sourceWidth) * output.height * output.pixelAspectDenominator;
        const auto outputRatio = static_cast<std::uint64_t>(sourceHeight) * output.width * output.pixelAspectNumerator;
        double width = output.width, height = output.height;
        if (sourceRatio > outputRatio)
            height = static_cast<double>(output.width) * output.pixelAspectNumerator * sourceHeight /
                (static_cast<double>(sourceWidth) * output.pixelAspectDenominator);
        else if (sourceRatio < outputRatio)
            width = static_cast<double>(output.height) * sourceWidth * output.pixelAspectDenominator /
                (static_cast<double>(sourceHeight) * output.pixelAspectNumerator);
        return { static_cast<float>((output.width - width) / 2), static_cast<float>((output.height - height) / 2),
            static_cast<float>(width), static_cast<float>(height) };
    }

    // CPU-only contracts, defined in ConversionOutputContracts.cpp. No COM,
    // graphics, endpoint or capture resources are initialized by these checks.
    UINT RunOutputContractTests() noexcept;
}
