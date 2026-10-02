#include "RecorderHost.h"
#include "ClipThumbnail.h"
#include "BorderlessAccess.h"
#include "GameScreenCapture.h"

#include <cwchar>
#include <iostream>

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && std::wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "Wisp recorder child. --stdio-protocol: explicit managed-child entry.\n"
            "--thumbnail-stdio: explicit local saved-clip thumbnail child.\n"
            "--borderless-access-stdio: explicit capture-border permission request; no capture.\n"
            "--self-test: pure host policy contracts; no capture or audio activation.\n";
        return 0;
    }
    if (argc == 2 && std::wcscmp(argv[1], L"--self-test") == 0)
    {
        const auto checks = recorder::host::RunPolicyContracts();
        const auto diagnostics = recorder::host::RunDiagnosticContracts();
        const auto borderless = recorder::capture::RunBorderlessAccessContracts();
        const auto screen = recorder::capture::RunScreenCaptureContracts();
        unsigned thumbnails = 0;
        try { thumbnails = recorder::thumbnail::RunContracts(); }
        catch (...) { /* A zero count reports a failed pure contract. */ }
        std::cout << "{\"mode\":\"recorder_host_policy_contracts\",\"passed\":" << checks
            << ",\"thumbnailContractsPassed\":" << thumbnails
            << ",\"diagnosticContractsPassed\":" << diagnostics
            << ",\"borderlessContractsPassed\":" << borderless
            << ",\"screenContractsPassed\":" << screen
            << ",\"captureUsed\":false,\"audioActivated\":false}\n";
        return checks && thumbnails && diagnostics && borderless && screen ? 0 : 1;
    }
    if (argc == 2 && std::wcscmp(argv[1], L"--stdio-protocol") == 0)
        return recorder::host::RunStdioRecorder();
    if (argc == 2 && std::wcscmp(argv[1], L"--thumbnail-stdio") == 0)
        return recorder::thumbnail::RunStdio();
    if (argc == 2 && std::wcscmp(argv[1], L"--borderless-access-stdio") == 0)
        return recorder::capture::RunBorderlessAccessStdio();
    return 2;
}
