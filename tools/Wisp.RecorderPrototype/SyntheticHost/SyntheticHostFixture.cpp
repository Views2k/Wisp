#include "RecorderHost.h"
#include "RecorderProtocol.h"
#include "SyntheticTiming.h"
#include <windows.h>
#include <winternl.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <string>
#include <thread>
#include <vector>

#if !defined(WISP_SYNTHETIC_HOST_FIXTURE)
#error This fixture must never be linked into the product recorder.
#endif

namespace recorder::capture
{
    int RunSyntheticShaderCheck() noexcept;
    void ConfigureSyntheticPattern(UINT cellSize) noexcept;
    std::uint64_t SyntheticFrameCount() noexcept;
}

namespace
{
    constexpr std::uint64_t GiB = 1024ull * 1024 * 1024;
    constexpr char Session[] = "11111111111141118111111111111111";
    constexpr char Clip[] = "22222222222242228222222222222222";
#if defined(WISP_SYNTHETIC_HOST_HDR)
    constexpr UINT OutputHeight = 1080;
    constexpr bool HdrInput = true;
#else
    constexpr UINT OutputHeight = 2160;
    constexpr bool HdrInput = false;
#endif
    struct Failure { const char* reason; };
    void Need(bool value, const char* reason) { if (!value) throw Failure{ reason }; }
    [[noreturn]] void Kill(UINT code) noexcept { (void)TerminateProcess(GetCurrentProcess(), code); std::terminate(); }
    struct Handle
    {
        HANDLE value = nullptr;
        Handle() = default;
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        ~Handle() { Close(); }
        void Close() noexcept { if (value && value != INVALID_HANDLE_VALUE) (void)CloseHandle(value); value = nullptr; }
    };
    std::wstring Extended(const std::wstring& path) { return L"\\\\?\\" + path; }
    std::uint64_t Creation()
    {
        FILETIME creation{}, exit{}, kernel{}, user{};
        Need(GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user) != FALSE, "process_identity_failed");
        return (static_cast<std::uint64_t>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
    }
    bool AppsClosed() noexcept
    {
        Handle snapshot; snapshot.value = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.value == INVALID_HANDLE_VALUE) return false;
        PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
        if (!Process32FirstW(snapshot.value, &entry)) return false;
        do
        {
            for (const auto* name : { L"Wisp.exe", L"Wisp.Recorder.exe", L"ForzaHorizon6.exe", L"ForzaHorizon5.exe", L"ForzaHorizon4.exe", L"ForzaMotorsport.exe" })
                if (_wcsicmp(entry.szExeFile, name) == 0) return false;
        } while (Process32NextW(snapshot.value, &entry));
        return GetLastError() == ERROR_NO_MORE_FILES;
    }
    struct OwnedOutput
    {
        std::wstring path;
        std::vector<HANDLE> parents;
        ~OwnedOutput() { for (auto value : parents) (void)CloseHandle(value); }
        void Hold(const std::filesystem::path& directory)
        {
            const auto name = Extended(directory.wstring());
            const HANDLE handle = CreateFileW(name.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
            Need(handle != INVALID_HANDLE_VALUE, "output_parent_open_failed");
            parents.push_back(handle);
            BY_HANDLE_FILE_INFORMATION info{};
            Need(GetFileInformationByHandle(handle, &info) && (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
                !(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT), "output_parent_not_regular");
        }
        void Prepare(const wchar_t* requested)
        {
            const std::filesystem::path supplied(requested);
            Need(supplied.is_absolute(), "absolute_output_required");
            path = supplied.lexically_normal().wstring();
            Need(path.size() > 3 && path.size() < 220 && path[1] == L':' && path[2] == L'\\' &&
                path.find(L':', 2) == std::wstring::npos && path.find_first_of(L"\r\n\"") == std::wstring::npos &&
                path.back() != L'.' && path.back() != L' ' && path.back() != L'\\', "local_output_required");
            std::array<wchar_t, 32768> module{};
            const auto length = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
            Need(length > 0 && length < module.size(), "module_location_failed");
            auto checkout = std::filesystem::path(module.data()).parent_path();
            while (!checkout.empty() && !std::filesystem::is_regular_file(checkout / L"Wisp.sln"))
            { const auto next = checkout.parent_path(); if (next == checkout) break; checkout = next; }
            Need(std::filesystem::is_regular_file(checkout / L"Wisp.sln"), "checkout_required");
            const auto work = (checkout / L"work").wstring() + L"\\";
            Need(path.size() > work.size() && _wcsnicmp(path.c_str(), work.c_str(), work.size()) == 0, "checkout_work_output_required");
            Need(GetDriveTypeW(path.substr(0, 3).c_str()) == DRIVE_FIXED, "fixed_local_drive_required");
            std::vector<std::filesystem::path> chain;
            auto parent = supplied.lexically_normal().parent_path();
            for (auto part = parent; !part.empty();)
            { chain.push_back(part); const auto next = part.parent_path(); if (next == part) break; part = next; }
            for (auto i = chain.rbegin(); i != chain.rend(); ++i) Hold(*i);
            ULARGE_INTEGER free{};
            Need(GetDiskFreeSpaceExW(Extended(parent.wstring()).c_str(), &free, nullptr, nullptr) != FALSE && free.QuadPart >= 16 * GiB,
                "sixteen_gib_free_required");
            Need(CreateDirectoryW(Extended(path).c_str(), nullptr) != FALSE, "fresh_output_required");
            Hold(std::filesystem::path(path));
        }
        HANDLE Create(const wchar_t* name)
        {
            const HANDLE file = CreateFileW(Extended(path + L"\\" + name).c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            Need(file != INVALID_HANDLE_VALUE, "new_report_failed"); return file;
        }
    };
    void ValidateInventoryHandle(HANDLE handle, bool directory)
    {
        FILE_ATTRIBUTE_TAG_INFO info{};
        Need(GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &info, sizeof(info)) &&
            !(info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) &&
            ((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == directory && GetFileType(handle) == FILE_TYPE_DISK,
            "output_inventory_not_regular");
    }
    bool OpenInventoryChild(Handle& output, HANDLE parent, const std::wstring& name, bool directory)
    {
        Need(!name.empty() && name.size() <= 255 && name.find_first_of(L"\\/:") == std::wstring::npos &&
            name.find(L'\0') == std::wstring::npos, "output_inventory_name_invalid");
        UNICODE_STRING text{}; text.Buffer = const_cast<PWSTR>(name.data());
        text.Length = static_cast<USHORT>(name.size() * sizeof(wchar_t)); text.MaximumLength = text.Length;
        OBJECT_ATTRIBUTES attributes{};
        InitializeObjectAttributes(&attributes, &text, OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE, parent, nullptr);
        IO_STATUS_BLOCK status{};
        const NTSTATUS result = NtCreateFile(&output.value, FILE_READ_ATTRIBUTES | SYNCHRONIZE |
            (directory ? FILE_LIST_DIRECTORY : 0), &attributes, &status, nullptr, 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, FILE_OPEN,
            FILE_SYNCHRONOUS_IO_NONALERT | FILE_OPEN_REPARSE_POINT | (directory ? FILE_DIRECTORY_FILE : FILE_NON_DIRECTORY_FILE), nullptr, 0);
        if (result < 0)
        {
            output.value = nullptr;
            const auto error = RtlNtStatusToDosError(result);
            // A concurrent normal stop can remove an entry after enumeration.
            if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND || error == ERROR_DELETE_PENDING) return false;
            throw Failure{ "output_inventory_open_failed" };
        }
        ValidateInventoryHandle(output.value, directory); return true;
    }
    std::uint64_t Size(HANDLE directory, UINT depth, UINT& entries)
    {
        Need(depth < 4, "output_depth_invalid");
        alignas(8) std::array<std::uint8_t, 16384> buffer{};
        std::uint64_t total = 0;
        while (GetFileInformationByHandleEx(directory, FileIdBothDirectoryInfo, buffer.data(), static_cast<DWORD>(buffer.size())))
        {
            std::size_t offset = 0;
            for (;;)
            {
                constexpr auto header = offsetof(FILE_ID_BOTH_DIR_INFO, FileName);
                Need(offset <= buffer.size() - header, "output_inventory_entry_invalid");
                const auto& entry = *reinterpret_cast<const FILE_ID_BOTH_DIR_INFO*>(buffer.data() + offset);
                Need(++entries <= 8192 && entry.FileNameLength > 0 && entry.FileNameLength <= 510 &&
                    entry.FileNameLength % sizeof(wchar_t) == 0 && entry.FileNameLength <= buffer.size() - offset - header,
                    "output_inventory_entry_invalid");
                const std::wstring name(entry.FileName, entry.FileNameLength / sizeof(wchar_t));
                if (name != L"." && name != L"..")
                {
                    Need(!(entry.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT), "output_inventory_not_regular");
                    const bool isDirectory = (entry.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    Handle child;
                    if (OpenInventoryChild(child, directory, name, isDirectory))
                    {
                        LARGE_INTEGER end{};
                        std::uint64_t bytes = 0;
                        if (isDirectory) bytes = Size(child.value, depth + 1, entries);
                        else
                        {
                            Need(GetFileSizeEx(child.value, &end) && end.QuadPart >= 0, "output_inventory_size_failed");
                            bytes = static_cast<std::uint64_t>(end.QuadPart);
                        }
                        Need(bytes <= 16 * GiB - total, "output_limit_exceeded"); total += bytes;
                    }
                }
                if (!entry.NextEntryOffset) break;
                Need(entry.NextEntryOffset % 8 == 0 && entry.NextEntryOffset >= header + entry.FileNameLength &&
                    entry.NextEntryOffset <= buffer.size() - offset - header, "output_inventory_entry_invalid");
                offset += entry.NextEntryOffset;
            }
        }
        Need(GetLastError() == ERROR_NO_MORE_FILES, "output_inventory_failed"); return total;
    }
    std::uint64_t Size(const std::wstring& path)
    {
        Handle directory;
        directory.value = CreateFileW(Extended(path).c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        Need(directory.value != INVALID_HANDLE_VALUE, "output_inventory_open_failed");
        ValidateInventoryHandle(directory.value, true);
        UINT entries = 0; return Size(directory.value, 0, entries);
    }
    void Write(HANDLE file, const std::string& text)
    {
        Need(text.size() <= 65536, "write_bound"); DWORD written = 0;
        Need(WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) && written == text.size(), "write_failed");
    }
    int CheckOpenFileSize(const wchar_t* requested)
    {
        Handle stop; stop.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!stop.value) return 2;
        std::thread deadline([&] { if (WaitForSingleObject(stop.value, 10000) != WAIT_OBJECT_0) Kill(124); });
        bool passed = false;
        try
        {
            OwnedOutput output; output.Prepare(requested);
            Handle writer; writer.value = output.Create(L"buffered-writer.bin");
            const std::string bytes(65536, 'x');
            Write(writer.value, bytes.substr(0, 8192));
            const auto first = Size(output.path);
            Write(writer.value, bytes);
            const auto second = Size(output.path);
            Need(first == 8192 && second == 73728, "open_writer_eof_not_observed");
            // Both measurements happen while the buffered writer is open, without a flush.
            Handle report; report.value = output.Create(L"size-check.json");
            Write(report.value, "{\"mode\":\"open_writer_size_check\",\"passed\":true,\"firstBytes\":" +
                std::to_string(first) + ",\"secondBytes\":" + std::to_string(second) +
                ",\"writerOpenDuringMeasurement\":true,\"flushed\":false,\"graphicsActivated\":false}\n");
            passed = true;
        }
        catch (...) { std::puts("Open writer size check failed."); }
        (void)SetEvent(stop.value); deadline.join();
        if (passed) std::puts("Open writer size check passed.");
        return passed ? 0 : 3;
    }
    std::string JsonPath(const std::wstring& text)
    {
        std::string value = "\"";
        for (const wchar_t c : text)
        {
            if (c == L'\\') value += "\\\\";
            else if (c >= 32 && c < 127 && c != L'\"') value.push_back(static_cast<char>(c));
            else { char escaped[7]{}; (void)std::snprintf(escaped, sizeof(escaped), "\\u%04x", static_cast<unsigned>(c)); value += escaped; }
        }
        return value + '"';
    }
    std::string Common(int request, const char* command)
    { return "{\"v\":2,\"session\":\"" + std::string(Session) + "\",\"request\":" + std::to_string(request) + ",\"command\":\"" + command + '"'; }
    void Command(HANDLE input, const std::string& text)
    {
        recorder::protocol::Command parsed;
        Need(recorder::protocol::ParseCommand(text, parsed), "fixture_command_invalid");
        Write(input, text + '\n');
    }
    struct Pipe
    {
        Handle read, write;
        Pipe() { Need(CreatePipe(&read.value, &write.value, nullptr, 65536) != FALSE, "fixture_pipe_failed"); }
    };
    struct Observation
    {
        bool start = false, buffering = false, saveResult = false, saved = false, stopped = false, audio = false, bufferingAudio = false;
        std::string pending;
        std::uint64_t bytes = 0;
        std::uint64_t payloadBytes = 0, payloadDuration100ns = 0, bufferSamples = 0;
        bool pauseMode = false, pausedReady = false, resumeAccepted = false, resumedBuffering = false;
        bool pauseBeforeFirstSave = false;
        unsigned pauseAcknowledgments = 0;
        std::array<bool, 3> pauseSaves{};
        std::array<std::uint64_t, 3> pauseStarts{}, pauseEnds{};
        static std::uint64_t Number(const std::string& text, const char* key)
        {
            const auto at = text.find(key); Need(at != std::string::npos, "buffer_scalar_missing");
            const auto first = at + std::strlen(key);
            const auto end = text.find_first_not_of("0123456789", first);
            Need(end != std::string::npos && end > first && end - first <= 20, "buffer_scalar_invalid");
            std::uint64_t value = 0;
            const auto parsed = std::from_chars(text.data() + first, text.data() + end, value);
            Need(parsed.ec == std::errc{} && parsed.ptr == text.data() + end, "buffer_scalar_invalid");
            return value;
        }
        void Read(Pipe& pipe, HANDLE log, bool protocol)
        {
            for (UINT i = 0; i < 16; ++i)
            {
                DWORD available = 0;
                if (!PeekNamedPipe(pipe.read.value, nullptr, 0, nullptr, &available, nullptr))
                { Need(GetLastError() == ERROR_BROKEN_PIPE, "pipe_probe_failed"); return; }
                if (!available) return;
                std::array<char, 16384> buffer{}; DWORD count = 0;
                Need(ReadFile(pipe.read.value, buffer.data(), (std::min)(available, static_cast<DWORD>(buffer.size())), &count, nullptr) != FALSE,
                    "pipe_read_failed");
                bytes += count; Need(bytes <= 2 * 1024 * 1024, "diagnostic_output_bound");
                Write(log, std::string(buffer.data(), count));
                if (!protocol) continue;
                pending.append(buffer.data(), count); Need(pending.size() <= 65536, "protocol_pending_bound");
                for (auto newline = pending.find('\n'); newline != std::string::npos; newline = pending.find('\n'))
                {
                    const auto line = pending.substr(0, newline); pending.erase(0, newline + 1);
                    if (line.find("\"type\":\"result\"") != std::string::npos)
                    {
                        const bool okay = line.find("\"ok\":true") != std::string::npos;
                        if (line.find("\"request\":2,") != std::string::npos) start = okay;
                        if (pauseMode)
                        {
                            const auto request = Number(line, "\"request\":");
                            Need(okay, "pause_fixture_command_refused");
                            if (request == 3 || request == 5 || request == 8)
                            {
                                const std::size_t index = request == 3 ? 0 : request == 5 ? 1 : 2;
                                Need(line.find("\"hasAudio\":true") != std::string::npos, "pause_fixture_audio_missing");
                                if (HdrInput) Need(line.find("\"hdrVideo\":true") != std::string::npos, "pause_fixture_hdr_missing");
                                Need(Number(line, "\"fileBytes\":") > 0, "pause_fixture_media_missing");
                                pauseSaves[index] = true;
                                pauseStarts[index] = Number(line, "\"start100ns\":");
                                pauseEnds[index] = Number(line, "\"end100ns\":");
                                saved = audio = pauseSaves[0] && pauseSaves[1] && pauseSaves[2];
                            }
                            if (request == 4 || request == 7)
                            {
                                if (request == 4) pauseBeforeFirstSave = !pauseSaves[0];
                                ++pauseAcknowledgments;
                            }
                            if (request == 6) resumeAccepted = true;
                            if (request == 9) stopped = true;
                        }
                        else if (line.find("\"request\":3,") != std::string::npos)
                        { saveResult = true; saved = okay && line.find("\"fileBytes\":") != std::string::npos; audio = line.find("\"hasAudio\":true") != std::string::npos; }
                        if (!pauseMode && line.find("\"request\":4,") != std::string::npos) stopped = okay;
                    }
                    if (line.find("\"state\":\"paused\"") != std::string::npos)
                        pausedReady = line.find("\"bufferReady\":true") != std::string::npos;
                    if (line.find("\"state\":\"buffering\"") != std::string::npos)
                    {
                        buffering = true;
                        if (resumeAccepted) resumedBuffering = true;
                        bufferingAudio |= line.find("\"reason\":\"none\"") != std::string::npos;
                        if (line.find("\"losslessBuffer\":{") != std::string::npos)
                        {
                            payloadBytes = Number(line, "\"payloadBytes\":");
                            payloadDuration100ns = Number(line, "\"duration100ns\":");
                            Need(payloadBytes > 0 && payloadBytes <= 16 * GiB && payloadDuration100ns > 0 &&
                                payloadDuration100ns <= 30ull * 10000000, "buffer_sample_outside_fixture_bounds");
                            ++bufferSamples;
                        }
                    }
                }
            }
        }
    };
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 4 && std::wcscmp(argv[1], L"--size-check") == 0 && std::wcscmp(argv[2], L"--output") == 0)
        return CheckOpenFileSize(argv[3]);
    if (argc == 2 && std::wcscmp(argv[1], L"--shader-check") == 0)
    {
        Handle stop; stop.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!stop.value) return 2;
        std::thread limit([&] { if (WaitForSingleObject(stop.value, 10000) != WAIT_OBJECT_0) Kill(124); });
        const int result = recorder::capture::RunSyntheticShaderCheck();
        (void)SetEvent(stop.value); limit.join(); return result;
    }
    if ((argc != 5 && argc != 6 && argc != 7) || std::wcscmp(argv[1], L"--output") != 0 || std::wcscmp(argv[3], L"--mode") != 0 ||
        (std::wcscmp(argv[4], L"lossless") != 0 && std::wcscmp(argv[4], L"compressed") != 0))
    { std::puts("--output <fresh checkout work directory> --mode lossless|compressed [--pause-resume | --pattern high-output|varying-8x8]"); return 1; }
    const bool pauseMode = argc == 6 && std::wcscmp(argv[5], L"--pause-resume") == 0;
    if (argc == 6 && !pauseMode) return 1;
    if (HdrInput && !pauseMode) { std::puts("Generated HDR mode requires --pause-resume."); return 1; }
    const bool highOutput = argc == 7;
    if (highOutput && (std::wcscmp(argv[5], L"--pattern") != 0 ||
        (std::wcscmp(argv[6], L"high-output") != 0 && std::wcscmp(argv[6], L"varying-8x8") != 0) ||
        std::wcscmp(argv[4], L"lossless") != 0)) return 1;
    const UINT detailCellSize = highOutput ? (std::wcscmp(argv[6], L"varying-8x8") == 0 ? 8u : 2u) : 0u;
    recorder::capture::ConfigureSyntheticPattern(detailCellSize);
    Handle deadlineStop; deadlineStop.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!deadlineStop.value) return 2;
    std::thread deadline([&] { if (WaitForSingleObject(deadlineStop.value, 58000) != WAIT_OBJECT_0) Kill(124); });
    std::thread host;
    OwnedOutput output;
    Handle protocolLog, diagnosticLog, report;
    Pipe input, nativeOutput, nativeError;
    const HANDLE oldInput = GetStdHandle(STD_INPUT_HANDLE), oldOutput = GetStdHandle(STD_OUTPUT_HANDLE), oldError = GetStdHandle(STD_ERROR_HANDLE);
    bool redirected = false, saveSent = false, stopSent = false;
    std::atomic<bool> done{ false };
    int hostExit = -1;
    const char* failure = "none";
    const char* stopTrigger = "none";
    std::uint64_t maximumBytes = 0;
    ULONGLONG startAt = 0, checkedAt = 0;
    const ULONGLONG began = GetTickCount64();
    Observation observations, diagnostics;
    observations.pauseMode = pauseMode;
    bool pausedSaveSent = false, resumeSent = false, finalPauseSent = false;
    ULONGLONG pausedAt = 0, resumedAt = 0;
    std::uint64_t frozenFrames = 0;
    bool pauseFramesUnchanged = false;
    try
    {
        Need(AppsClosed(), "apps_must_be_closed");
        output.Prepare(argv[2]); protocolLog.value = output.Create(L"protocol.jsonl");
        diagnosticLog.value = output.Create(L"native-diagnostics.jsonl"); report.value = output.Create(L"fixture.json");
        recorder::synthetic_timing::Initialize();
        Need(SetStdHandle(STD_INPUT_HANDLE, input.read.value) && SetStdHandle(STD_OUTPUT_HANDLE, nativeOutput.write.value) &&
            SetStdHandle(STD_ERROR_HANDLE, nativeError.write.value), "stdio_redirection_failed"); redirected = true;
        host = std::thread([&] { hostExit = recorder::host::RunStdioRecorder(); done.store(true); });
        const std::wstring spool = output.path + L"\\.wisp-recorder-" + std::wstring(Session, Session + 32);
        const bool lossless = std::wcscmp(argv[4], L"lossless") == 0;
        Command(input.write.value, Common(1, "config") + ",\"durationSeconds\":30,\"height\":" + std::to_string(OutputHeight) + ",\"frameRate\":60,\"quality\":100,\"gameAudio\":true,\"systemAudio\":false,\"losslessVideo\":" +
            (lossless ? "true" : "false") + ",\"spoolDirectory\":" + JsonPath(spool) + '}');
        Command(input.write.value, Common(2, "start") + ",\"processId\":" + std::to_string(GetCurrentProcessId()) +
            ",\"window\":\"1\",\"creationFileTime\":\"" + std::to_string(Creation()) + "\"}");
        while (!done.load())
        {
            observations.Read(nativeOutput, protocolLog.value, true); diagnostics.Read(nativeError, diagnosticLog.value, false);
            const auto now = GetTickCount64();
            if (observations.start && !startAt) startAt = now;
            if (now - checkedAt >= 250)
            {
                Need(AppsClosed(), "apps_started_during_fixture"); checkedAt = now;
                maximumBytes = (std::max)(maximumBytes, Size(output.path));
                // Conservative fixture ceiling leaves 10 GiB for the sampling
                // interval and any in-flight native writes before termination.
                if (maximumBytes >= 6 * GiB) Kill(126);
            }
            if (pauseMode && startAt && !stopSent)
            {
                const auto save = [&](int request, const char* clip)
                {
                    Command(input.write.value, Common(request, "save") + ",\"clipId\":\"" + clip + "\",\"destination\":" +
                        JsonPath(output.path + L"\\" + std::wstring(clip, clip + 32) + L".mp4") + '}');
                };
                if (!saveSent && observations.bufferingAudio && now - startAt >= 4000)
                {
                    save(3, Clip);
                    Command(input.write.value, Common(4, "pause") + '}');
                    saveSent = true;
                }
                if (observations.pauseAcknowledgments && observations.pausedReady && !pausedAt)
                { pausedAt = now; frozenFrames = recorder::capture::SyntheticFrameCount(); }
                if (pausedAt && !resumeSent)
                {
                    Need(recorder::capture::SyntheticFrameCount() == frozenFrames, "capture_advanced_while_paused");
                    if (!pausedSaveSent && observations.pauseSaves[0] && now - pausedAt >= 1000)
                    { save(5, "33333333333343338333333333333333"); pausedSaveSent = true; }
                    if (observations.pauseSaves[1] && now - pausedAt >= 2000)
                    {
                        pauseFramesUnchanged = true;
                        Command(input.write.value, Common(6, "resume") + ",\"processId\":" + std::to_string(GetCurrentProcessId()) +
                            ",\"window\":\"1\",\"creationFileTime\":\"" + std::to_string(Creation()) + "\"}");
                        resumeSent = true;
                    }
                }
                if (observations.resumedBuffering && !resumedAt) resumedAt = now;
                if (resumedAt && !finalPauseSent && now - resumedAt >= 3000)
                {
                    Command(input.write.value, Common(7, "pause") + '}');
                    save(8, "44444444444444448444444444444444"); finalPauseSent = true;
                }
            }
            if (!pauseMode && !highOutput && startAt && now - startAt >= 35000 && !saveSent && !stopSent)
            {
                Command(input.write.value, Common(3, "save") + ",\"clipId\":\"" + Clip + "\",\"destination\":" +
                    JsonPath(output.path + L"\\" + std::wstring(Clip, Clip + 32) + L".mp4") + '}');
                saveSent = true;
            }
            const bool plannedEnd = highOutput && startAt && now - startAt >= 8000;
            if (!stopSent && (plannedEnd || observations.saveResult || (pauseMode && observations.pauseSaves[2]) ||
                maximumBytes >= 4 * GiB || now - began >= (pauseMode ? 25000ull : 45000ull)))
            {
                stopTrigger = maximumBytes >= 4 * GiB ? "output_soft_limit" : now - began >= (pauseMode ? 25000ull : 45000ull) ? "process_deadline" :
                    plannedEnd ? "planned_eight_seconds" : pauseMode ? "pause_resume_saves_complete" : "save_result";
                Command(input.write.value, Common(pauseMode ? 9 : 4, "stop") + '}'); stopSent = true;
            }
            Sleep(5);
        }
        host.join();
        observations.Read(nativeOutput, protocolLog.value, true); diagnostics.Read(nativeError, diagnosticLog.value, false);
        maximumBytes = (std::max)(maximumBytes, Size(output.path));
    }
    catch (const Failure& error) { failure = error.reason; }
    catch (...) { failure = "fixture_unexpected_failure"; }
    if (host.joinable())
    {
        input.write.Close();
        if (WaitForSingleObject(host.native_handle(), 5000) != WAIT_OBJECT_0) Kill(127);
        host.join();
    }
    if (redirected)
    { (void)SetStdHandle(STD_INPUT_HANDLE, oldInput); (void)SetStdHandle(STD_OUTPUT_HANDLE, oldOutput); (void)SetStdHandle(STD_ERROR_HANDLE, oldError); }
    const bool pauseSatisfied = pauseFramesUnchanged && observations.pauseAcknowledgments == 2 && observations.resumeAccepted &&
        observations.resumedBuffering && observations.pauseSaves[0] && observations.pauseSaves[1] && observations.pauseSaves[2] &&
        observations.pauseStarts[2] == observations.pauseStarts[0] && observations.pauseEnds[1] >= observations.pauseEnds[0] &&
        observations.pauseEnds[2] >= observations.pauseEnds[1] + 20000000 &&
        observations.pauseEnds[2] <= observations.pauseEnds[1] + 40000000 &&
        std::strcmp(stopTrigger, "pause_resume_saves_complete") == 0;
    const bool runSatisfied = pauseMode ? pauseSatisfied : highOutput ? !saveSent && observations.bufferingAudio && observations.bufferSamples > 0 &&
        observations.payloadDuration100ns >= 6ull * 10000000 && std::strcmp(stopTrigger, "planned_eight_seconds") == 0 :
        saveSent && observations.saved && observations.audio;
    const bool completed = std::strcmp(failure, "none") == 0 && hostExit == 0 && runSatisfied && observations.stopped;
    if (report.value)
    {
        try
        {
            if (recorder::synthetic_timing::Enabled())
            {
                Handle timings; timings.value = output.Create(L"timing.json");
                Write(timings.value, recorder::synthetic_timing::Report());
            }
            const std::string result = "{\"mode\":\"synthetic_host\",\"completed\":" + std::string(completed ? "true" : "false") +
                ",\"variant\":\"" + (pauseMode ? "pause_save_resume_same_epoch" : detailCellSize == 8 ? "varying_8x8_eight_seconds_no_save" :
                    highOutput ? "high_output_eight_seconds_no_save" : "baseline_save_at_thirty_five_seconds") +
                "\",\"stopTrigger\":\"" + stopTrigger + "\",\"selectedDurationSeconds\":30" +
                ",\"frameVaryingDetailCellSide\":" + std::to_string(detailCellSize) +
                ",\"generatedHdrInput\":" + (HdrInput ? "true" : "false") + ",\"outputHeight\":" + std::to_string(OutputHeight) +
                ",\"failure\":\"" + failure + "\",\"hostExit\":" + std::to_string(hostExit) + ",\"elapsedMs\":" + std::to_string(GetTickCount64() - began) +
                ",\"maximumObservedOutputBytes\":" + std::to_string(maximumBytes) + ",\"saveRequested\":" + (saveSent ? "true" : "false") +
                ",\"outputAccounting\":\"held_file_get_file_size_ex\"" +
                ",\"saved\":" + (observations.saved ? "true" : "false") + ",\"savedAudio\":" + (observations.audio ? "true" : "false") +
                ",\"stopAcknowledged\":" + (observations.stopped ? "true" : "false") +
                ",\"lastBufferPayloadBytes\":" + std::to_string(observations.payloadBytes) +
                ",\"lastBufferDuration100ns\":" + std::to_string(observations.payloadDuration100ns) +
                ",\"sampledPayloadBytesPerSecond\":" + std::to_string(observations.payloadDuration100ns ? observations.payloadBytes * 10000000 / observations.payloadDuration100ns : 0) +
                ",\"bufferStatusSamples\":" + std::to_string(observations.bufferSamples) +
                ",\"pauseFramesUnchanged\":" + (pauseFramesUnchanged ? "true" : "false") +
                ",\"pauseBeforeFirstSaveCompleted\":" + (observations.pauseBeforeFirstSave ? "true" : "false") +
                ",\"pauseResumeHistoryVerified\":" + (pauseSatisfied ? "true" : "false") +
                ",\"timingInstrumented\":" + (recorder::synthetic_timing::Enabled() ? "true" : "false") +
                ",\"desktopCapture\":false,\"windowsCreated\":false,\"selfProcessAudioOnly\":true,\"managedRecoveryTested\":false}\n";
            Write(report.value, result);
        }
        catch (...) { failure = "report_write_failed"; }
    }
    (void)SetEvent(deadlineStop.value); deadline.join();
    std::puts(completed && std::strcmp(failure, "none") == 0 ? "Synthetic host completed; inspect saved duration and diagnostics." : "Synthetic host failed; inspect bounded scalar reports.");
    return completed && std::strcmp(failure, "none") == 0 ? 0 : 3;
}
