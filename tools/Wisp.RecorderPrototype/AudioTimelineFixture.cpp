#include "AudioTimeline.h"
#include <cstdio>
#include <cwchar>

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--help") == 0))
    {
        std::puts("Wisp audio timeline contracts. --self-test; no audio, files or graphics.");
        return 0;
    }
    if (argc != 2 || wcscmp(argv[1], L"--self-test") != 0) return 2;
    unsigned failed = 0;
    const auto checks = recorder::audio::RunAudioTimelineContracts(&failed);
    std::printf("{\"contracts\":%u,\"failedCheck\":%u,\"audioActivated\":false}\n", checks, failed);
    return checks ? 0 : 1;
}
