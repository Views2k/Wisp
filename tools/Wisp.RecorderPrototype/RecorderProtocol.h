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
        bool gameAudio = false;
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

    enum class State { Waiting, Buffering, Saving, Stopped, Error };
    enum class Reason
    {
        None, WaitingForGame, TargetExited, TargetChanged, WindowClosed, WindowMinimized,
        WindowResized, FocusLost, UnsupportedOs, UnsupportedGpu, UnsupportedFormat,
        CaptureFailed, EncoderFailed, AudioFailed, AudioCaptureFailed, AudioUnavailable,
        BufferFull, NoKeyframe, NotReady, SaveInProgress, StorageFailed, MuxFailed,
        ProtocolError, Cancelled, Stopped, ParentClosed
    };
    const char* Name(State state) noexcept;
    const char* Name(Reason reason) noexcept;

    struct SavedMedia
    {
        std::string clipId;
        std::uint64_t fileBytes = 0;
        std::uint32_t width = 0, height = 0, frameRate = 0;
        std::int64_t start100ns = 0, end100ns = 0;
        bool hasAudio = false;
    };
    struct Result
    {
        std::string session;
        std::int64_t request = 0;
        bool ok = false;
        Reason reason = Reason::ProtocolError;
        std::optional<SavedMedia> media;
    };

    // Canonical allowlisted output, WITHOUT trailing LF. No API accepts raw OS
    // error text, paths or target identity. Output is cleared on any refusal.
    bool SerializeResult(const Result& result, std::string& line) noexcept;
    bool SerializeState(std::string_view session, State state, Reason reason, std::string& line) noexcept;
}
