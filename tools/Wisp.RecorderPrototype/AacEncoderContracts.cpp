#include "AacEncoder.h"

#include <limits>

namespace recorder::aac
{
    UINT RunContractTests() noexcept
    {
        UINT checks = 0;
        bool passed = true;
        const auto test = [&](bool condition) { ++checks; passed = passed && condition; };
        Configuration config;
        test(ValidateConfiguration(config) == nullptr && config.bitrate == 192000);
        for (const UINT bitrate : { 96000u, 128000u, 160000u, 192000u })
        {
            config.bitrate = bitrate;
            test(ValidateConfiguration(config) == nullptr);
        }
        config.bitrate = 19200;
        test(ValidateConfiguration(config) != nullptr);
        config = {};
        config.maximumSourceFrames = 0;
        test(ValidateConfiguration(config) != nullptr);
        config.maximumSourceFrames = MaximumSourceFrames + 1;
        test(ValidateConfiguration(config) != nullptr);
        config = {};
        config.operationTimeoutMs = 999;
        test(ValidateConfiguration(config) != nullptr);
        config = {};
        config.operationTimeoutMs = 30001;
        test(ValidateConfiguration(config) != nullptr);
        config = {};
        config.epochTime100ns = -1;
        test(ValidateConfiguration(config) != nullptr);
        config.epochTime100ns = (std::numeric_limits<LONGLONG>::max)();
        test(ValidateConfiguration(config) != nullptr);
        config = {};
        LONGLONG time = 0, end = 0;
        test(FrameTime(7000000, 0, time) && time == 7000000);
        test(FrameTime(7000000, SampleRate, time) && time == 17000000);
        test(FrameTime(0, 1, time) && time == 208);
        test(FrameTime(0, CodecFrames, time) && time == 213333);
        test(FrameTime(0, CodecFrames * 3, time) && time == 640000);
        test(FrameTime(0, MaximumSourceFrames, time) && time == 3000000000ll);
        test(!FrameTime(-1, 0, time));
        test(!FrameTime((std::numeric_limits<LONGLONG>::max)(), 1, time));
        test(!FrameTime(0, (std::numeric_limits<std::uint64_t>::max)(), time));
        // A cumulative rational clock avoids adding a truncated AAC duration.
        LONGLONG total = 0;
        bool clockValid = true;
        for (UINT i = 0; i < 300; ++i)
        {
            clockValid = clockValid && FrameTime(0, static_cast<std::uint64_t>(i) * CodecFrames, time) &&
                FrameTime(0, static_cast<std::uint64_t>(i + 1) * CodecFrames, end) && end > time;
            total += end - time;
        }
        test(clockValid && total == 64000000);
        test(ValidateInputBoundary(config, 0, 0, 1));
        test(ValidateInputBoundary(config, 17, 17, MaximumChunkFrames));
        test(!ValidateInputBoundary(config, 17, 18, 20));
        test(!ValidateInputBoundary(config, 17, 16, 20));
        test(!ValidateInputBoundary(config, 0, 0, 0));
        test(!ValidateInputBoundary(config, 0, 0, MaximumChunkFrames + 1));
        test(ValidateInputBoundary(config, MaximumSourceFrames - 1, MaximumSourceFrames - 1, 1));
        test(!ValidateInputBoundary(config, MaximumSourceFrames, MaximumSourceFrames, 1));
        test(!ValidateInputBoundary(config, (std::numeric_limits<std::uint64_t>::max)(),
            (std::numeric_limits<std::uint64_t>::max)(), 1));

        Configuration continuous;
        continuous.mode = SessionMode::UntilStopped;
        test(ValidateConfiguration(continuous) != nullptr); // Explicit zero removes the finite cap.
        continuous.maximumSourceFrames = 0;
        continuous.epochTime100ns = 7000000;
        std::uint64_t continuousLimit = 0;
        test(ValidateConfiguration(continuous) == nullptr &&
            EffectiveSourceFrameLimit(continuous, continuousLimit) && continuousLimit > MaximumSourceFrames);
        test(ValidateInputBoundary(continuous, MaximumSourceFrames, MaximumSourceFrames, MaximumChunkFrames));
        const auto sixHours = 6ull * 60 * 60 * SampleRate;
        test(ValidateInputBoundary(continuous, sixHours, sixHours, MaximumChunkFrames));
        test(FrameTime(continuous.epochTime100ns, sixHours, time) && time == 216007000000ll);
        test(!ValidateInputBoundary(continuous, sixHours, sixHours + 1, 1));
        test(ValidateInputBoundary(continuous, continuousLimit - 1, continuousLimit - 1, 1));
        test(!ValidateInputBoundary(continuous, continuousLimit, continuousLimit, 1));
        test(FrameTime(continuous.epochTime100ns, continuousLimit + CodecFrames, time));
        test(FrameTime(continuous.epochTime100ns, continuousLimit + TailPadding(continuousLimit), time));
        test(!ValidateInputBoundary(continuous, (std::numeric_limits<std::uint64_t>::max)(),
            (std::numeric_limits<std::uint64_t>::max)(), 1));
        continuous.epochTime100ns = (std::numeric_limits<LONGLONG>::max)() - 10000;
        test(ValidateConfiguration(continuous) != nullptr && !EffectiveSourceFrameLimit(continuous, continuousLimit) &&
            continuousLimit == 0);
        continuous.epochTime100ns = -1;
        test(ValidateConfiguration(continuous) != nullptr);
        continuous = {};
        continuous.mode = static_cast<SessionMode>(99);
        test(ValidateConfiguration(continuous) != nullptr && !EffectiveSourceFrameLimit(continuous, continuousLimit));
        std::uint64_t finiteLimit = 0;
        test(EffectiveSourceFrameLimit(config, finiteLimit) && finiteLimit == MaximumSourceFrames);
        constexpr auto maximumCounter = (std::numeric_limits<std::uint64_t>::max)();
        test(CounterAdditionFits(maximumCounter, 0));
        test(CounterAdditionFits(maximumCounter - 1, 1));
        test(!CounterAdditionFits(maximumCounter, 1));
        test(CounterAdditionFits(maximumCounter - CodecFrames, CodecFrames));
        test(!CounterAdditionFits(maximumCounter - CodecFrames + 1, CodecFrames));
        test(CounterAdditionFits(0, maximumCounter));
        test(!CounterAdditionFits(1, maximumCounter));
        test(TailPadding(0) == 0 && TailPadding(1) == 1023);
        test(TailPadding(1023) == 1 && TailPadding(1024) == 0);
        test(TailPadding(96000) == 256 && TailPadding(96000) + 96000 == 94 * CodecFrames);
        MFT_INPUT_STREAM_INFO allocation{};
        test(ValidateInputAllocation(allocation));
        allocation.cbSize = FrameBytes; allocation.cbAlignment = 16;
        test(ValidateInputAllocation(allocation));
        allocation.cbAlignment = 3;
        test(!ValidateInputAllocation(allocation));
        allocation.cbAlignment = 8192;
        test(!ValidateInputAllocation(allocation));
        allocation.cbAlignment = 16; allocation.cbSize = CodecFrames * FrameBytes + 1;
        test(!ValidateInputAllocation(allocation));
        allocation.cbSize = FrameBytes; allocation.dwFlags = MFT_INPUT_STREAM_FIXED_SAMPLE_SIZE;
        test(!ValidateInputAllocation(allocation));
        allocation.cbSize = CodecFrames * FrameBytes;
        test(ValidateInputAllocation(allocation));
        allocation.dwFlags = MFT_INPUT_STREAM_SINGLE_SAMPLE_PER_BUFFER;
        test(!ValidateInputAllocation(allocation));
        allocation.dwFlags = MFT_INPUT_STREAM_PROCESSES_IN_PLACE;
        test(!ValidateInputAllocation(allocation));
        allocation.dwFlags = MFT_INPUT_STREAM_HOLDS_BUFFERS | MFT_INPUT_STREAM_DOES_NOT_ADDREF;
        test(!ValidateInputAllocation(allocation));
        allocation.dwFlags = 0x80000000;
        test(!ValidateInputAllocation(allocation));

        CodecConfiguration codec;
        codec.userDataBytes = 14;
        codec.userData[2] = 0x29;
        codec.userData[12] = 0x11; // AAC-LC, 48 kHz, stereo, 1024 samples.
        codec.userData[13] = 0x90;
        test(ValidateCodecConfiguration(codec) && codec.audioSpecificConfigOffset == 12 &&
            codec.audioSpecificConfigBytes == 2 && codec.userData[12] == 0x11);
        auto malformed = codec;
        malformed.userDataBytes = 13;
        test(!ValidateCodecConfiguration(malformed) && malformed.audioSpecificConfigBytes == 0);
        malformed = codec; malformed.userDataBytes = MaximumConfigBytes + 1;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userDataBytes = 15; malformed.userData[14] = 0x56;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userData[0] = 1;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userData[2] = 0x2c;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userData[4] = 1;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userData[8] = 1;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userData[12] = 0x12;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userData[13] = 0x88;
        test(!ValidateCodecConfiguration(malformed));
        malformed = codec; malformed.userData[13] = 0x94;
        test(!ValidateCodecConfiguration(malformed));

        Options options;
        const wchar_t* help[] = { L"fixture" };
        test(ParseOptions(1, help, options) && options.mode == Mode::Help);
        const wchar_t* self[] = { L"fixture", L"--self-test" };
        test(ParseOptions(2, self, options) && options.mode == Mode::SelfTest);
        const wchar_t* defaults[] = { L"fixture", L"--encode-fixture" };
        test(ParseOptions(2, defaults, options) && options.mode == Mode::Encode && options.pcmFrames == 96000);
        const wchar_t* full[] = { L"fixture", L"--encode-fixture", L"--pcm-frames", L"1", L"--timeout-ms", L"30000" };
        test(ParseOptions(6, full, options) && options.pcmFrames == 1 && options.timeoutMs == 30000);
        const wchar_t* duplicate[] = { L"fixture", L"--encode-fixture", L"--pcm-frames", L"2", L"--pcm-frames", L"3" };
        test(!ParseOptions(6, duplicate, options));
        const wchar_t* malformedNumber[] = { L"fixture", L"--encode-fixture", L"--pcm-frames", L"+1" };
        test(!ParseOptions(4, malformedNumber, options));
        const wchar_t* enormous[] = { L"fixture", L"--encode-fixture", L"--pcm-frames", L"99999999999999999999" };
        test(!ParseOptions(4, enormous, options));
        const wchar_t* missing[] = { L"fixture", L"--encode-fixture", L"--timeout-ms" };
        test(!ParseOptions(3, missing, options));
        const wchar_t* mixed[] = { L"fixture", L"--self-test", L"--encode-fixture" };
        test(!ParseOptions(3, mixed, options));
        const wchar_t* unknown[] = { L"fixture", L"--capture" };
        test(!ParseOptions(2, unknown, options));
        test(!ParseOptions(0, nullptr, options));
        Encoder inert;
        test(!inert.Feed(nullptr, 1, 0) && !inert.Result().activatedMicrosoftAac);
        test(SUCCEEDED(inert.Close()) && SUCCEEDED(inert.Close()));
        Encoder empty;
        test(!empty.Drain() && !empty.Result().activatedMicrosoftAac);
        std::atomic<bool> cancelled{ false };
        config.bitrate = 1;
        Encoder invalid;
        test(!invalid.Initialize(config, cancelled) && !invalid.Result().activatedMicrosoftAac);
        return passed ? checks : 0;
    }
}
