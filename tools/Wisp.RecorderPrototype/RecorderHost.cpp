#include "RecorderHost.h"
#include "RecorderProtocol.h"
#include "GameScreenCapture.h"
#include "HardwareVideoSession.h"
#include "NvencLosslessVideoSession.h"
#include "HdrFrameConverter.h"
#include "AudioTimeline.h"
#include "AacEncoder.h"
#include "EncodedSpool.h"
#include "SpoolMp4Writer.h"

#include <codecapi.h>
#include <mfapi.h>
#include <mferror.h>
#include <roapi.h>
#include <wrl/client.h>
#include <intrin.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <cstdio>
#include <deque>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <thread>
#include <utility>
#include <vector>

namespace recorder::host
{
    using Microsoft::WRL::ComPtr;
    using protocol::Reason;

    bool BuildPolicy(const Settings& settings, Policy& result) noexcept
    {
        result = {};
        const auto height = settings.height;
        if ((height != 360 && height != 480 && height != 720 && height != 1080 && height != 1440 && height != 2160) ||
            (settings.frameRate != 30 && settings.frameRate != 60) || settings.quality < 10 || settings.quality > 100 ||
            settings.seconds < 30 || settings.seconds > 300 || settings.seconds % 30 != 0) return false;
        const std::uint64_t width = height == 480 ? 854 : height * 16 / 9;
        // Rational piecewise target: quality 10=.25, 75=1, 100=1.5.
        const std::uint64_t factorNumerator = settings.quality <= 75 ? 65 + 3ull * (settings.quality - 10) :
            50ull + settings.quality - 75;
        const std::uint64_t factorDenominator = settings.quality <= 75 ? 260 : 50;
        const std::uint64_t numerator = 16000000ull * width * height * settings.frameRate *
            factorNumerator;
        const std::uint64_t denominator = 1920ull * 1080 * 60 * factorDenominator;
        const auto bitrate = (std::clamp)((numerator + denominator / 2) / denominator, 100000ull, 120000000ull);
        const auto storage = (3ull * (bitrate + 192000) * settings.seconds + 7) / 8 + 8ull * 1024 * 1024;
        result = { static_cast<std::uint32_t>(width), height, settings.frameRate, static_cast<std::uint32_t>(bitrate),
            height == 480 ? 1280u : 1u, height == 480 ? 1281u : 1u,
            (std::min)((std::max)(64ull * 1024 * 1024, storage), 16ull * 1024 * 1024 * 1024) };
        return true;
    }

    bool BuildLosslessStoragePolicy(std::uint32_t rate, std::uint64_t freeBytes, LosslessStoragePolicy& result) noexcept
    {
        result = {};
        if (rate != 30 && rate != 60) return false;
        constexpr std::uint64_t gib = 1024ull * 1024 * 1024;
        constexpr std::uint64_t metadata = 64ull * 1024 * 1024;
        // Two-second IDR spacing plus one boundary packet and bounded audio.
        const auto gop = (2ull * rate + 1) * lossless::MaximumPacketBytes + 16ull * 1024 * 1024;
        const auto fixed = gib + 2 * gop + metadata;
        if (freeBytes <= fixed) return false;
        // Five payload budgets cover two rolling/pinned ranges and two MP4
        // copies, including worst-case Annex-B length-prefix expansion.
        const auto payload = (std::min)(12 * gib, (freeBytes - fixed) / 5);
        if (payload < gop) return false;
        result = { payload, 2 * (payload + gop) + metadata };
        return result.spoolBytes <= 64 * gib;
    }

    bool BuildLosslessSaveRequirement(const LosslessStoragePolicy& policy, std::uint64_t charged,
        std::uint64_t video, std::uint64_t audio, std::uint64_t& required) noexcept
    {
        required = 0;
        constexpr std::uint64_t gib = 1024ull * 1024 * 1024;
        if (!policy.videoBytes || policy.videoBytes > spool::MaximumLosslessVideoBytes ||
            !policy.spoolBytes || policy.spoolBytes > 64 * gib || charged > policy.spoolBytes ||
            !video || video > policy.videoBytes || audio > spool::MaximumRetainedAudioBytes) return false;
        // Three-byte Annex-B prefixes may become four-byte MP4 lengths. Keep
        // mux metadata allowance, both finalized/publication copies, and all
        // remaining spool growth while the snapshot pins its source files.
        const auto mp4 = video + (video + 3) / 4 + audio + 64ull * 1024 * 1024;
        if (mp4 > protocol::MaximumMediaBytes) return false;
        required = policy.spoolBytes - charged + 2 * mp4 + gib;
        return true;
    }

    bool QpcTo100ns(std::uint64_t counter, std::uint64_t frequency, std::uint64_t& time) noexcept
    {
        time = 0;
        constexpr auto maximum = static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)());
        if (!frequency || frequency > maximum || counter > maximum || counter / frequency > maximum / 10000000) return false;
        const auto whole = (counter / frequency) * 10000000;
        unsigned __int64 high = 0, remainder = 0;
        const auto low = _umul128(counter % frequency, 10000000, &high);
        const auto fraction = _udiv128(high, low, frequency, &remainder);
        if (whole > maximum - fraction) return false;
        time = whole + fraction;
        return true;
    }

    bool FrameTime(std::uint64_t epoch, std::uint32_t index, std::uint32_t rate,
        std::uint64_t& due, std::int64_t& pts) noexcept
    {
        due = 0; pts = 0;
        constexpr auto maximum = static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)());
        if (!epoch || epoch > maximum || (rate != 30 && rate != 60) ||
            index == (std::numeric_limits<std::uint32_t>::max)()) return false;
        const auto offset = static_cast<std::uint64_t>(index) * 10000000 / rate;
        if (epoch > maximum - offset) return false;
        due = epoch + offset; pts = static_cast<std::int64_t>(offset);
        return true;
    }
    bool SchedulingAllowed(std::uint64_t now, std::uint64_t due) noexcept
    { return now <= due || now - due <= MaximumLateness100ns; }
    bool FrameFresh(std::uint64_t now, std::uint64_t presentation, std::uint64_t received, std::uint64_t allowance) noexcept
    {
        constexpr auto maximum = static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)());
        if (!presentation || !received || now > maximum || presentation > maximum || received > maximum) return false;
        if (received > now ? received - now > allowance : now - received > MaximumSourceAge100ns) return false;
        return presentation >= now || now - presentation <= MaximumSourceAge100ns;
    }
    bool DestinationMatches(const std::wstring& spoolDirectory, const std::wstring& destination, const std::string& id) noexcept
    {
        try
        {
            auto normalizedSpool = spoolDirectory, normalizedDestination = destination;
            std::replace(normalizedSpool.begin(), normalizedSpool.end(), L'/', L'\\');
            std::replace(normalizedDestination.begin(), normalizedDestination.end(), L'/', L'\\');
            if (!spool::ValidateSessionPath(normalizedSpool)) return false;
            const std::wstring basename = std::wstring(id.begin(), id.end()) + L".mp4";
            if (!spool::ValidateExportName(basename)) return false;
            const auto sourceSlash = normalizedSpool.find_last_of(L'\\');
            const auto destinationSlash = normalizedDestination.find_last_of(L'\\');
            if (sourceSlash == std::wstring::npos || destinationSlash != sourceSlash ||
                normalizedDestination.substr(destinationSlash + 1) != basename) return false;
            return CompareStringOrdinal(normalizedSpool.data(), static_cast<int>(sourceSlash + 1),
                normalizedDestination.data(), static_cast<int>(destinationSlash + 1), TRUE) == CSTR_EQUAL;
        }
        catch (...) { return false; }
    }

    namespace
    {
        constexpr std::size_t DiagnosticCapacity = 4096;
        struct FailureDiagnostic
        {
            bool recorded = false;
            Reason reason = Reason::None;
            HRESULT hr = S_OK;
            const char* stage = "not_started";
            bool schedulerLagKnown = false, sourceAgeKnown = false, localFrameAgeKnown = false;
            std::int64_t schedulerLag100ns = 0, sourceAge100ns = 0, localFrameAge100ns = 0;
            std::uint64_t videoPackets = 0, audioPackets = 0;
            UINT submittedFrames = 0;
            void Record(Reason value, HRESULT error, const char* location) noexcept
            {
                if (recorded) return;
                recorded = true; reason = value; hr = error; stage = location;
            }
        };
        struct DiagnosticComponent
        {
            const char* reason = "not_started";
            HRESULT hr = S_OK;
        };
        struct DiagnosticSnapshot
        {
            FailureDiagnostic first;
            DiagnosticComponent capture, video, conversion, aac, spool, audio, mux;
            const char* timelineReason = "not_started";
            std::int64_t audioResidual100ns = 0;
            std::uint64_t audioMaxResidual100ns = 0, audioAllowance100ns = 0;
            bool audioSourceCompleted = false;
        };
        // All callers pass compiler-owned component reason/stage literals, never
        // OS error messages or input strings. Reject unexpected token syntax as
        // a further serialization boundary; do not print paths or raw payloads.
        const char* DiagnosticToken(const char* value) noexcept
        {
            if (!value) return "unknown";
            for (std::size_t i = 0; i < 96; ++i)
            {
                const char c = value[i];
                if (!c) return i ? value : "unknown";
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return "unknown";
            }
            return "unknown";
        }
        std::size_t SerializeDiagnostic(const DiagnosticSnapshot& value,
            std::array<char, DiagnosticCapacity>& output) noexcept
        {
            output.fill(0);
            const auto& first = value.first;
            if (!first.recorded) return 0;
            const auto hr = [](HRESULT code) { return static_cast<unsigned long>(static_cast<DWORD>(code)); };
            const int size = std::snprintf(output.data(), output.size(),
                "{\"mode\":\"recorder_failure\",\"v\":1,\"reason\":\"%s\",\"hr\":%lu,\"stage\":\"%s\","
                "\"videoPackets\":%llu,\"audioPackets\":%llu,\"submittedFrames\":%u,"
                "\"schedulerLagKnown\":%s,\"schedulerLag100ns\":%lld,\"sourceAgeKnown\":%s,\"sourceAge100ns\":%lld,"
                "\"localFrameAgeKnown\":%s,\"localFrameAge100ns\":%lld,"
                "\"capture\":{\"reason\":\"%s\",\"hr\":%lu},\"video\":{\"reason\":\"%s\",\"hr\":%lu},"
                "\"conversion\":{\"reason\":\"%s\",\"hr\":%lu},\"aac\":{\"reason\":\"%s\",\"hr\":%lu},"
                "\"spool\":{\"reason\":\"%s\",\"hr\":%lu},\"audioSourceCompleted\":%s,"
                "\"audio\":{\"reason\":\"%s\",\"hr\":%lu},\"mux\":{\"reason\":\"%s\",\"hr\":%lu},"
                "\"timelineReason\":\"%s\",\"audioResidual100ns\":%lld,\"audioMaxResidual100ns\":%llu,\"audioAllowance100ns\":%llu}\n",
                protocol::Name(first.reason), hr(first.hr), DiagnosticToken(first.stage),
                static_cast<unsigned long long>(first.videoPackets), static_cast<unsigned long long>(first.audioPackets), first.submittedFrames,
                first.schedulerLagKnown ? "true" : "false", static_cast<long long>(first.schedulerLag100ns),
                first.sourceAgeKnown ? "true" : "false", static_cast<long long>(first.sourceAge100ns),
                first.localFrameAgeKnown ? "true" : "false", static_cast<long long>(first.localFrameAge100ns),
                DiagnosticToken(value.capture.reason), hr(value.capture.hr), DiagnosticToken(value.video.reason), hr(value.video.hr),
                DiagnosticToken(value.conversion.reason), hr(value.conversion.hr), DiagnosticToken(value.aac.reason), hr(value.aac.hr),
                DiagnosticToken(value.spool.reason), hr(value.spool.hr), value.audioSourceCompleted ? "true" : "false",
                DiagnosticToken(value.audio.reason), hr(value.audio.hr), DiagnosticToken(value.mux.reason), hr(value.mux.hr),
                DiagnosticToken(value.timelineReason), static_cast<long long>(value.audioResidual100ns),
                static_cast<unsigned long long>(value.audioMaxResidual100ns), static_cast<unsigned long long>(value.audioAllowance100ns));
            if (size <= 0 || static_cast<std::size_t>(size) >= output.size()) { output.fill(0); return 0; }
            return static_cast<std::size_t>(size);
        }
        void WriteDiagnostic(const DiagnosticSnapshot& snapshot) noexcept
        {
            std::array<char, DiagnosticCapacity> output{};
            const auto size = SerializeDiagnostic(snapshot, output);
            const HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
            if (!size || !error || error == INVALID_HANDLE_VALUE) return;
            DWORD written = 0;
            // Payload is bounded; synchronous pipe IO itself can block. Callers
            // require the existing active-media or armed shutdown watchdog.
            // Never retry the write or change those watchdog limits.
            (void)WriteFile(error, output.data(), static_cast<DWORD>(size), &written, nullptr);
        }
        constexpr DWORD FatalExit = 30;
        constexpr ULONGLONG StartupLimitMs = 14000, ProgressLimitMs = 12000, StopLimitMs = 4500;
        constexpr ULONGLONG SaveCancelMs = 290000, SaveKillMs = 295000;
        struct Failure { Reason reason; HRESULT hr; };
        void Require(bool value, Reason reason, HRESULT hr = E_FAIL) { if (!value) throw Failure{ reason, hr }; }
        void Check(HRESULT hr, Reason reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        std::uint64_t AvailableSpoolSpace(const std::wstring& sessionPath)
        {
            const auto slash = sessionPath.find_last_of(L"\\/");
            Require(slash != std::wstring::npos, Reason::StorageFailed, E_INVALIDARG);
            const auto parent = sessionPath.substr(0, slash + 1);
            ULARGE_INTEGER available{};
            if (!GetDiskFreeSpaceExW(parent.c_str(), &available, nullptr, nullptr))
                throw Failure{ Reason::StorageFailed, HRESULT_FROM_WIN32(GetLastError()) };
            return available.QuadPart;
        }
        [[noreturn]] void TerminateSelf() noexcept
        {
            (void)TerminateProcess(GetCurrentProcess(), FatalExit);
            std::terminate();
        }
        struct Handle
        {
            HANDLE value = nullptr;
            Handle() = default;
            explicit Handle(HANDLE owned) : value(owned) {}
            ~Handle() { if (value && value != INVALID_HANDLE_VALUE) (void)CloseHandle(value); }
            Handle(const Handle&) = delete;
            Handle& operator=(const Handle&) = delete;
            bool Close() noexcept
            {
                if (!value || value == INVALID_HANDLE_VALUE) return true;
                if (!CloseHandle(value)) return false;
                value = nullptr; return true;
            }
        };
        struct Event : Handle
        {
            Event() : Handle(CreateEventW(nullptr, TRUE, FALSE, nullptr))
            { Require(value != nullptr, Reason::ProtocolError, HRESULT_FROM_WIN32(GetLastError())); }
            void Signal() const noexcept { if (!SetEvent(value)) TerminateSelf(); }
        };
        bool Join(std::thread& thread, DWORD timeout, bool cancelPipeIo = false) noexcept
        {
            if (!thread.joinable()) return true;
            const HANDLE handle = thread.native_handle();
            if (cancelPipeIo && !CancelSynchronousIo(handle) && GetLastError() != ERROR_NOT_FOUND) return false;
            if (WaitForSingleObject(handle, timeout) != WAIT_OBJECT_0) return false;
            try { thread.join(); return true; }
            catch (...) { return false; }
        }
        struct Shared
        {
            HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
            Event wake, outputReady, watchdogStop;
            std::mutex inputMutex, outputMutex;
            std::deque<protocol::Command> commands;
            std::deque<std::string> lines;
            std::atomic<bool> abort{ false }, eof{ false }, pipeStop{ false }, exportCancel{ false };
            std::atomic<bool> mediaActive{ false }, outputFailed{ false };
            std::atomic<ULONGLONG> progress{ 0 }, startup{ 0 }, stopping{ 0 }, saving{ 0 };
            std::thread reader, writer, watchdog;

            void Abort(bool endOfInput = false) noexcept
            {
                if (endOfInput) eof.store(true);
                abort.store(true); exportCancel.store(true); wake.Signal();
            }
            void Emit(std::string line)
            {
                Require(!line.empty() && line.size() <= protocol::MaximumLineBytes, Reason::ProtocolError);
                std::lock_guard<std::mutex> lock(outputMutex);
                Require(!outputFailed.load() && lines.size() < MaximumOutputLines, Reason::ProtocolError);
                line.push_back('\n'); lines.push_back(std::move(line)); outputReady.Signal();
            }
            bool Take(protocol::Command& command)
            {
                std::lock_guard<std::mutex> lock(inputMutex);
                if (commands.empty())
                {
                    Require(ResetEvent(wake.value) != FALSE, Reason::ProtocolError);
                    // Abort can be set without inputMutex. Re-signal after a
                    // concurrent reset so idle EOF never loses its wakeup.
                    if (abort.load()) wake.Signal();
                    return false;
                }
                command = std::move(commands.front()); commands.pop_front(); return true;
            }
            void Start()
            {
                Require(input && input != INVALID_HANDLE_VALUE && output && output != INVALID_HANDLE_VALUE &&
                    GetFileType(input) == FILE_TYPE_PIPE && GetFileType(output) == FILE_TYPE_PIPE, Reason::ProtocolError);
                progress.store(GetTickCount64());
                watchdog = std::thread([this]
                {
                    for (;;)
                    {
                        const DWORD wait = WaitForSingleObject(watchdogStop.value, 100);
                        if (wait == WAIT_OBJECT_0) return;
                        if (wait != WAIT_TIMEOUT) TerminateSelf();
                        const auto now = GetTickCount64();
                        const auto stop = stopping.load(), start = startup.load(), save = saving.load();
                        if (abort.load() || (save && now - save >= SaveCancelMs)) exportCancel.store(true);
                        if ((stop && now - stop >= StopLimitMs) || (start && now - start >= StartupLimitMs) ||
                            (save && now - save >= SaveKillMs) ||
                            (mediaActive.load() && now - progress.load() >= ProgressLimitMs)) TerminateSelf();
                    }
                });
                writer = std::thread([this]
                {
                    try
                    {
                        for (;;)
                        {
                            std::string line;
                            {
                                std::lock_guard<std::mutex> lock(outputMutex);
                                if (!lines.empty()) { line = std::move(lines.front()); lines.pop_front(); }
                                else
                                {
                                    if (pipeStop.load()) return;
                                    (void)ResetEvent(outputReady.value);
                                }
                            }
                            if (line.empty()) { if (WaitForSingleObject(outputReady.value, 100) == WAIT_FAILED) throw Failure{ Reason::ProtocolError, E_FAIL }; continue; }
                            std::size_t offset = 0;
                            while (offset < line.size())
                            {
                                DWORD written = 0;
                                if (!WriteFile(output, line.data() + offset, static_cast<DWORD>(line.size() - offset), &written, nullptr) || !written)
                                    throw Failure{ Reason::ProtocolError, E_FAIL };
                                offset += written;
                            }
                        }
                    }
                    catch (...) { outputFailed.store(true); Abort(); }
                });
                reader = std::thread([this]
                {
                    try
                    {
                        std::array<char, 1024> bytes{};
                        std::string line; line.reserve(protocol::MaximumLineBytes);
                        for (;;)
                        {
                            DWORD read = 0;
                            if (!ReadFile(input, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr))
                            {
                                const auto error = GetLastError();
                                if (pipeStop.load() && error == ERROR_OPERATION_ABORTED) return;
                                if (error == ERROR_BROKEN_PIPE && line.empty()) { Abort(true); return; }
                                throw Failure{ Reason::ProtocolError, E_FAIL };
                            }
                            if (!read)
                            {
                                if (!line.empty()) throw Failure{ Reason::ProtocolError, E_FAIL };
                                Abort(true); return;
                            }
                            for (DWORD index = 0; index < read; ++index)
                            {
                                if (bytes[index] == '\n')
                                {
                                    protocol::Command command;
                                    Require(protocol::ParseCommand(line, command), Reason::ProtocolError);
                                    line.clear();
                                    std::lock_guard<std::mutex> lock(inputMutex);
                                    Require(commands.size() < MaximumCommands, Reason::ProtocolError);
                                    commands.push_back(std::move(command)); wake.Signal();
                                }
                                else
                                {
                                    Require(line.size() < protocol::MaximumLineBytes, Reason::ProtocolError);
                                    line.push_back(bytes[index]);
                                }
                            }
                        }
                    }
                    catch (...) { if (!pipeStop.load()) Abort(); }
                });
            }
            void StopPipes() noexcept
            {
                pipeStop.store(true); outputReady.Signal();
                if (!Join(reader, 500, true)) TerminateSelf();
                // Give already-serialized final responses a bounded opportunity
                // to leave the pipe; never block shutdown on an absent parent.
                if (!Join(writer, 500))
                    if (!Join(writer, 500, true)) TerminateSelf();
                watchdogStop.Signal();
                if (!Join(watchdog, 500)) TerminateSelf();
            }
        };
        std::uint64_t FileTimeValue(FILETIME value) noexcept
        { return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime; }
        bool DeviceReset(HRESULT hr) noexcept
        { return hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET || hr == DXGI_ERROR_DEVICE_HUNG; }
        Reason VideoReason(HRESULT hr, Reason otherwise = Reason::EncoderFailed) noexcept
        { return DeviceReset(hr) ? Reason::EncoderReconnecting : otherwise; }
        bool AudioInvalidated(HRESULT hr) noexcept
        { return hr == AUDCLNT_E_DEVICE_INVALIDATED || hr == AUDCLNT_E_RESOURCES_INVALIDATED || hr == AUDCLNT_E_SERVICE_NOT_RUNNING; }
        bool AudioCleanupSucceeded(const audio::Evidence& value) noexcept
        {
            // Invalidated clients may reject Stop/ReleaseBuffer. Releasing both
            // interfaces, closing owned handles and joining the source are the
            // cleanup boundary; source success is a separate media result.
            return value.resourcesReleased && SUCCEEDED(value.handleCloseHr) && !value.lateActivationCallbackPossible &&
                (SUCCEEDED(value.stopHr) || AudioInvalidated(value.stopHr)) &&
                (SUCCEEDED(value.releaseBufferHr) || AudioInvalidated(value.releaseBufferHr));
        }
        Reason AudioReason(HRESULT hr, const char* reason) noexcept
        {
            if (AudioInvalidated(hr)) return Reason::AudioReconnecting;
            if (reason && (std::strcmp(reason, "target_exited") == 0 || std::strcmp(reason, "target_creation_changed") == 0))
                return Reason::TargetExited;
            return Reason::AudioCaptureFailed;
        }
        Reason CaptureReason(const char* reason, HRESULT hr) noexcept
        {
            if (DeviceReset(hr)) return Reason::CaptureReconnecting;
            if (hr == RO_E_CLOSED || hr == RPC_E_DISCONNECTED || hr == HRESULT_FROM_WIN32(RPC_S_SERVER_UNAVAILABLE))
                return Reason::CaptureReconnecting;
            if (!reason) return Reason::CaptureFailed;
            if (std::strcmp(reason, "target_process_exited") == 0) return Reason::TargetExited;
            if (std::strcmp(reason, "capture_item_closed") == 0 || std::strcmp(reason, "target_window_hidden") == 0) return Reason::WindowClosed;
            if (std::strcmp(reason, "target_window_minimized") == 0) return Reason::WindowMinimized;
            if (std::strcmp(reason, "target_focus_lost") == 0) return Reason::FocusLost;
            if (std::strcmp(reason, "target_not_fullscreen") == 0) return Reason::FullscreenRequired;
            if (std::strcmp(reason, "duplication_access_lost") == 0 ||
                std::strcmp(reason, "duplication_session_disconnected") == 0 ||
                std::strcmp(reason, "duplication_busy") == 0 ||
                std::strcmp(reason, "duplication_desktop_unavailable") == 0 ||
                std::strcmp(reason, "duplication_mode_changed") == 0 ||
                std::strcmp(reason, "display_rotation_changed") == 0 ||
                std::strcmp(reason, "capture_surface_changed") == 0) return Reason::CaptureReconnecting;
            if (std::strcmp(reason, "target_size_changed") == 0 || std::strcmp(reason, "capture_content_size_changed") == 0) return Reason::WindowResized;
            if (std::strcmp(reason, "target_process_identity_mismatch") == 0 || std::strcmp(reason, "target_window_identity_changed") == 0)
                return Reason::TargetChanged;
            if (std::strcmp(reason, "target_monitor_changed") == 0 || std::strcmp(reason, "display_configuration_changed") == 0 ||
                std::strcmp(reason, "display_color_changed") == 0 || std::strcmp(reason, "display_white_changed") == 0 ||
                std::strcmp(reason, "display_configuration_unstable") == 0) return Reason::CaptureReconnecting;
            if (std::strcmp(reason, "duplication_format_unsupported") == 0 ||
                std::strcmp(reason, "duplication_mode_unsupported") == 0 ||
                std::strcmp(reason, "display_rotation_unsupported") == 0 ||
                std::strcmp(reason, "display_color_unsupported") == 0 ||
                std::strcmp(reason, "display_color_information_unavailable") == 0 ||
                std::strcmp(reason, "display_white_target_ambiguous") == 0 ||
                std::strcmp(reason, "display_white_out_of_bounds") == 0) return Reason::UnsupportedFormat;
            if (std::strcmp(reason, "hardware_display_adapter_missing") == 0) return Reason::UnsupportedGpu;
            if (std::strcmp(reason, "windows_capture_unavailable") == 0 ||
                std::strcmp(reason, "required_capture_controls_unavailable") == 0) return Reason::UnsupportedOs;
            return Reason::CaptureFailed;
        }
        struct MediaPacket
        {
            LONGLONG time = 0, duration = 0;
            bool clean = false;
            std::vector<BYTE> bytes;
        };
        struct SaveWork
        {
            std::thread thread;
            std::shared_ptr<const spool::Snapshot> snapshot;
            spool::Bounds bounds{};
            exporting::FileExportEvidence result{};
            std::int64_t request = 0;
            std::string clipId;
            bool hasAudio = false, sizeLimited = false;
            std::atomic<bool> done{ false };
        };

        class MediaSession final : public encoder::PacketObserver, public aac::PacketObserver,
            public encoder::FrameWriter, public capture::FrameConsumer
        {
        public:
            MediaSession(Shared& shared, const protocol::Command& configuration, const Policy& policy) :
                shared_(shared), config_(configuration), policy_(policy) {}
            ~MediaSession() { (void)Close(false); }
            bool Start(const protocol::Command& command) noexcept;
            bool Tick() noexcept;
            bool BeginSave(const protocol::Command& command) noexcept;
            bool CollectSave(protocol::Result& result, bool wait);
            bool Close(bool clean) noexcept;
            bool CleanupSucceeded() const noexcept { return closed_ && cleanupSucceeded_; }
            bool Ready() const noexcept { return ready_; }
            bool Saving() const noexcept { return save_ != nullptr; }
            bool HasAudio() const noexcept { return audioEnabled_; }
            bool SourceStale() const noexcept { return sourceStale_; }
            const protocol::LosslessBuffer* LosslessBufferStatus() const noexcept
            { return losslessBuffer_ ? &*losslessBuffer_ : nullptr; }
            bool BufferStatusChanged() const noexcept { return losslessBufferDirty_; }
            void BufferStatusAnnounced() noexcept { losslessBufferDirty_ = false; }
            Reason Error() const noexcept { return reason_; }
            DWORD WaitMilliseconds() noexcept;
            void EmitFailureDiagnostic(Reason, HRESULT, const char*) noexcept;

            HRESULT OnConfiguration(IMFMediaType*, const encoder::EncodeConfig&) noexcept override { return S_OK; }
            HRESULT OnPacket(IMFMediaType*, IMFSample*) noexcept override;
            HRESULT OnConfiguration(const aac::CodecConfiguration& value, IMFMediaType*) noexcept override
            { audioConfiguration_ = value; return S_OK; }
            HRESULT OnPacket(const aac::PacketView&) noexcept override;
            HRESULT Fill(UINT, ID3D11Texture2D*) noexcept override;
            HRESULT Submit(ID3D11Texture2D*, ID3D11Texture2D*) noexcept override;
        private:
            bool Fail(Reason value, HRESULT hr = E_FAIL) noexcept
            { RememberFailure(value, hr); reason_ = value; failed_ = true; return false; }
            void RememberFailure(Reason, HRESULT) noexcept;
            std::uint64_t Now();
            void CheckTarget();
            void ProcessAudio(UINT maximumPackets);
            void InitializeSpool();
            void CheckReadiness();
            void UpdateLosslessBuffer();
            void SetLosslessBuffer(const spool::AvailablePlan*);
            bool StopAudio() noexcept;
            HRESULT VideoError() const noexcept
            {
                if (!config_.losslessVideo) return video_.Result().hr;
                const auto& result = losslessVideo_.Result();
                return FAILED(result.cleanupHr) ? result.cleanupHr : result.hr;
            }
            const char* VideoReasonText() const noexcept
            {
                if (!config_.losslessVideo) return video_.Result().reason;
                const auto& result = losslessVideo_.Result();
                return FAILED(result.cleanupHr) ? result.cleanupReason : result.reason;
            }
            bool PumpVideo() noexcept { return config_.losslessVideo ? losslessVideo_.Pump() : video_.Pump(0); }
            exporting::VideoFormat VideoFormat() const noexcept;
            Shared& shared_;
            protocol::Command config_;
            Policy policy_;
            LosslessStoragePolicy losslessStorage_{};
            std::optional<protocol::LosslessBuffer> losslessBuffer_;
            ULONGLONG nextLosslessBufferCheck_ = 0;
            bool losslessBufferDirty_ = false;
            capture::GameScreenCapture capture_;
            hdr::HdrFrameConverter converter_;
            hdr::Evidence conversionEvidence_{};
            encoder::HardwareVideoSession video_;
            lossless::NvencLosslessVideoSession losslessVideo_;
            aac::Encoder audioEncoder_;
            audio::AudioTimeline timeline_;
            audio::PacketQueue audioQueue_;
            audio::Evidence audioEvidence_{};
            Event audioStop_;
            Handle process_;
            std::thread audioThread_;
            std::atomic<bool> audioDone_{ false };
            std::unique_ptr<const audio::Packet> pendingPcm_;
            audio::FeedSlice pendingSlice_{};
            spool::EncodedSpool spool_;
            std::optional<MediaPacket> bootstrap_;
            std::vector<BYTE> videoHeader_;
            aac::CodecConfiguration audioConfiguration_{};
            std::unique_ptr<SaveWork> save_;
            std::uint64_t frequency_ = 0, qpcRounding_ = 0, epoch_ = 0, previousVersion_ = 0, duplicateFrames_ = 0;
            std::uint64_t videoPackets_ = 0, audioPackets_ = 0;
            LONGLONG videoEnd_ = 0, audioEnd_ = 0;
            UINT nextFrame_ = 0;
            ULONGLONG began_ = 0;
            Reason reason_ = Reason::None;
            bool audioEnabled_ = false, audioInitialized_ = false, spoolInitialized_ = false;
            bool ready_ = false, readinessDirty_ = false, failed_ = false, closed_ = false, mfStarted_ = false, runtimeStarted_ = false;
            bool preserveBuffer_ = false, cleanupSucceeded_ = false, sourceStale_ = false;
            const char* diagnosticStage_ = "not_started";
            FailureDiagnostic firstFailure_{};
            bool diagnosticWritten_ = false, schedulerLagKnown_ = false, sourceAgeKnown_ = false, localFrameAgeKnown_ = false;
            std::int64_t schedulerLag100ns_ = 0, sourceAge100ns_ = 0, localFrameAge100ns_ = 0;
        };

        void MediaSession::RememberFailure(Reason reason, HRESULT hr) noexcept
        {
            if (firstFailure_.recorded) return;
            firstFailure_.Record(reason, hr, diagnosticStage_);
            firstFailure_.videoPackets = videoPackets_; firstFailure_.audioPackets = audioPackets_;
            firstFailure_.submittedFrames = config_.losslessVideo ? losslessVideo_.Result().submitted : video_.Result().submitted;
            firstFailure_.schedulerLagKnown = schedulerLagKnown_; firstFailure_.schedulerLag100ns = schedulerLag100ns_;
            firstFailure_.sourceAgeKnown = sourceAgeKnown_; firstFailure_.sourceAge100ns = sourceAge100ns_;
            firstFailure_.localFrameAgeKnown = localFrameAgeKnown_; firstFailure_.localFrameAge100ns = localFrameAge100ns_;
        }
        void MediaSession::EmitFailureDiagnostic(Reason reason, HRESULT hr, const char* stage) noexcept
        {
            if (diagnosticWritten_ || !shared_.watchdog.joinable()) return;
            diagnosticWritten_ = true;
            if (!firstFailure_.recorded) { diagnosticStage_ = stage; RememberFailure(reason, hr); }
            DiagnosticSnapshot snapshot;
            snapshot.first = firstFailure_;
            const auto capture = capture_.Result();
            snapshot.capture = { capture.reason, capture.hr };
            snapshot.video = { VideoReasonText(), VideoError() };
            snapshot.conversion = { conversionEvidence_.reason, conversionEvidence_.hr };
            snapshot.aac = { audioEncoder_.Result().reason, audioEncoder_.Result().hr };
            snapshot.spool = { spool_.Result().reason, spool_.Result().hr };
            // The producer writes its final Evidence before publishing done.
            // Never read that non-atomic structure while capture still owns it.
            snapshot.audioSourceCompleted = audioDone_.load();
            snapshot.audio = snapshot.audioSourceCompleted ? DiagnosticComponent{ audioEvidence_.reason, audioEvidence_.hr } :
                DiagnosticComponent{ "not_completed", E_PENDING };
            const auto& timeline = timeline_.Result();
            snapshot.timelineReason = timeline.reason;
            snapshot.audioResidual100ns = timeline.lastResidual100ns;
            snapshot.audioMaxResidual100ns = timeline.maximumAbsoluteResidual100ns;
            snapshot.audioAllowance100ns = timeline.policyAllowance100ns;
            if (save_ && save_->done.load()) snapshot.mux = { save_->result.media.reason, save_->result.media.hr };
            WriteDiagnostic(snapshot);
        }
        std::uint64_t MediaSession::Now()
        {
            LARGE_INTEGER counter{};
            Require(QueryPerformanceCounter(&counter) && counter.QuadPart > 0, Reason::CaptureFailed);
            std::uint64_t result = 0;
            Require(QpcTo100ns(static_cast<std::uint64_t>(counter.QuadPart), frequency_, result), Reason::CaptureFailed);
            return result;
        }
        void MediaSession::CheckTarget()
        {
            if (!capture_.CheckTarget()) throw Failure{ CaptureReason(capture_.Result().reason, capture_.Result().hr), capture_.Result().hr };
        }
        exporting::VideoFormat MediaSession::VideoFormat() const noexcept
        {
            if (config_.losslessVideo)
                return { policy_.width, policy_.height, policy_.frameRate, 0,
                    MFVideoPrimaries_BT709, MFVideoTransFunc_709, MFVideoTransferMatrix_Identity, MFNominalRange_0_255,
                    eAVEncH264VProfile_444, policy_.aspectNumerator, policy_.aspectDenominator, 0,
                    exporting::VideoEncoding::H264LosslessGbr444 };
            return { policy_.width, policy_.height, policy_.frameRate, policy_.bitrate,
                MFVideoPrimaries_BT709, MFVideoTransFunc_709, MFVideoTransferMatrix_BT709, MFNominalRange_16_235,
                eAVEncH264VProfile_Base, policy_.aspectNumerator, policy_.aspectDenominator, MFVideoChromaSubsampling_MPEG2 };
        }
        bool MediaSession::Start(const protocol::Command& command) noexcept
        {
            try
            {
                began_ = GetTickCount64();
                diagnosticStage_ = "storage_preflight";
                Check(spool::PreflightSessionParent(config_.spoolDirectory), Reason::StorageFailed);
                if (config_.losslessVideo)
                {
                    Require(BuildLosslessStoragePolicy(policy_.frameRate, AvailableSpoolSpace(config_.spoolDirectory), losslessStorage_),
                        Reason::LosslessStorageLow, HRESULT_FROM_WIN32(ERROR_DISK_FULL));
                    policy_.spoolBytes = losslessStorage_.spoolBytes;
                }
                diagnosticStage_ = "runtime_initialize";
                Check(RoInitialize(RO_INIT_MULTITHREADED), Reason::CaptureFailed); runtimeStarted_ = true;
                Check(MFStartup(MF_VERSION, MFSTARTUP_FULL), Reason::EncoderFailed); mfStarted_ = true;
                LARGE_INTEGER frequency{};
                Require(QueryPerformanceFrequency(&frequency) && frequency.QuadPart >= audio::SampleRate, Reason::CaptureFailed);
                frequency_ = static_cast<std::uint64_t>(frequency.QuadPart);
                qpcRounding_ = 10000000 / frequency_ + (10000000 % frequency_ != 0 ? 1 : 0);
                process_.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, command.processId);
                Require(process_.value != nullptr && GetProcessId(process_.value) == command.processId, Reason::TargetChanged);
                FILETIME creation{}, exit{}, kernel{}, user{};
                Require(GetProcessTimes(process_.value, &creation, &exit, &kernel, &user) &&
                    FileTimeValue(creation) == command.creationFileTime &&
                    WaitForSingleObject(process_.value, 0) == WAIT_TIMEOUT, Reason::TargetChanged);
                const capture::TargetIdentity target{ reinterpret_cast<HWND>(static_cast<UINT_PTR>(command.window)),
                    command.processId, command.creationFileTime };
                diagnosticStage_ = "capture_initialize";
                if (!capture_.Initialize(target, { policy_.frameRate, config_.borderlessAllowed }))
                    throw Failure{ CaptureReason(capture_.Result().reason, capture_.Result().hr), capture_.Result().hr };
                const auto& source = capture_.Source();
                conversion::OutputConfiguration output{ policy_.width, policy_.height, policy_.frameRate,
                    policy_.aspectNumerator, policy_.aspectDenominator };
                diagnosticStage_ = "conversion_initialize";
                const auto sourceEncoding = source.encoding == capture::SourceEncoding::LinearScRgbFp16
                    ? hdr::SourceEncoding::LinearScRgbFp16 : source.encoding == capture::SourceEncoding::SrgbBgra8
                    ? hdr::SourceEncoding::SrgbBgra8 : hdr::SourceEncoding::Unknown;
                if (!converter_.Initialize(capture_.Device(), source.width, source.height,
                    sourceEncoding, sourceEncoding == hdr::SourceEncoding::LinearScRgbFp16
                        ? source.referenceWhiteNits : 0.0f, output,
                    config_.losslessVideo ? hdr::OutputEncoding::PreparedRgbAyuv : hdr::OutputEncoding::Bt709Nv12, conversionEvidence_))
                    throw Failure{ Reason::UnsupportedFormat, conversionEvidence_.hr };
                encoder::EncodeConfig encode{ policy_.width, policy_.height, policy_.frameRate, policy_.bitrate,
                    policy_.aspectNumerator, policy_.aspectDenominator, MFVideoChromaSubsampling_MPEG2 };
                encoder::LiveOptions options; options.operationTimeoutMs = 3000;
                diagnosticStage_ = "video_initialize";
                if (config_.losslessVideo)
                {
                    encode.bitrate = 0; encode.chromaSiting = 0;
                    if (!losslessVideo_.Initialize(capture_.Device(), encode, { options.operationTimeoutMs, 0 }, shared_.abort, *this))
                        throw Failure{ VideoReason(VideoError(), Reason::UnsupportedGpu), VideoError() };
                }
                else if (!video_.Initialize(capture_.Device(), encode, options, shared_.abort, *this))
                    throw Failure{ VideoReason(VideoError(), Reason::UnsupportedGpu), VideoError() };
                if (config_.gameAudio)
                {
                    diagnosticStage_ = "audio_initialize";
                    audio::Target audioTarget{ process_.value, command.processId, command.creationFileTime };
                    audioThread_ = std::thread([this, audioTarget]
                    {
                        audio::Options settings;
                        settings.source = config_.systemAudio ? audio::LoopbackSource::SystemPlayback : audio::LoopbackSource::GameProcess;
                        settings.mode = audio::CaptureMode::UntilStopped; settings.maximumCaptureMs = 0;
                        audioEvidence_ = audio::RunProcessLoopback(audioTarget, settings, audioStop_.value, audioQueue_);
                        audioDone_.store(true); shared_.wake.Signal();
                    });
                    const auto waitBegan = GetTickCount64();
                    while (!audioQueue_.TryPop(pendingPcm_) && !audioDone_.load())
                    {
                        Require(!shared_.abort.load(), Reason::ParentClosed);
                        CheckTarget();
                        shared_.progress.store(GetTickCount64());
                        if (GetTickCount64() - waitBegan >= 3500) break;
                        Require(WaitForSingleObject(audioStop_.value, 10) == WAIT_TIMEOUT, Reason::Cancelled);
                    }
                    audioEnabled_ = pendingPcm_ != nullptr;
                    if (!audioEnabled_)
                    {
                        Require(StopAudio(), Reason::CleanupFailed);
                        if (FAILED(audioEvidence_.hr))
                            throw Failure{ AudioReason(audioEvidence_.hr, audioEvidence_.reason), audioEvidence_.hr };
                    }
                }
                Require(!shared_.abort.load(), Reason::ParentClosed);
                diagnosticStage_ = "capture_start";
                if (!capture_.Start()) throw Failure{ CaptureReason(capture_.Result().reason, capture_.Result().hr), capture_.Result().hr };
                return true;
            }
            catch (const Failure& value) { return Fail(value.reason, value.hr); }
            catch (...) { return Fail(Reason::CaptureFailed); }
        }

        HRESULT MediaSession::Submit(ID3D11Texture2D* source, ID3D11Texture2D* destination) noexcept
        { return converter_.Submit(source, destination, conversionEvidence_) ? S_OK : E_FAIL; }

        HRESULT MediaSession::Fill(UINT, ID3D11Texture2D* destination) noexcept
        {
            try
            {
                diagnosticStage_ = "capture_submit";
                capture::FrameInfo frame;
                const HRESULT hr = capture_.SubmitLatestLocked(destination, *this, frame);
                Require(hr == S_OK, CaptureReason(capture_.Result().reason, hr), FAILED(hr) ? hr : E_PENDING);
                const auto now = Now();
                diagnosticStage_ = "capture_freshness";
                if (frame.rawTimestamp100ns > 0)
                {
                    sourceAgeKnown_ = true;
                    sourceAge100ns_ = static_cast<std::int64_t>(now) - frame.rawTimestamp100ns;
                }
                std::uint64_t received100ns = 0;
                Require(frame.receivedQpc != 0 && QpcTo100ns(frame.receivedQpc, frequency_, received100ns), Reason::CaptureFailed);
                localFrameAgeKnown_ = true;
                localFrameAge100ns_ = static_cast<std::int64_t>(now) - static_cast<std::int64_t>(received100ns);
                // A valid visible target may legitimately stop producing frames
                // in a loading screen. Preserve CFR/history by repeating this
                // texture, with an explicit stale state instead of a reset.
                // Raw metadata ensures clamping cannot hide old source content.
                Require(frame.rawTimestamp100ns > 0, Reason::CaptureFailed);
                sourceStale_ = !FrameFresh(now, static_cast<std::uint64_t>(frame.rawTimestamp100ns), received100ns, qpcRounding_);
                if (!epoch_)
                {
                    // The first desktop image may predate capture. Start this
                    // recording at submission; preserve raw image age above.
                    epoch_ = now;
                    Require(timeline_.Initialize({ epoch_, frequency_ }), Reason::AudioFailed);
                }
                if (frame.version == previousVersion_)
                {
                    Require(duplicateFrames_ != (std::numeric_limits<std::uint64_t>::max)(), Reason::CaptureFailed);
                    ++duplicateFrames_;
                }
                previousVersion_ = frame.version;
                return S_OK;
            }
            catch (const Failure& value) { (void)Fail(value.reason, value.hr); return value.hr; }
            catch (...) { (void)Fail(Reason::CaptureFailed); return E_FAIL; }
        }

        HRESULT MediaSession::OnPacket(IMFMediaType* type, IMFSample* sample) noexcept
        {
            try
            {
                diagnosticStage_ = "video_output";
                UINT32 headerSize = 0;
                Check(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &headerSize), Reason::EncoderFailed);
                Require(headerSize != 0 && headerSize <= 65536, Reason::EncoderFailed);
                std::vector<BYTE> header(headerSize);
                Check(type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, header.data(), headerSize, nullptr), Reason::EncoderFailed);
                if (videoHeader_.empty()) videoHeader_ = std::move(header);
                else Require(videoHeader_ == header, Reason::EncoderFailed);
                MediaPacket packet;
                Check(sample->GetSampleTime(&packet.time), Reason::EncoderFailed);
                Check(sample->GetSampleDuration(&packet.duration), Reason::EncoderFailed);
                UINT32 clean = 0;
                const HRESULT cleanHr = sample->GetUINT32(MFSampleExtension_CleanPoint, &clean);
                if (cleanHr != MF_E_ATTRIBUTENOTFOUND) Check(cleanHr, Reason::EncoderFailed);
                packet.clean = clean != 0;
                Require(packet.time == videoEnd_ && packet.duration > 0 &&
                    packet.time <= (std::numeric_limits<LONGLONG>::max)() - packet.duration, Reason::EncoderFailed);
                ComPtr<IMFMediaBuffer> buffer;
                Check(sample->ConvertToContiguousBuffer(&buffer), Reason::EncoderFailed);
                DWORD length = 0;
                Check(buffer->GetCurrentLength(&length), Reason::EncoderFailed);
                Require(length && length <= (config_.losslessVideo ? lossless::MaximumPacketBytes : spool::StandardMaximumPacketBytes),
                    Reason::EncoderFailed);
                BYTE* data = nullptr;
                Check(buffer->Lock(&data, nullptr, nullptr), Reason::EncoderFailed);
                HRESULT operation = S_OK;
                try
                {
                    Require(data != nullptr, Reason::EncoderFailed);
                    if (!spoolInitialized_)
                    {
                        Require(!bootstrap_ && packet.clean && videoPackets_ == 0, Reason::EncoderFailed);
                        packet.bytes.assign(data, data + length); bootstrap_ = std::move(packet);
                    }
                    else
                    {
                        diagnosticStage_ = "video_spool_append";
                        Require(spool_.AppendVideo(packet.time, packet.duration, packet.clean, data, length), Reason::StorageFailed);
                    }
                }
                catch (const Failure& value) { RememberFailure(value.reason, value.hr); operation = E_FAIL; }
                catch (...) { operation = E_FAIL; }
                const HRESULT unlocked = buffer->Unlock();
                Check(unlocked, Reason::EncoderFailed); Check(operation, Reason::StorageFailed);
                Require(videoPackets_ != (std::numeric_limits<std::uint64_t>::max)(), Reason::EncoderFailed);
                ++videoPackets_;
                const auto& accepted = bootstrap_ ? *bootstrap_ : packet;
                videoEnd_ = accepted.time + accepted.duration;
                readinessDirty_ = true;
                // Configuration is normally ready before output arrives. Commit
                // the single bootstrap now, so a subsequent output event never
                // needs another retained encoded packet.
                InitializeSpool();
                return S_OK;
            }
            catch (const Failure& value) { (void)Fail(value.reason, value.hr); return value.hr; }
            catch (...) { (void)Fail(Reason::EncoderFailed); return E_FAIL; }
        }
        HRESULT MediaSession::OnPacket(const aac::PacketView& packet) noexcept
        {
            diagnosticStage_ = "audio_output";
            if (!spoolInitialized_ || !audioEnabled_ || !packet.data || !packet.bytes ||
                packet.time100ns < 0 || packet.duration100ns <= 0 ||
                packet.time100ns > (std::numeric_limits<LONGLONG>::max)() - packet.duration100ns ||
                audioPackets_ == (std::numeric_limits<std::uint64_t>::max)())
            { (void)Fail(Reason::AudioFailed); return E_FAIL; }
            diagnosticStage_ = "audio_spool_append";
            if (!spool_.AppendAudio(packet.time100ns, packet.duration100ns, packet.data, packet.bytes))
            { (void)Fail(Reason::StorageFailed, spool_.Result().hr); return E_FAIL; }
            ++audioPackets_; audioEnd_ = packet.time100ns + packet.duration100ns; readinessDirty_ = true;
            return S_OK;
        }

        void MediaSession::ProcessAudio(UINT maximumPackets)
        {
            if (!audioEnabled_ || !epoch_) return;
            diagnosticStage_ = "audio_source";
            if (audioDone_.load()) throw Failure{ AudioReason(audioEvidence_.hr, audioEvidence_.reason), audioEvidence_.hr };
            for (UINT count = 0; count < maximumPackets; ++count)
            {
                if (pendingSlice_.frames)
                {
                    if (!spoolInitialized_) return;
                    diagnosticStage_ = "audio_encode";
                    if (!audioEncoder_.Feed(pendingSlice_.samples, pendingSlice_.frames, pendingSlice_.firstFrameIndex))
                        throw Failure{ Reason::AudioFailed, audioEncoder_.Result().hr };
                    pendingSlice_ = {}; pendingPcm_.reset();
                }
                if (!pendingPcm_ && !audioQueue_.TryPop(pendingPcm_)) return;
                audio::FeedSlice slice;
                diagnosticStage_ = "audio_timeline";
                const auto status = timeline_.Inspect(*pendingPcm_, slice);
                if (status == audio::TimelineResult::Failed)
                {
                    const auto* reason = timeline_.Result().reason;
                    const bool discontinuity = std::strcmp(reason, "audio_timeline_source_discontinuity") == 0 ||
                        std::strcmp(reason, "audio_timeline_timestamp_unavailable") == 0 ||
                        std::strcmp(reason, "audio_timeline_timestamp_not_increasing") == 0 ||
                        std::strcmp(reason, "audio_timeline_clock_outside_policy") == 0;
                    throw Failure{ discontinuity ? Reason::AudioReconnecting : Reason::AudioFailed, E_FAIL };
                }
                if (status == audio::TimelineResult::BeforeVideoEpoch) { pendingPcm_.reset(); continue; }
                pendingSlice_ = slice;
                if (!audioInitialized_)
                {
                    aac::Configuration options;
                    options.mode = aac::SessionMode::UntilStopped; options.maximumSourceFrames = 0;
                    options.operationTimeoutMs = 3000; options.epochTime100ns = slice.audioEpochTime100ns;
                    diagnosticStage_ = "aac_initialize";
                    if (!audioEncoder_.Initialize(options, shared_.abort, this))
                        throw Failure{ Reason::AudioFailed, audioEncoder_.Result().hr };
                    audioInitialized_ = true;
                }
            }
        }
        void MediaSession::InitializeSpool()
        {
            if (spoolInitialized_ || !bootstrap_ || (audioEnabled_ && !audioInitialized_)) return;
            diagnosticStage_ = "spool_initialize";
            spool::Configuration configuration;
            configuration.epoch = epoch_; configuration.h264SequenceHeader = videoHeader_;
            if (audioEnabled_)
                configuration.aacUserData.assign(audioConfiguration_.userData.begin(),
                    audioConfiguration_.userData.begin() + audioConfiguration_.userDataBytes);
            spool::Limits limits;
            limits.maximumDuration100ns = static_cast<LONGLONG>(config_.durationSeconds) * 10000000;
            limits.maximumFileBytes = policy_.spoolBytes;
            limits.maximumPacketBytes = config_.losslessVideo ? lossless::MaximumPacketBytes : spool::StandardMaximumPacketBytes;
            if (!spool_.Initialize(config_.spoolDirectory, limits, configuration))
                throw Failure{ Reason::StorageFailed, spool_.Result().hr };
            spoolInitialized_ = true;
            const auto& first = *bootstrap_;
            diagnosticStage_ = "spool_bootstrap";
            if (!spool_.AppendVideo(first.time, first.duration, first.clean, first.bytes.data(), first.bytes.size()))
                throw Failure{ Reason::StorageFailed, spool_.Result().hr };
            bootstrap_.reset(); readinessDirty_ = true;
        }
        void MediaSession::CheckReadiness()
        {
            if (ready_ || !readinessDirty_ || !spoolInitialized_ || videoPackets_ == 0 ||
                (audioEnabled_ && (audioPackets_ == 0 || audioEnd_ <= 0))) return;
            readinessDirty_ = false;
            if (config_.losslessVideo)
            {
                spool::AvailablePlan plan;
                if (spool_.PlanAvailable(static_cast<LONGLONG>(config_.durationSeconds) * 10000000,
                    { losslessStorage_.videoBytes, spool::MaximumRetainedAudioBytes }, plan))
                {
                    ready_ = true; SetLosslessBuffer(&plan);
                    nextLosslessBufferCheck_ = GetTickCount64() + 1000;
                    shared_.startup.store(0);
                }
                return;
            }
            std::shared_ptr<const spool::Snapshot> probe;
            if (spool_.Retain(static_cast<LONGLONG>(config_.durationSeconds) * 10000000, probe))
            {
                ready_ = probe && probe->Range().videoPackets > 0 &&
                    (!audioEnabled_ || probe->Range().audioPackets > 0);
                probe.reset();
                if (ready_) shared_.startup.store(0);
            }
        }
        void MediaSession::SetLosslessBuffer(const spool::AvailablePlan* plan)
        {
            std::optional<protocol::LosslessBuffer> next;
            if (plan)
                next = protocol::LosslessBuffer{ plan->bounds.end100ns - plan->bounds.start100ns,
                    plan->videoPayloadBytes + plan->audioPayloadBytes,
                    losslessStorage_.videoBytes + spool::MaximumRetainedAudioBytes, plan->sizeLimited };
            const bool same = next.has_value() == losslessBuffer_.has_value() &&
                (!next || *next == *losslessBuffer_);
            if (!same) { losslessBuffer_ = next; losslessBufferDirty_ = true; }
        }
        void MediaSession::UpdateLosslessBuffer()
        {
            if (!config_.losslessVideo || !ready_ || !spoolInitialized_) return;
            const auto now = GetTickCount64();
            if (now < nextLosslessBufferCheck_) return;
            nextLosslessBufferCheck_ = now + 1000;
            spool::AvailablePlan plan;
            const bool available = spool_.PlanAvailable(static_cast<LONGLONG>(config_.durationSeconds) * 10000000,
                { losslessStorage_.videoBytes, spool::MaximumRetainedAudioBytes }, plan);
            SetLosslessBuffer(available ? &plan : nullptr);
        }
        bool MediaSession::Tick() noexcept
        {
            try
            {
                Require(!failed_ && !closed_ && !shared_.abort.load(), reason_ == Reason::None ? Reason::ParentClosed : reason_);
                diagnosticStage_ = "capture_target";
                CheckTarget();
                diagnosticStage_ = "video_pump";
                if (!PumpVideo()) throw Failure{ failed_ ? reason_ : VideoReason(VideoError()), VideoError() };
                ProcessAudio(8); InitializeSpool(); ProcessAudio(8);
                for (UINT submitted = 0; submitted < 2; ++submitted)
                {
                    // Do not depend on first H264 output to submit input2: a
                    // low-latency MFT may still need more than one input. The
                    // existing three tracked textures bound in-flight inputs.
                    if (nextFrame_ && audioEnabled_ && !audioInitialized_) break;
                    if (capture_.Result().copiedFrames == 0) break;
                    std::uint64_t due = 0; std::int64_t pts = 0;
                    diagnosticStage_ = "video_schedule";
                    const auto now = Now();
                    if (nextFrame_)
                    {
                        Require(FrameTime(epoch_, nextFrame_, policy_.frameRate, due, pts), Reason::EncoderFailed);
                        schedulerLagKnown_ = true;
                        schedulerLag100ns_ = static_cast<std::int64_t>(now) - static_cast<std::int64_t>(due);
                        if (now < due) break;
                        Require(SchedulingAllowed(now, due), Reason::SchedulerLate);
                    }
                    if (!(config_.losslessVideo ? losslessVideo_.CanAcceptInput() : video_.CanAcceptInput())) break;
                    diagnosticStage_ = "video_submit";
                    const auto status = config_.losslessVideo ? losslessVideo_.TrySubmit(nextFrame_, pts, *this) :
                        video_.TrySubmit(nextFrame_, pts, *this);
                    diagnosticStage_ = "video_submit";
                    Require(status != encoder::SubmitResult::Failed, failed_ ? reason_ : VideoReason(VideoError()), VideoError());
                    if (status == encoder::SubmitResult::WouldBlock) break;
                    Require(nextFrame_ != (std::numeric_limits<UINT>::max)(), Reason::EncoderFailed);
                    ++nextFrame_;
                    ProcessAudio(8); InitializeSpool();
                }
                diagnosticStage_ = "video_pump";
                if (!PumpVideo()) throw Failure{ failed_ ? reason_ : VideoReason(VideoError()), VideoError() };
                InitializeSpool(); ProcessAudio(8); CheckReadiness();
                diagnosticStage_ = "startup_readiness";
                Require(ready_ || GetTickCount64() - began_ < 12000, Reason::CaptureReconnecting);
                diagnosticStage_ = "spool_health";
                Require(!spool_.Result().poisoned, Reason::StorageFailed);
                UpdateLosslessBuffer();
                shared_.progress.store(GetTickCount64());
                return true;
            }
            catch (const Failure& value) { return Fail(value.reason, value.hr); }
            catch (...) { return Fail(Reason::EncoderFailed); }
        }

        DWORD MediaSession::WaitMilliseconds() noexcept
        {
            try
            {
                if (!epoch_ || !spoolInitialized_) return 5;
                std::uint64_t due = 0; std::int64_t pts = 0;
                if (!FrameTime(epoch_, nextFrame_, policy_.frameRate, due, pts)) return 1;
                const auto now = Now();
                if (due <= now) return 1;
                return static_cast<DWORD>((std::min)(10ull, (std::max)(1ull, (due - now) / 10000)));
            }
            catch (...) { return 1; }
        }

        bool MediaSession::BeginSave(const protocol::Command& command) noexcept
        {
            if (save_ || !ready_ || failed_ || closed_) return false;
            HANDLE file = nullptr;
            bool retainedAttempt = false;
            try
            {
                diagnosticStage_ = "save_destination";
                Require(DestinationMatches(config_.spoolDirectory, command.destination, command.clipId), Reason::ProtocolError);
                auto work = std::make_unique<SaveWork>();
                diagnosticStage_ = "save_retain";
                if (config_.losslessVideo)
                {
                    spool::AvailablePlan plan;
                    const bool retained = spool_.RetainAvailable(static_cast<LONGLONG>(config_.durationSeconds) * 10000000,
                        { losslessStorage_.videoBytes, spool::MaximumRetainedAudioBytes }, work->snapshot, plan);
                    Require(retained, Reason::NoKeyframe, spool_.Result().hr);
                    retainedAttempt = true; work->sizeLimited = plan.sizeLimited;
                    diagnosticStage_ = "save_storage_preflight";
                    std::uint64_t required = 0;
                    Require(BuildLosslessSaveRequirement(losslessStorage_, spool_.Result().accountedFileBytes,
                        plan.videoPayloadBytes, plan.audioPayloadBytes, required), Reason::LosslessStorageLow);
                    Require(AvailableSpoolSpace(config_.spoolDirectory) >= required,
                        Reason::LosslessStorageLow, HRESULT_FROM_WIN32(ERROR_DISK_FULL));
                }
                else Require(spool_.Retain(static_cast<LONGLONG>(config_.durationSeconds) * 10000000, work->snapshot), Reason::NoKeyframe);
                retainedAttempt = true;
                const std::wstring filename = std::wstring(command.clipId.begin(), command.clipId.end()) + L".mp4";
                diagnosticStage_ = "save_create";
                Check(spool_.CreateNewExport(filename, file), Reason::StorageFailed);
                work->bounds = work->snapshot->Range(); work->request = command.request;
                work->clipId = command.clipId; work->hasAudio = audioEnabled_;
                const auto format = VideoFormat();
                shared_.exportCancel.store(false); shared_.saving.store(GetTickCount64());
                auto* destination = work.get();
                diagnosticStage_ = "save_worker";
                work->thread = std::thread([this, destination, file, format]
                {
                    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                    HRESULT mf = E_FAIL;
                    if (SUCCEEDED(com)) mf = MFStartup(MF_VERSION, MFSTARTUP_LITE);
                    if (SUCCEEDED(com) && SUCCEEDED(mf))
                        destination->result = exporting::WriteSpoolMp4(file, *destination->snapshot, format, shared_.exportCancel);
                    else
                    {
                        destination->result.media.hr = FAILED(com) ? com : mf;
                        destination->result.media.reason = "save_worker_initialization_failed";
                        if (!CloseHandle(file)) destination->result.fileCloseHr = HRESULT_FROM_WIN32(GetLastError());
                    }
                    destination->snapshot.reset();
                    if (SUCCEEDED(mf))
                    {
                        const HRESULT shutdown = MFShutdown();
                        if (FAILED(shutdown))
                        {
                            destination->result.media.completed = false;
                            if (SUCCEEDED(destination->result.media.cleanupHr)) destination->result.media.cleanupHr = shutdown;
                        }
                    }
                    if (SUCCEEDED(com)) CoUninitialize();
                    destination->done.store(true); shared_.wake.Signal();
                });
                file = nullptr; save_ = std::move(work);
                return true;
            }
            catch (const Failure& value)
            {
                RememberFailure(value.reason, value.hr); reason_ = value.reason;
                if (value.reason == Reason::LosslessStorageLow ||
                    (config_.losslessVideo && value.reason == Reason::StorageFailed)) failed_ = true;
            }
            catch (...) { RememberFailure(Reason::StorageFailed, E_FAIL); reason_ = Reason::StorageFailed; }
            EmitFailureDiagnostic(reason_, E_FAIL, "save_begin");
            preserveBuffer_ |= retainedAttempt;
            shared_.saving.store(0);
            if (file && !CloseHandle(file)) (void)Fail(Reason::StorageFailed);
            return false;
        }

        bool MediaSession::CollectSave(protocol::Result& result, bool wait)
        {
            if (!save_ || (!wait && !save_->done.load())) return false;
            if (wait && !Join(save_->thread, 3000)) TerminateSelf();
            if (!wait && !Join(save_->thread, 100)) TerminateSelf();
            const auto& completed = save_->result;
            if (!completed.media.completed)
                EmitFailureDiagnostic(shared_.exportCancel.load() ? Reason::Cancelled : Reason::MuxFailed,
                    completed.media.hr, "save_finalize");
            preserveBuffer_ |= !completed.media.completed;
            result = { config_.session, save_->request, completed.media.completed,
                completed.media.completed ? (save_->hasAudio ? Reason::None : Reason::AudioUnavailable) :
                    (shared_.exportCancel.load() ? Reason::Cancelled : Reason::MuxFailed), std::nullopt };
            if (completed.media.completed)
                result.media = protocol::SavedMedia{ save_->clipId, completed.fileBytes, policy_.width, policy_.height,
                    policy_.frameRate, save_->bounds.start100ns, save_->bounds.end100ns, save_->hasAudio,
                    config_.losslessVideo, save_->sizeLimited };
            save_.reset(); shared_.saving.store(0);
            return true;
        }
        bool MediaSession::StopAudio() noexcept
        {
            audioStop_.Signal();
            if (!Join(audioThread_, 1500)) return false;
            return !audioDone_.load() || AudioCleanupSucceeded(audioEvidence_);
        }
        bool MediaSession::Close(bool clean) noexcept
        {
            if (closed_) return !failed_ && cleanupSucceeded_;
            bool okay = !failed_;
            const bool stoppedAudio = StopAudio();
            if (!stoppedAudio && audioThread_.joinable()) TerminateSelf();
            bool cleanupOkay = stoppedAudio;
            okay = stoppedAudio && okay;
            if (clean && audioEnabled_ && audioDone_.load())
                okay = audioEvidence_.completed && SUCCEEDED(audioEvidence_.hr) && okay;
            if (save_)
            {
                shared_.exportCancel.store(true);
                if (!Join(save_->thread, 3000)) TerminateSelf();
                save_.reset(); shared_.saving.store(0);
                preserveBuffer_ = true;
                okay = false; // An interrupted export always keeps recovery data.
            }
            cleanupOkay = SUCCEEDED(capture_.Close()) && cleanupOkay;
            okay = cleanupOkay && okay;
            if (clean && okay && !shared_.abort.load() && spoolInitialized_)
            {
                // The source is now stopped; only its finite queued tail remains.
                try
                {
                    for (UINT count = 0; count < 129; ++count)
                    {
                        if (!pendingSlice_.frames && !pendingPcm_ && !audioQueue_.TryPop(pendingPcm_)) break;
                        if (!audioEnabled_) { pendingPcm_.reset(); pendingSlice_ = {}; break; }
                        if (!pendingSlice_.frames)
                        {
                            audio::FeedSlice slice;
                            const auto status = timeline_.Inspect(*pendingPcm_, slice);
                            Require(status != audio::TimelineResult::Failed, Reason::AudioFailed);
                            if (status == audio::TimelineResult::BeforeVideoEpoch) { pendingPcm_.reset(); continue; }
                            pendingSlice_ = slice;
                        }
                        Require(audioInitialized_ && audioEncoder_.Feed(pendingSlice_.samples, pendingSlice_.frames,
                            pendingSlice_.firstFrameIndex), Reason::AudioFailed);
                        pendingSlice_ = {}; pendingPcm_.reset();
                    }
                    if (audioInitialized_) Require(audioEncoder_.Drain(), Reason::AudioFailed);
                    Require(config_.losslessVideo ? losslessVideo_.Drain() : video_.Drain(), Reason::EncoderFailed);
                }
                catch (...) { okay = false; }
            }
            const HRESULT videoClose = config_.losslessVideo ? losslessVideo_.Close() : video_.Close();
            if (config_.losslessVideo && FAILED(videoClose))
            {
                EmitFailureDiagnostic(Reason::CleanupFailed, videoClose, "video_cleanup");
                TerminateSelf(); // Retained driver resources belong to this disposable helper.
            }
            cleanupOkay = SUCCEEDED(videoClose) && cleanupOkay;
            cleanupOkay = SUCCEEDED(audioEncoder_.Close()) && cleanupOkay;
            converter_ = hdr::HdrFrameConverter{};
            pendingPcm_.reset(); pendingSlice_ = {}; bootstrap_.reset();
            cleanupOkay = process_.Close() && cleanupOkay;
            if (mfStarted_) { cleanupOkay = SUCCEEDED(MFShutdown()) && cleanupOkay; mfStarted_ = false; }
            okay = cleanupOkay && okay;
            if (clean && okay && !preserveBuffer_ && spoolInitialized_)
                okay = SUCCEEDED(spool_.DiscardOwnedBuffer()) && okay;
            cleanupOkay = SUCCEEDED(spool_.Close()) && cleanupOkay;
            cleanupOkay = audioStop_.Close() && cleanupOkay;
            if (runtimeStarted_) { RoUninitialize(); runtimeStarted_ = false; }
            closed_ = true;
            cleanupSucceeded_ = cleanupOkay;
            okay = cleanupOkay && okay;
            if (!okay) (void)Fail(reason_ == Reason::None ? Reason::StorageFailed : reason_);
            return okay;
        }

        void EmitResult(Shared& shared, const protocol::Result& result)
        {
            std::string line;
            Require(protocol::SerializeResult(result, line), Reason::ProtocolError);
            shared.Emit(std::move(line));
        }
        void EmitState(Shared& shared, const std::string& session, protocol::State state, Reason reason,
            const protocol::LosslessBuffer* losslessBuffer = nullptr)
        {
            std::string line;
            Require(protocol::SerializeState(session, state, reason, line, losslessBuffer), Reason::ProtocolError);
            shared.Emit(std::move(line));
        }
    }

    unsigned RunDiagnosticContracts() noexcept
    {
        unsigned count = 0;
        bool passed = true;
        const auto test = [&](bool value) { ++count; passed = passed && value; };
        DiagnosticSnapshot value;
        std::array<char, DiagnosticCapacity> output{};
        test(SerializeDiagnostic(value, output) == 0 && output[0] == 0);
        value.first.Record(Reason::EncoderFailed, E_ACCESSDENIED, "video_schedule");
        value.first.Record(Reason::StorageFailed, E_FAIL, "cleanup");
        test(value.first.reason == Reason::EncoderFailed && value.first.hr == E_ACCESSDENIED &&
            std::strcmp(value.first.stage, "video_schedule") == 0);
        value.first.schedulerLagKnown = true; value.first.schedulerLag100ns = 2500001;
        value.first.sourceAgeKnown = true; value.first.sourceAge100ns = -1;
        value.first.localFrameAgeKnown = true; value.first.localFrameAge100ns = 120;
        value.audioResidual100ns = -213; value.audioAllowance100ns = 212;
        value.first.videoPackets = (std::numeric_limits<std::uint64_t>::max)();
        auto size = SerializeDiagnostic(value, output);
        test(size > 0 && size < output.size() && output[size - 1] == '\n' && output[size] == 0);
        test(std::strstr(output.data(), "\"mode\":\"recorder_failure\"") != nullptr);
        test(std::strstr(output.data(), "\"hr\":2147942405") != nullptr);
        test(std::strstr(output.data(), "\"schedulerLag100ns\":2500001") != nullptr &&
            std::strstr(output.data(), "\"sourceAge100ns\":-1") != nullptr);
        test(std::strstr(output.data(), "\"localFrameAgeKnown\":true") != nullptr &&
            std::strstr(output.data(), "\"localFrameAge100ns\":120") != nullptr);
        test(std::strstr(output.data(), "\"audioResidual100ns\":-213") != nullptr &&
            std::strstr(output.data(), "18446744073709551615") != nullptr);
        value.capture.reason = "C:\\private\\file"; value.video.reason = "\"injected\":true";
        value.timelineReason = nullptr;
        size = SerializeDiagnostic(value, output);
        test(size > 0 && std::strstr(output.data(), "private") == nullptr && std::strstr(output.data(), "injected") == nullptr);
        test(std::strcmp(DiagnosticToken(""), "unknown") == 0 &&
            std::strcmp(DiagnosticToken("audio_timeline_clock_outside_policy"), "audio_timeline_clock_outside_policy") == 0);
        std::array<char, 97> longToken{}; longToken.fill('a'); longToken.back() = 0;
        test(std::strcmp(DiagnosticToken(longToken.data()), "unknown") == 0);
        test(CaptureReason("capture_frame_failed", DXGI_ERROR_DEVICE_RESET) == Reason::CaptureReconnecting);
        test(CaptureReason("display_color_unsupported", E_INVALIDARG) == Reason::UnsupportedFormat);
        test(CaptureReason("display_color_changed", E_INVALIDARG) == Reason::CaptureReconnecting);
        test(CaptureReason("duplication_access_lost", DXGI_ERROR_ACCESS_LOST) == Reason::CaptureReconnecting);
        test(CaptureReason("duplication_busy", DXGI_ERROR_NOT_CURRENTLY_AVAILABLE) == Reason::CaptureReconnecting);
        test(CaptureReason("duplication_desktop_unavailable", E_ACCESSDENIED) == Reason::CaptureReconnecting);
        test(CaptureReason("duplication_mode_changed", E_FAIL) == Reason::CaptureReconnecting);
        test(CaptureReason("capture_surface_changed", E_FAIL) == Reason::CaptureReconnecting);
        test(CaptureReason("duplication_format_unsupported", E_FAIL) == Reason::UnsupportedFormat);
        test(CaptureReason("target_focus_lost", E_FAIL) == Reason::FocusLost);
        test(CaptureReason("target_not_fullscreen", E_FAIL) == Reason::FullscreenRequired);
        test(VideoReason(DXGI_ERROR_DEVICE_REMOVED) == Reason::EncoderReconnecting && VideoReason(E_FAIL) == Reason::EncoderFailed);
        test(AudioReason(AUDCLNT_E_DEVICE_INVALIDATED, "audio_get_buffer_failed") == Reason::AudioReconnecting &&
            AudioReason(E_ACCESSDENIED, "process_audio_activation_result_failed") == Reason::AudioCaptureFailed);
        audio::Evidence audioCleanup;
        audioCleanup.resourcesReleased = true; audioCleanup.hr = AUDCLNT_E_DEVICE_INVALIDATED;
        audioCleanup.stopHr = AUDCLNT_E_DEVICE_INVALIDATED;
        test(AudioCleanupSucceeded(audioCleanup));
        audioCleanup.handleCloseHr = E_FAIL; test(!AudioCleanupSucceeded(audioCleanup));
        audioCleanup.handleCloseHr = S_OK; audioCleanup.resourcesReleased = false;
        test(!AudioCleanupSucceeded(audioCleanup));
        return passed ? count : 0;
    }

    int RunStdioRecorder() noexcept
    {
        std::unique_ptr<Shared> shared;
        std::unique_ptr<MediaSession> media;
        protocol::Command config;
        Policy policy;
        Reason recoverableShutdown = Reason::None;
        std::int64_t lastRequest = 0, activeRequest = 0;
        bool configured = false, started = false, announcedReady = false, announcedStale = false, stopping = false;
        int exitCode = 0;
        try
        {
            shared = std::make_unique<Shared>(); shared->Start();
            for (;;)
            {
                if (shared->abort.load()) throw Failure{ shared->eof.load() ? Reason::ParentClosed : Reason::ProtocolError, E_ABORT };
                protocol::Command command;
                if (shared->Take(command))
                {
                    Require(command.request > lastRequest && (!configured || command.session == config.session), Reason::ProtocolError);
                    lastRequest = command.request; activeRequest = command.request;
                    switch (command.kind)
                    {
                    case protocol::CommandKind::Config:
                        Require(!configured && !started, Reason::ProtocolError);
                        config = command;
                        Require(spool::ValidateSessionPath(config.spoolDirectory), Reason::StorageFailed);
                        Require(BuildPolicy({ config.durationSeconds, config.height, config.frameRate, config.quality }, policy),
                            Reason::ProtocolError);
                        configured = true;
                        EmitResult(*shared, { config.session, command.request, true, Reason::None, std::nullopt });
                        EmitState(*shared, config.session, protocol::State::Waiting, Reason::WaitingForGame);
                        break;
                    case protocol::CommandKind::Start:
                        Require(configured && !started, Reason::ProtocolError);
                        started = true; shared->startup.store(GetTickCount64()); shared->mediaActive.store(true);
                        media = std::make_unique<MediaSession>(*shared, config, policy);
                        if (!media->Start(command)) throw Failure{ media->Error(), E_FAIL };
                        EmitResult(*shared, { config.session, command.request, true, Reason::None, std::nullopt });
                        break;
                    case protocol::CommandKind::Save:
                        Require(configured && started && media, Reason::ProtocolError);
                        if (media->Saving())
                            EmitResult(*shared, { config.session, command.request, false, Reason::SaveInProgress, std::nullopt });
                        else if (!media->Ready())
                            EmitResult(*shared, { config.session, command.request, false, Reason::NotReady, std::nullopt });
                        else if (!media->BeginSave(command))
                            EmitResult(*shared, { config.session, command.request, false, media->Error(), std::nullopt });
                        else EmitState(*shared, config.session, protocol::State::Saving, Reason::None);
                        break;
                    case protocol::CommandKind::Stop:
                        Require(configured, Reason::ProtocolError);
                        stopping = true; shared->stopping.store(GetTickCount64()); shared->startup.store(0);
                        shared->exportCancel.store(true);
                        if (media && media->Saving())
                        {
                            protocol::Result pending;
                            if (media->CollectSave(pending, true)) EmitResult(*shared, pending);
                        }
                        if (media && !media->Close(true)) throw Failure{ media->Error(), E_FAIL };
                        EmitResult(*shared, { config.session, command.request, true, Reason::None, std::nullopt });
                        EmitState(*shared, config.session, protocol::State::Stopped, Reason::Stopped);
                        break;
                    default: throw Failure{ Reason::ProtocolError, E_INVALIDARG };
                    }
                    activeRequest = 0;
                    if (stopping) break;
                }
                if (media)
                {
                    if (!media->Tick()) throw Failure{ media->Error(), E_FAIL };
                    if (media->Ready() && !media->Saving() &&
                        (!announcedReady || announcedStale != media->SourceStale() || media->BufferStatusChanged()))
                    {
                        announcedReady = true;
                        announcedStale = media->SourceStale();
                        EmitState(*shared, config.session, protocol::State::Buffering,
                            announcedStale ? Reason::CaptureStale : (media->HasAudio() ? Reason::None : Reason::AudioUnavailable),
                            media->LosslessBufferStatus());
                        media->BufferStatusAnnounced();
                    }
                    protocol::Result result;
                    if (media->CollectSave(result, false))
                    {
                        EmitResult(*shared, result);
                        announcedStale = media->SourceStale();
                        EmitState(*shared, config.session, protocol::State::Buffering,
                            announcedStale ? Reason::CaptureStale : (media->HasAudio() ? Reason::None : Reason::AudioUnavailable),
                            media->LosslessBufferStatus());
                        media->BufferStatusAnnounced();
                    }
                }
                shared->progress.store(GetTickCount64());
                const DWORD wait = WaitForSingleObject(shared->wake.value, media ? media->WaitMilliseconds() : INFINITE);
                Require(wait != WAIT_FAILED, Reason::ProtocolError);
            }
        }
        catch (const Failure& failure)
        {
            exitCode = failure.reason == Reason::ParentClosed ? 0 : 3;
            if (protocol::IsRecoverable(failure.reason)) recoverableShutdown = failure.reason;
            if (shared)
            {
                shared->stopping.store(GetTickCount64()); shared->startup.store(0); shared->exportCancel.store(true);
                if (shared->watchdog.joinable() && failure.reason != Reason::ParentClosed)
                {
                    if (media) media->EmitFailureDiagnostic(failure.reason, failure.hr, "host_loop");
                    else
                    {
                        DiagnosticSnapshot snapshot;
                        snapshot.first.Record(failure.reason, failure.hr, "host_configuration");
                        WriteDiagnostic(snapshot);
                    }
                }
                try
                {
                    if (!config.session.empty() && !shared->eof.load() && !shared->outputFailed.load())
                    {
                        if (activeRequest) EmitResult(*shared, { config.session, activeRequest, false, failure.reason, std::nullopt });
                        if (recoverableShutdown == Reason::None)
                            EmitState(*shared, config.session, protocol::State::Error, failure.reason);
                    }
                }
                catch (...) {}
            }
        }
        catch (...)
        {
            exitCode = 3;
            if (shared)
            {
                shared->stopping.store(GetTickCount64()); shared->startup.store(0); shared->exportCancel.store(true);
                if (shared->watchdog.joinable())
                {
                    if (media) media->EmitFailureDiagnostic(Reason::ProtocolError, E_FAIL, "host_exception");
                    else
                    {
                        DiagnosticSnapshot snapshot;
                        snapshot.first.Record(Reason::ProtocolError, E_FAIL, "host_exception");
                        WriteDiagnostic(snapshot);
                    }
                }
            }
        }
        if (shared)
        {
            if (!shared->stopping.load()) shared->stopping.store(GetTickCount64());
            if (media && !media->Close(false)) exitCode = 3;
            if (recoverableShutdown != Reason::None)
            {
                const bool cleaned = !media || media->CleanupSucceeded();
                exitCode = cleaned ? 0 : 3;
                try
                {
                    if (!config.session.empty() && !shared->eof.load() && !shared->outputFailed.load())
                        EmitState(*shared, config.session, cleaned ?
                            (recoverableShutdown == Reason::WindowMinimized || recoverableShutdown == Reason::FocusLost ||
                                recoverableShutdown == Reason::FullscreenRequired ? protocol::State::Paused : protocol::State::Reconnecting) :
                            protocol::State::Error, cleaned ? recoverableShutdown : Reason::CleanupFailed);
                }
                catch (...) { exitCode = 3; }
            }
            media.reset(); shared->mediaActive.store(false); shared->startup.store(0);
            shared->StopPipes();
        }
        return exitCode;
    }
}
