#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace recorder::protocol
{
    constexpr std::size_t MaximumLineBytes = 16384;
    constexpr std::uint64_t MaximumMediaBytes = 16ull * 1024 * 1024 * 1024;
    enum class CommandKind { Invalid, Config, Start, Save, Stop };
    struct Command
    {
        CommandKind kind = CommandKind::Invalid;
        std::string session;
        std::int64_t request = 0;
        std::uint32_t durationSeconds = 0, height = 0, frameRate = 0, quality = 0;
        bool gameAudio = false, borderlessAllowed = false, systemAudio = false, losslessVideo = false;
        std::wstring spoolDirectory;
        std::uint32_t processId = 0;
        std::uint64_t window = 0, creationFileTime = 0;
        std::string clipId;
        std::wstring destination;
    };

    // One bounded JSON line, excluding LF (one final CR is accepted). Pure
    // grammar and syntax validation: no IO, path existence or target discovery.
    // Session binding/request sequencing and path ownership stay with the host.
    bool ParseCommand(std::string_view line, Command& result) noexcept;

    enum class State { Waiting, Buffering, Saving, Paused, Reconnecting, Stopped, Error };
    enum class Reason
    {
        None, WaitingForGame, TargetExited, TargetChanged, WindowClosed, WindowMinimized,
        WindowResized, FocusLost, FullscreenRequired, UnsupportedOs, UnsupportedGpu, UnsupportedFormat,
        CaptureFailed, EncoderFailed, AudioFailed, AudioCaptureFailed, AudioUnavailable,
        CaptureStale, CaptureReconnecting, EncoderReconnecting, AudioReconnecting, SchedulerLate,
        BufferFull, NoKeyframe, NotReady, SaveInProgress, StorageFailed, LosslessStorageLow, MuxFailed,
        ProtocolError, CleanupFailed, Cancelled, Stopped, ParentClosed
    };
    const char* Name(State state) noexcept;
    const char* Name(Reason reason) noexcept;
    // An explicit new recording epoch is required. The managed owner rechecks
    // the exact game target and applies bounded retry/backoff after helper exit.
    bool IsRecoverable(Reason reason) noexcept;

    struct SavedMedia
    {
        std::string clipId;
        std::uint64_t fileBytes = 0;
        std::uint32_t width = 0, height = 0, frameRate = 0;
        std::int64_t start100ns = 0, end100ns = 0;
        bool hasAudio = false, losslessVideo = false, sizeLimited = false;
    };
    struct Result
    {
        std::string session;
        std::int64_t request = 0;
        bool ok = false;
        Reason reason = Reason::ProtocolError;
        std::optional<SavedMedia> media;
    };
    struct LosslessBuffer
    {
        std::int64_t duration100ns = 0;
        std::uint64_t payloadBytes = 0, budgetBytes = 0;
        bool sizeLimited = false;
        bool operator==(const LosslessBuffer& other) const noexcept
        {
            return duration100ns == other.duration100ns && payloadBytes == other.payloadBytes &&
                budgetBytes == other.budgetBytes && sizeLimited == other.sizeLimited;
        }
    };

    // Canonical allowlisted output, WITHOUT trailing LF. No API accepts raw OS
    // error text, paths or target identity. Output is cleared on any refusal.
    bool SerializeResult(const Result& result, std::string& line) noexcept;
    bool SerializeState(std::string_view session, State state, Reason reason, std::string& line,
        const LosslessBuffer* losslessBuffer = nullptr) noexcept;
}
