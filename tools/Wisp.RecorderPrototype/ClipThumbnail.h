#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace recorder::thumbnail
{
    constexpr std::uint32_t Width = 320, Height = 180, Stride = Width * 4, PosterBytes = Stride * Height;
    constexpr std::uint32_t RequestHeaderBytes = 32, ResponseHeaderBytes = 24, MaximumPathBytes = 8192;
    constexpr std::uint64_t MaximumFileBytes = 16ull * 1024 * 1024 * 1024;
    struct Request
    {
        std::wstring path;
        std::uint64_t fileBytes = 0, lastWriteFileTime = 0;
        std::uint32_t width = 0, height = 0;
    };
    // Pure wire/path/layout contracts. No file, decoder, graphics or process calls.
    bool ParseRequest(const std::vector<std::uint8_t>&, Request&) noexcept;
    // Sample flags use -1 for absent, 0 for FALSE and 1 for TRUE.
    bool IsProgressiveFrame(std::uint32_t interlaceMode, int interlaced, int singleField, int repeatFirstField) noexcept;
    struct DisplayArea
    {
        std::int32_t x = 0, y = 0, width = 0, height = 0;
        std::uint16_t fractionX = 0, fractionY = 0;
    };
    bool GetCropOffset(std::uint32_t codedWidth, std::uint32_t codedHeight,
        std::uint32_t visibleWidth, std::uint32_t visibleHeight, const DisplayArea&,
        std::int32_t stride, std::int64_t& scanlineOffset) noexcept;
    bool MakePoster(const std::uint8_t* rgb32, std::size_t sourceBytes, std::int64_t scanlineOffset,
        std::int32_t stride, std::uint32_t width, std::uint32_t height, std::vector<std::uint8_t>&,
        std::uint32_t pixelAspectNumerator = 1, std::uint32_t pixelAspectDenominator = 1) noexcept;
    unsigned RunContracts();
    // Explicit isolated child mode. One request, one read-only local file,
    // one decoded frame; parent owns the process deadline/cancellation.
    // No capture, playback, audio rendering, cache writes or shell handlers.
    int RunStdio() noexcept;
}
