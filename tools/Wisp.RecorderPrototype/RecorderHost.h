#pragma once

#include <cstdint>
#include <string>

namespace recorder::host
{
    struct Settings
    {
        std::uint32_t seconds = 60, height = 1080, frameRate = 60, quality = 75;
    };
    struct Policy
    {
        std::uint32_t width = 0, height = 0, frameRate = 0, bitrate = 0;
        std::uint32_t aspectNumerator = 1, aspectDenominator = 1;
        std::uint64_t spoolBytes = 0;
    };
    struct LosslessStoragePolicy
    {
        std::uint64_t videoBytes = 0, spoolBytes = 0;
    };
    // Actual-byte lossless retention. The free-space allowance covers a pinned
    // save, live replacement GOPs and both temporary/final clip copies. It is
    // not a disk reservation against other applications.
    bool BuildLosslessStoragePolicy(std::uint32_t frameRate, std::uint64_t freeBytes,
        LosslessStoragePolicy&) noexcept;
    bool BuildLosslessSaveRequirement(const LosslessStoragePolicy&, std::uint64_t chargedSpoolBytes,
        std::uint64_t videoPayloadBytes, std::uint64_t audioPayloadBytes, std::uint64_t& requiredFreeBytes) noexcept;
    // Application policy, not a guarantee of bitrate, visual quality, capture
    // cadence or operating-system timing. Runtime negotiation remains required.
    constexpr std::uint64_t MaximumLateness100ns = 2500000;
    constexpr std::uint64_t MaximumSourceAge100ns = 10000000;
    constexpr std::uint32_t MaximumCommands = 4, MaximumOutputLines = 8;
    bool BuildPolicy(const Settings&, Policy&) noexcept;
    bool QpcTo100ns(std::uint64_t counter, std::uint64_t frequency, std::uint64_t&) noexcept;
    bool FrameTime(std::uint64_t epoch, std::uint32_t index, std::uint32_t rate,
        std::uint64_t& due100ns, std::int64_t& pts100ns) noexcept;
    bool SchedulingAllowed(std::uint64_t now, std::uint64_t due) noexcept;
    // A resumed audio sample preserves its existing media index. Only the wall
    // scheduler origin changes, within the normal bounded A/V scheduling gap.
    bool RebasePausedClock(std::uint64_t source100ns, std::int64_t media100ns,
        std::uint32_t nextFrame, std::uint32_t frameRate, std::uint64_t& epoch100ns) noexcept;
    // Receipt age measures local liveness. Presentation time remains the media
    // epoch. False signals visible stale-frame repetition, not a buffer reset.
    bool FrameFresh(std::uint64_t now, std::uint64_t presentation, std::uint64_t received,
        std::uint64_t roundingAllowance) noexcept;
    bool DestinationMatches(const std::wstring& spoolDirectory, const std::wstring& destination,
        const std::string& clipId) noexcept;
    unsigned RunPolicyContracts() noexcept;
    unsigned RunDiagnosticContracts() noexcept;

    // Explicit stdio child entry only. No discovery, shell, elevation, priority,
    // desktop capture, microphone, renderer changes or child processes. A final
    // watchdog may terminate THIS helper after a blocked API/cleanup deadline.
    int RunStdioRecorder() noexcept;
}
