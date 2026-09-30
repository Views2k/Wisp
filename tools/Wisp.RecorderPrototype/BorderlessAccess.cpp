#include "BorderlessAccess.h"

#include <windows.h>
#include <roapi.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <string_view>
#include <thread>

namespace recorder::capture
{
    namespace
    {
        using winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus;
        constexpr DWORD DeadlineMs = 10000;
        constexpr std::string_view Request = "request-borderless-v1\n";
        const char* Outcome(AppCapabilityAccessStatus status) noexcept
        {
            switch (status)
            {
            case AppCapabilityAccessStatus::Allowed: return "allowed";
            case AppCapabilityAccessStatus::DeniedBySystem:
            case AppCapabilityAccessStatus::DeniedByUser: return "denied";
            default: return "unavailable";
            }
        }
        const char* ResultLine(const char* outcome) noexcept
        {
            if (std::strcmp(outcome, "allowed") == 0)
                return "{\"v\":1,\"mode\":\"borderless_access\",\"status\":\"allowed\"}\n";
            if (std::strcmp(outcome, "denied") == 0)
                return "{\"v\":1,\"mode\":\"borderless_access\",\"status\":\"denied\"}\n";
            return "{\"v\":1,\"mode\":\"borderless_access\",\"status\":\"unavailable\"}\n";
        }
        bool IsRequest(std::string_view request) noexcept { return request == Request; }
    }

    unsigned RunBorderlessAccessContracts() noexcept
    {
        unsigned count = 0;
        bool passed = true;
        const auto test = [&](bool value) { ++count; passed = passed && value; };
        test(IsRequest(Request));
        test(!IsRequest("request-borderless-v1\r\n") && !IsRequest("request-borderless-v1"));
        test(!IsRequest("request-borderless-v1\nextra") && !IsRequest(""));
        test(std::strcmp(Outcome(AppCapabilityAccessStatus::Allowed), "allowed") == 0);
        test(std::strcmp(Outcome(AppCapabilityAccessStatus::DeniedByUser), "denied") == 0);
        test(std::strcmp(Outcome(AppCapabilityAccessStatus::NotDeclaredByApp), "unavailable") == 0);
        test(std::strcmp(Outcome(static_cast<AppCapabilityAccessStatus>(999)), "unavailable") == 0);
        test(std::strstr(ResultLine("unknown"), "\"status\":\"unavailable\"") != nullptr);
        return passed ? count : 0;
    }

    int RunBorderlessAccessStdio() noexcept
    {
        const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
        if (!input || !output || GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE) return 2;
        const HANDLE completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!completed) return 2;
        std::thread watchdog;
        try
        {
            watchdog = std::thread([completed]
            {
                if (WaitForSingleObject(completed, DeadlineMs) != WAIT_OBJECT_0)
                    (void)TerminateProcess(GetCurrentProcess(), 30);
            });
        }
        catch (...) { (void)CloseHandle(completed); return 2; }

        const auto began = GetTickCount64();
        const char* outcome = "unavailable";
        int exitCode = 0;
        bool initialized = false;
        try
        {
            std::array<char, 64> request{};
            std::size_t used = 0;
            for (;;)
            {
                DWORD received = 0;
                const BOOL read = ReadFile(input, request.data() + used, static_cast<DWORD>(request.size() - used), &received, nullptr);
                if (!read)
                {
                    if (GetLastError() == ERROR_BROKEN_PIPE) break;
                    throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
                }
                if (!received) break;
                used += received;
                if (used > Request.size()) throw winrt::hresult_invalid_argument();
            }
            if (!IsRequest({ request.data(), used })) throw winrt::hresult_invalid_argument();
            winrt::check_hresult(RoInitialize(RO_INIT_MULTITHREADED)); initialized = true;
            // Microsoft documents a package capability for borderless access.
            // An unpackaged helper can be denied/unavailable; never assume a
            // grant or modify packaging/security to obtain one.
            auto requestAccess = winrt::Windows::Graphics::Capture::GraphicsCaptureAccess::RequestAccessAsync(
                winrt::Windows::Graphics::Capture::GraphicsCaptureAccessKind::Borderless);
            const auto elapsed = GetTickCount64() - began;
            const auto remaining = elapsed < DeadlineMs - 500 ? DeadlineMs - 500 - elapsed : 0;
            if (remaining && requestAccess.wait_for(std::chrono::milliseconds(static_cast<std::int64_t>(remaining))) == winrt::Windows::Foundation::AsyncStatus::Completed)
                outcome = Outcome(requestAccess.GetResults());
            else requestAccess.Cancel();
            requestAccess.Close();
        }
        catch (...) { outcome = "unavailable"; }
        if (initialized) RoUninitialize();
        const auto* line = ResultLine(outcome);
        const auto bytes = static_cast<DWORD>(std::strlen(line));
        DWORD written = 0;
        if (!WriteFile(output, line, bytes, &written, nullptr) || written != bytes) exitCode = 2;
        if (!SetEvent(completed)) (void)TerminateProcess(GetCurrentProcess(), 30);
        watchdog.join();
        if (!CloseHandle(completed)) exitCode = 2;
        return exitCode;
    }
}
