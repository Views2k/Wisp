#include "RecorderHost.h"
#include "GameWindowCapture.h"

#include <limits>

namespace recorder::host
{
    unsigned RunPolicyContracts() noexcept
    {
        unsigned count = 0;
        bool passed = true;
        const auto test = [&](bool value) { ++count; passed = passed && value; };
        Settings settings;
        Policy policy;
        test(BuildPolicy(settings, policy) && policy.width == 1920 && policy.bitrate == 16000000);
        test(policy.spoolBytes == 372708608ull);
        settings.quality = 10;
        test(BuildPolicy(settings, policy) && policy.bitrate == 4000000);
        settings.quality = 100;
        test(BuildPolicy(settings, policy) && policy.bitrate == 24000000);
        settings.height = 2160; settings.seconds = 300;
        test(BuildPolicy(settings, policy) && policy.bitrate == 96000000 && policy.spoolBytes < 16ull * 1024 * 1024 * 1024);
        settings = {}; settings.height = 480;
        test(BuildPolicy(settings, policy) && policy.width == 854 && policy.aspectNumerator == 1280 && policy.aspectDenominator == 1281);
        settings = {}; settings.height = 360; settings.frameRate = 30; settings.quality = 10; settings.seconds = 30;
        test(BuildPolicy(settings, policy) && policy.spoolBytes == 64ull * 1024 * 1024);
        settings.height = 361; test(!BuildPolicy(settings, policy) && policy.bitrate == 0);
        settings = {}; settings.frameRate = 59; test(!BuildPolicy(settings, policy));
        settings = {}; settings.seconds = 31; test(!BuildPolicy(settings, policy));
        settings = {}; settings.seconds = 330; test(!BuildPolicy(settings, policy));
        settings = {}; settings.quality = 9; test(!BuildPolicy(settings, policy));
        settings = {}; settings.quality = 101; test(!BuildPolicy(settings, policy));
        LosslessStoragePolicy lossless;
        constexpr std::uint64_t gib = 1024ull * 1024 * 1024;
        test(!BuildLosslessStoragePolicy(60, 0, lossless) && lossless.videoBytes == 0);
        test(!BuildLosslessStoragePolicy(60, 3 * gib, lossless));
        test(!BuildLosslessStoragePolicy(59, 100 * gib, lossless));
        test(BuildLosslessStoragePolicy(60, 100 * gib, lossless));
        test(lossless.videoBytes == 12 * gib && lossless.spoolBytes <= 64 * gib);
        const auto capped = lossless;
        test(BuildLosslessStoragePolicy(60, (std::numeric_limits<std::uint64_t>::max)(), lossless));
        test(lossless.videoBytes == capped.videoBytes && lossless.spoolBytes == capped.spoolBytes);
        test(BuildLosslessStoragePolicy(30, 40 * gib, lossless));
        test(lossless.videoBytes > 0 && lossless.videoBytes <= 12 * gib && lossless.spoolBytes < 40 * gib);
        std::uint64_t required = 0, grownRequired = 0;
        constexpr std::uint64_t mib = 1024ull * 1024;
        test(BuildLosslessSaveRequirement(capped, 0, gib, 16 * mib, required));
        test(required == capped.spoolBytes + 2 * (gib + gib / 4 + 80 * mib) + gib);
        test(BuildLosslessSaveRequirement(capped, gib, gib, 16 * mib, grownRequired) && grownRequired == required - gib);
        test(BuildLosslessSaveRequirement(capped, capped.spoolBytes, 12 * gib, 0, required) &&
            required == 31 * gib + 128 * mib);
        test(!BuildLosslessSaveRequirement(capped, capped.spoolBytes + 1, gib, 0, required) && required == 0);
        test(!BuildLosslessSaveRequirement(capped, 0, 0, 0, required));
        test(!BuildLosslessSaveRequirement(capped, 0, 12 * gib + 1, 0, required));
        test(!BuildLosslessSaveRequirement(capped, 0, gib, 16 * mib + 1, required));
        test(!BuildLosslessSaveRequirement({}, 0, gib, 0, required));
        test(!BuildLosslessSaveRequirement({12 * gib, (std::numeric_limits<std::uint64_t>::max)()}, 0, gib, 0, required));
        std::uint64_t time = 0, due = 0;
        std::int64_t pts = 0;
        test(QpcTo100ns(12345678, 10000000, time) && time == 12345678);
        test(QpcTo100ns(3125001, 3125000, time) && time == 10000003);
        test(!QpcTo100ns(1, 0, time));
        const auto maximum = static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)());
        test(QpcTo100ns(maximum, maximum, time) && time == 10000000);
        test(!QpcTo100ns(maximum, 1, time));
        test(FrameTime(10000000, 2, 60, due, pts) && due == 10333333 && pts == 333333);
        test(FrameTime(10000000, 648000, 30, due, pts) && due == 216010000000ull);
        test(!FrameTime(maximum, 1, 60, due, pts));
        test(!FrameTime(1, (std::numeric_limits<std::uint32_t>::max)(), 60, due, pts));
        test(!FrameTime(1, 1, 59, due, pts));
        test(SchedulingAllowed(100, 100) && SchedulingAllowed(99, 100));
        test(SchedulingAllowed(100 + MaximumLateness100ns, 100));
        test(!SchedulingAllowed(101 + MaximumLateness100ns, 100));
        // A newly acquired static desktop can carry an older presentation time.
        // Its first-submit clock starts PTS0 now, without altering image age or
        // backdating frame1 into an already missed scheduler interval.
        constexpr std::uint64_t firstSubmit = 100000000, oldDesktopPresentation = 70000000;
        test(FrameTime(oldDesktopPresentation, 1, 60, due, pts) && !SchedulingAllowed(firstSubmit, due));
        test(FrameTime(firstSubmit, 0, 60, due, pts) && due == firstSubmit && pts == 0);
        test(FrameTime(firstSubmit, 1, 60, due, pts) && due == firstSubmit + 166666 && pts == 166666 && SchedulingAllowed(firstSubmit, due));
        test(!FrameFresh(firstSubmit, oldDesktopPresentation, firstSubmit, 1));
        test(FrameFresh(100 + MaximumSourceAge100ns, 100, 100, 1));
        test(!FrameFresh(101 + MaximumSourceAge100ns, 100, 101 + MaximumSourceAge100ns, 1));
        test(!FrameFresh(101 + MaximumSourceAge100ns, 101 + MaximumSourceAge100ns, 100, 1));
        test(FrameFresh(100, 100, 101, 1) && !FrameFresh(100, 100, 102, 1));
        test(!FrameFresh(100, 0, 100, 1) && !FrameFresh(100, 100, 0, 1));
        // Measured failure: presentation led host now by 3.2949 ms, while the
        // separately observed local receipt is fresh. Do not rewrite media time.
        const std::uint64_t observedNow = 100000000, presentation = observedNow + 32949;
        std::uint64_t received = 0;
        test(QpcTo100ns(observedNow - 120, 10000000, received) && FrameFresh(observedNow, presentation, received, 1));
        test(FrameTime(presentation, 1, 60, due, pts) && due == presentation + 166666 && pts == 166666);
        test(!FrameFresh(observedNow, presentation, observedNow - MaximumSourceAge100ns - 1, 1));
        test(!FrameFresh(observedNow, presentation, observedNow + 2, 1));
        test(!FrameFresh(maximum, maximum + 1, maximum, 1) && !FrameFresh(maximum, maximum, maximum + 1, 1));
        LONGLONG normalized = 0;
        bool clamped = false;
        test(capture::NormalizeFrameTimestamp(1234, 0, normalized, clamped) && normalized == 1234 && !clamped);
        test(capture::NormalizeFrameTimestamp(1234, 1234, normalized, clamped) && normalized == 1235 && clamped);
        test(capture::NormalizeFrameTimestamp(1200, 1235, normalized, clamped) && normalized == 1236 && clamped);
        test(capture::NormalizeFrameTimestamp(1300, 1236, normalized, clamped) && normalized == 1300 && !clamped);
        test(!capture::NormalizeFrameTimestamp(0, 100, normalized, clamped) && normalized == 0);
        test(!capture::NormalizeFrameTimestamp(100, (std::numeric_limits<LONGLONG>::max)(), normalized, clamped));
        // Metadata normalization cannot shift an already selected media epoch.
        test(FrameTime(presentation, 2, 60, due, pts) && due == presentation + 333333 && pts == 333333);
        const std::string clip = "fedcba9876543210fedcba9876543210";
        const std::wstring spool = L"C:\\Clips\\.wisp-recorder-0123456789abcdef0123456789abcdef";
        test(DestinationMatches(spool, L"c:/clips/fedcba9876543210fedcba9876543210.mp4", clip));
        test(!DestinationMatches(spool, L"C:\\Elsewhere\\fedcba9876543210fedcba9876543210.mp4", clip));
        test(!DestinationMatches(spool, L"C:\\Clips\\different.mp4", clip));
        test(!DestinationMatches(spool, L"C:\\Clips\\..\\fedcba9876543210fedcba9876543210.mp4", clip));
        return passed ? count : 0;
    }
}
