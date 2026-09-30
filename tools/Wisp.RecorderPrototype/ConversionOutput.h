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
        if (width < 2 || height < 2 || width > 3840 || height > 2160 || (width & 1) || (height & 1))
            return "source_dimensions_unsupported";
        if (static_cast<std::uint64_t>(width) * 9 != static_cast<std::uint64_t>(height) * 16)
            return "source_aspect_ratio_unsupported";
        return nullptr;
    }

    // CPU-only contracts, defined in ConversionOutputContracts.cpp. No COM,
    // graphics, endpoint or capture resources are initialized by these checks.
    UINT RunOutputContractTests() noexcept;
}
