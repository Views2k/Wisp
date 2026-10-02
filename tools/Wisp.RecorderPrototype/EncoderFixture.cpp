#include "HardwareEncoder.h"

#include <iomanip>
#include <iostream>

namespace
{
    std::atomic<bool> cancelled{ false };
    BOOL WINAPI Cancel(DWORD signal) noexcept
    {
        if (signal != CTRL_C_EVENT && signal != CTRL_BREAK_EVENT) return FALSE;
        cancelled.store(true);
        return TRUE;
    }
}

int wmain(int argc, wchar_t** argv)
{
    using namespace recorder::encoder;
    Options options;
    if (!ParseOptions(argc, argv, options))
    {
        std::cout << "{\"completed\":false,\"reason\":\"invalid_arguments\"}\n";
        return 2;
    }
    if (options.mode == Mode::Help)
    {
        std::cout << "Headless synthetic hardware H264 fixture; no capture or file output.\n"
            "Default/help initialize no COM, MF or GPU resources.\n"
            "--self-test (CPU-only parser, ownership and media-time contracts)\n"
            "--encode-fixture [--adapter-index 0..15] [--encoder-index 0..15]\n"
            "  [--frames 1..150] [--timeout-ms 1000..30000]\n"
            "Defaults: adapter 0, candidate 0, 60 frames, 10000 ms; fixed 1080p30.\n"
            "Requires Forza closed. Ctrl+C cancels; supervise with an external deadline.\n"
            "Synthetic success is not a decode, color, gameplay or performance validation.\n";
        return 0;
    }
    if (options.mode == Mode::SelfTest)
    {
        const UINT passed = RunContractTests();
        std::cout << "{\"mode\":\"cpu_contracts\",\"passed\":" << passed
            << ",\"graphicsInitialized\":false,\"encoderActivated\":false}\n";
        return passed ? 0 : 1;
    }
    if (!SetConsoleCtrlHandler(Cancel, TRUE))
    {
        std::cout << "{\"completed\":false,\"reason\":\"cancel_handler_failed\"}\n";
        return 3;
    }
    const Evidence result = RunSyntheticFixture(options, cancelled);
    (void)SetConsoleCtrlHandler(Cancel, FALSE);
    std::cout << std::boolalpha
        << "{\"mode\":\"synthetic_nv12_hardware_h264\",\"completed\":" << result.completed
        << ",\"reason\":\"" << result.reason << "\",\"hresult\":" << static_cast<std::uint32_t>(result.hr)
        << ",\"cleanupHresult\":" << static_cast<std::uint32_t>(result.cleanupHr)
        << ",\"width\":" << Width << ",\"height\":" << Height << ",\"frameRate\":" << FrameRate
        << ",\"configuredBitrate\":" << Bitrate << ",\"poolSize\":" << PoolSize
        << ",\"hardwareCandidates\":" << result.hardwareCandidates
        << ",\"hardwareRegistration\":" << result.hardwareRegistration
        << ",\"activatedHardwareAttribute\":" << result.activatedHardwareAttribute
        << ",\"asynchronous\":" << result.asynchronous << ",\"d3d11Aware\":" << result.d3d11Aware
        << ",\"sameDeviceManager\":" << result.sameDeviceManager << ",\"baselineProfile\":" << result.baselineProfile
        << ",\"bPictureControlSupported\":" << result.bPictureControlSupported
        << ",\"bPictureZeroReadback\":" << result.bPictureZeroReadback
        << ",\"lowLatencySupported\":" << result.lowLatencySupported
        << ",\"lowLatencyReadback\":" << result.lowLatencyReadback
        << ",\"lookaheadDepthVerified\":false,\"decodedOutputVerified\":false,\"hdrConversionVerified\":false"
        << ",\"inputAttributeStoreAvailable\":" << result.inputAttributeStoreAvailable
        << ",\"inputBindHintAvailable\":" << result.inputBindHintAvailable
        << ",\"inputBindFlags\":" << result.inputBindFlags
        << ",\"submittedFrames\":" << result.submitted << ",\"outputSamples\":" << result.outputSamples
        << ",\"cleanPoints\":" << result.cleanPoints << ",\"missingCleanPointAttributes\":" << result.missingCleanPointAttributes
        << ",\"sequenceHeaderBytes\":" << result.sequenceHeaderBytes
        << ",\"encodedBytes\":" << result.encodedBytes << ",\"encodedChecksumFnv1a64\":\""
        << std::hex << std::setw(16) << std::setfill('0') << result.checksumFnv1a64 << std::dec
        << "\",\"needInputEvents\":" << result.needInputEvents << ",\"haveOutputEvents\":" << result.haveOutputEvents
        << ",\"drainComplete\":" << result.drainComplete << ",\"samplesReturned\":" << result.samplesReturned
        << ",\"returnedSamples\":" << result.returnedSamples << ",\"peakOwnedSamples\":" << result.peakOwnedSamples
        << ",\"eventCallbackDrained\":" << result.eventCallbackDrained << ",\"elapsedMs\":" << result.elapsedMs
        << ",\"captureUsed\":false,\"softwareFallback\":false,\"gameplayPerformanceVerified\":false}\n";
    return result.completed ? 0 : 3;
}
