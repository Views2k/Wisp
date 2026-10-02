#include "AacEncoder.h"

#include <mfapi.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <cmath>
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
    HRESULT NoForza() noexcept
    {
        const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        struct Release { HANDLE handle; ~Release() { CloseHandle(handle); } } release{ snapshot };
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        if (!Process32FirstW(snapshot, &entry)) return HRESULT_FROM_WIN32(GetLastError());
        do
        {
            if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") == 0) return HRESULT_FROM_WIN32(ERROR_BUSY);
        } while (Process32NextW(snapshot, &entry));
        return GetLastError() == ERROR_NO_MORE_FILES ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }
    struct Observer final : recorder::aac::PacketObserver
    {
        std::uint64_t packets = 0;
        bool configured = false;
        HRESULT OnConfiguration(const recorder::aac::CodecConfiguration& config, IMFMediaType* type) noexcept override
        {
            if (configured || !type || config.audioSpecificConfigBytes != 2) return E_UNEXPECTED;
            configured = true;
            return S_OK;
        }
        HRESULT OnPacket(const recorder::aac::PacketView& packet) noexcept override
        {
            if (!configured || !packet.data || packet.bytes == 0 || packet.index != packets || packet.duration100ns <= 0)
                return E_UNEXPECTED;
            ++packets;
            return S_OK;
        }
    };
    recorder::aac::Evidence Run(const recorder::aac::Options& options) noexcept
    {
        using namespace recorder::aac;
        Evidence result;
        HRESULT hr = NoForza();
        if (FAILED(hr))
        { result.reason = "game_closed_guard_failed"; result.hr = hr; return result; }
        hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(hr)) { result.reason = "com_initialization_failed"; result.hr = hr; return result; }
        const bool com = SUCCEEDED(hr);
        hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (FAILED(hr))
        {
            result.reason = "mf_startup_failed"; result.hr = hr;
            if (com) CoUninitialize();
            return result;
        }
        {
            Encoder encoder;
            Observer observer;
            Configuration config;
            config.epochTime100ns = 10000000; // Explicit synthetic epoch, not device position.
            config.maximumSourceFrames = options.pcmFrames;
            config.operationTimeoutMs = options.timeoutMs;
            const auto deadline = GetTickCount64() + options.timeoutMs;
            bool okay = encoder.Initialize(config, cancelled, &observer);
            bool guardFailed = false;
            std::array<std::int16_t, MaximumChunkFrames * Channels> pcm{};
            std::uint64_t submitted = 0;
            // Uneven chunks exercise accumulator boundaries without requiring
            // audio devices, playback or a fabricated capture timestamp.
            constexpr std::array<UINT, 4> chunks{ 137, 4800, 1023, 17 };
            UINT chunk = 0;
            while (okay && submitted < options.pcmFrames)
            {
                if (cancelled.load() || GetTickCount64() >= deadline || FAILED(NoForza()))
                { okay = false; guardFailed = true; break; }
                const UINT count = static_cast<UINT>((std::min)(
                    static_cast<std::uint64_t>(chunks[chunk++ % chunks.size()]), options.pcmFrames - submitted));
                constexpr double pi = 3.14159265358979323846;
                for (UINT i = 0; i < count; ++i)
                {
                    const double seconds = static_cast<double>(submitted + i) / SampleRate;
                    pcm[i * Channels] = static_cast<std::int16_t>(std::lround(9000.0 * std::sin(2.0 * pi * 440.0 * seconds)));
                    pcm[i * Channels + 1] = static_cast<std::int16_t>(std::lround(6000.0 * std::sin(2.0 * pi * 660.0 * seconds)));
                }
                okay = encoder.Feed(pcm.data(), count, submitted);
                submitted += count;
            }
            if (okay) okay = encoder.Drain();
            const HRESULT close = encoder.Close();
            result = encoder.Result();
            if (guardFailed)
            { result.reason = "fixture_guard_failed"; result.hr = E_ABORT; }
            if (FAILED(close) || !okay) result.completed = false;
            if (GetTickCount64() >= deadline)
            { result.completed = false; result.reason = "fixture_deadline_reached"; result.hr = HRESULT_FROM_WIN32(WAIT_TIMEOUT); }
            if (result.completed && (!observer.configured || observer.packets != result.outputPackets ||
                result.logicalSourceFrames != options.pcmFrames ||
                result.applicationZeroPaddingFrames != TailPadding(options.pcmFrames) ||
                result.submittedPcmFrames != result.logicalSourceFrames + result.applicationZeroPaddingFrames))
            { result.completed = false; result.reason = "fixture_evidence_mismatch"; result.hr = E_FAIL; }
        }
        hr = MFShutdown();
        if (FAILED(hr)) { result.completed = false; if (SUCCEEDED(result.cleanupHr)) result.cleanupHr = hr; }
        if (com) CoUninitialize();
        return result;
    }
}

int wmain(int argc, wchar_t** argv)
{
    using namespace recorder::aac;
    Options options;
    if (!ParseOptions(argc, argv, options))
    { std::cout << "{\"completed\":false,\"reason\":\"invalid_arguments\"}\n"; return 2; }
    if (options.mode == Mode::Help)
    {
        std::cout << "Synthetic PCM16 48 kHz stereo -> Microsoft AAC-LC, 192 kbps. No audio devices or playback.\n"
            "Default/help activate no resources. --self-test runs CPU contracts only.\n"
            "--encode-fixture [--pcm-frames 1..480000] [--timeout-ms 1000..30000]\n"
            "Defaults: 96000 PCM frames, 10000 ms. Requires Forza closed.\n"
            "Codec calls require an external watchdog in addition to internal deadlines.\n"
            "Output verifies packets and drain, not decode, priming, audio quality or live performance.\n";
        return 0;
    }
    if (options.mode == Mode::SelfTest)
    {
        const auto passed = RunContractTests();
        std::cout << "{\"mode\":\"cpu_contracts\",\"passed\":" << passed
            << ",\"encoderActivated\":false,\"audioDeviceActivated\":false}\n";
        return passed ? 0 : 1;
    }
    if (!SetConsoleCtrlHandler(Cancel, TRUE))
    { std::cout << "{\"completed\":false,\"reason\":\"cancel_handler_failed\"}\n"; return 3; }
    const auto result = Run(options);
    (void)SetConsoleCtrlHandler(Cancel, FALSE);
    std::cout << std::boolalpha << "{\"mode\":\"synthetic_pcm16_microsoft_aac\",\"completed\":" << result.completed
        << ",\"reason\":\"" << result.reason << "\",\"hresult\":" << static_cast<std::uint32_t>(result.hr)
        << ",\"cleanupHresult\":" << static_cast<std::uint32_t>(result.cleanupHr)
        << ",\"sampleRate\":" << SampleRate << ",\"channels\":" << Channels << ",\"bitsPerSample\":" << BitsPerSample
        << ",\"configuredBitrate\":" << result.configuredBitrate
        << ",\"inputMinimumBytes\":" << result.inputMinimumBytes << ",\"inputAlignmentBytes\":" << result.inputAlignmentBytes
        << ",\"inputStreamFlags\":" << result.inputStreamFlags
        << ",\"activatedMicrosoftAac\":" << result.activatedMicrosoftAac << ",\"synchronous\":" << result.synchronous
        << ",\"negotiated\":" << result.negotiated << ",\"codecConfigurationVerified\":" << result.codecConfigurationVerified
        << ",\"configurationBytes\":" << result.configurationBytes << ",\"audioSpecificConfigBytes\":" << result.audioSpecificConfigBytes
        << ",\"logicalSourceFrames\":" << result.logicalSourceFrames << ",\"submittedPcmFrames\":" << result.submittedPcmFrames
        << ",\"applicationZeroPaddingFrames\":" << result.applicationZeroPaddingFrames
        << ",\"inputSamples\":" << result.inputSamples << ",\"outputPackets\":" << result.outputPackets
        << ",\"outputBytes\":" << result.outputBytes << ",\"checksumFnv1a64\":\"" << std::hex << std::setw(16)
        << std::setfill('0') << result.checksumFnv1a64 << std::dec << "\",\"firstOutputOffset100ns\":" << result.firstOutputOffset100ns
        << ",\"lastOutputEndOffset100ns\":" << result.lastOutputEndOffset100ns
        << ",\"minimumOutputDuration100ns\":" << result.minimumOutputDuration100ns
        << ",\"maximumOutputDuration100ns\":" << result.maximumOutputDuration100ns
        << ",\"nonContiguousOutputBoundaries\":" << result.nonContiguousOutputBoundaries
        << ",\"drainComplete\":" << result.drainComplete << ",\"codecDelayKnown\":" << result.codecDelayKnown
        << ",\"codecTrailingPaddingKnown\":" << result.codecTrailingPaddingKnown
        << ",\"audioDeviceActivated\":false,\"playbackUsed\":false,\"decodedOutputVerified\":false,\"gameplayPerformanceVerified\":false}\n";
    return result.completed ? 0 : 3;
}
