#include "RecorderProtocol.h"

#include <array>
#include <iostream>
#include <limits>
#include <string>

namespace
{
    using namespace recorder::protocol;
    constexpr const char* Session = "0123456789abcdef0123456789abcdef";
    constexpr const char* Clip = "fedcba9876543210fedcba9876543210";
    struct Failure { const char* contract; };
    unsigned checks = 0;
    void Check(bool value, const char* contract) { if (!value) throw Failure{ contract }; ++checks; }
    std::string CommandLine(std::string_view kind, std::string_view extra = {}, std::string_view request = "1")
    {
        return "{\"v\":2,\"session\":\"" + std::string(Session) + "\",\"request\":" + std::string(request) +
            ",\"command\":\"" + std::string(kind) + '"' + std::string(extra) + '}';
    }
    std::string Spool() { return std::string("C:\\Clips\\.wisp-recorder-") + Session; }
    std::string JsonStringContent(std::string_view value)
    {
        constexpr char hex[] = "0123456789abcdef";
        std::string out;
        for (const auto character : value)
        {
            const auto byte = static_cast<unsigned char>(character);
            if (byte == '\\' || byte == '"') { out += '\\'; out += character; }
            else if (byte < 0x20) { out += "\\u00"; out += hex[byte >> 4]; out += hex[byte & 15]; }
            else out += character;
        }
        return out;
    }
    std::string ConfigEncoded(std::string_view encodedPath)
    {
        return CommandLine("config", ",\"durationSeconds\":60,\"height\":1080,\"frameRate\":60,\"quality\":75,\"gameAudio\":true,\"losslessVideo\":false,\"spoolDirectory\":\"" + std::string(encodedPath) + '"');
    }
    std::string Config(std::string path = Spool()) { return ConfigEncoded(JsonStringContent(path)); }
    std::string Start(std::string_view pid = "42", std::string_view window = "123", std::string_view time = "456")
    {
        return CommandLine("start", ",\"processId\":" + std::string(pid) + ",\"window\":\"" + std::string(window) +
            "\",\"creationFileTime\":\"" + std::string(time) + '"');
    }
    std::string Save(std::string path = std::string("C:\\Clips\\") + Clip + ".mp4")
    {
        return CommandLine("save", ",\"clipId\":\"" + std::string(Clip) + "\",\"destination\":\"" + JsonStringContent(path) + '"');
    }
    void Replace(std::string& value, std::string_view before, std::string_view after)
    {
        const auto position = value.find(before); Check(position != std::string::npos, "fixture_replacement_exists");
        value.replace(position, before.size(), after);
    }
    bool Accepted(const std::string& line) { Command command; return ParseCommand(line, command); }

    void ValidCommands()
    {
        Command command;
        Check(ParseCommand(Config(), command) && command.kind == CommandKind::Config && command.durationSeconds == 60 &&
            command.height == 1080 && command.frameRate == 60 && command.quality == 75 && command.gameAudio, "config_exact_typed_fields");
        Check(command.session == Session && command.request == 1 && command.spoolDirectory == L"C:\\Clips\\.wisp-recorder-0123456789abcdef0123456789abcdef", "config_session_and_wide_path");
        Check(!command.borderlessAllowed, "borderless_missing_defaults_off");
        Check(!command.systemAudio, "system_audio_missing_defaults_to_game");
        auto systemAudio = Config(); systemAudio.insert(systemAudio.size() - 1, ",\"systemAudio\":true");
        Check(ParseCommand(systemAudio, command) && command.systemAudio, "system_audio_explicit_boolean");
        systemAudio.insert(systemAudio.size() - 1, ",\"borderlessAllowed\":false");
        Check(ParseCommand(systemAudio, command) && command.systemAudio && !command.borderlessAllowed, "both_optional_fields_accepted");
        auto unknownAudio = systemAudio; unknownAudio.insert(unknownAudio.size() - 1, ",\"extra\":0");
        Check(!Accepted(unknownAudio), "audio_still_rejects_unknown_field");
        Replace(systemAudio, "\"systemAudio\":true", "\"systemAudio\":1");
        Check(!Accepted(systemAudio), "system_audio_rejects_non_boolean");
        Check(!Accepted(CommandLine("stop", ",\"systemAudio\":true")), "audio_scope_config_only");
        auto borderless = Config(); borderless.insert(borderless.size() - 1, ",\"borderlessAllowed\":true");
        Check(ParseCommand(borderless, command) && command.borderlessAllowed, "borderless_explicit_boolean");
        Replace(borderless, "\"borderlessAllowed\":true", "\"borderlessAllowed\":false");
        Check(ParseCommand(borderless, command) && !command.borderlessAllowed, "borderless_explicit_false");
        Replace(borderless, "\"borderlessAllowed\":false", "\"borderlessAllowed\":true");
        auto unknown = borderless; unknown.insert(unknown.size() - 1, ",\"extra\":0");
        Check(!Accepted(unknown), "borderless_rejects_extra_field");
        Replace(borderless, "\"borderlessAllowed\":true", "\"borderlessAllowed\":1");
        Check(!Accepted(borderless), "borderless_rejects_non_boolean");
        borderless = Config(); borderless.insert(borderless.size() - 1, ",\"borderlessAllowed\":true,\"borderlessAllowed\":true");
        Check(!Accepted(borderless), "borderless_rejects_duplicate");
        Check(!Accepted(CommandLine("stop", ",\"borderlessAllowed\":true")), "borderless_config_only");
        Check(ParseCommand(Start(), command) && command.kind == CommandKind::Start && command.processId == 42 && command.window == 123 && command.creationFileTime == 456, "start_exact_target_identity");
        Check(ParseCommand(CommandLine("pause"), command) && command.kind == CommandKind::Pause,
            "pause_has_no_payload");
        Check(!Accepted(CommandLine("pause", ",\"processId\":42")), "pause_rejects_target_payload");
        auto resume = Start(); Replace(resume, "\"start\"", "\"resume\"");
        Check(ParseCommand(resume, command) && command.kind == CommandKind::Resume && command.processId == 42 &&
            command.window == 123 && command.creationFileTime == 456, "resume_exact_target_identity");
        Check(!Accepted(CommandLine("resume")), "resume_requires_verified_identity");
        Replace(resume, "\"window\":\"123\"", "\"window\":\"0\"");
        Check(!Accepted(resume), "resume_rejects_zero_window");
        Check(ParseCommand(Save(), command) && command.kind == CommandKind::Save && command.clipId == Clip, "save_guid_destination");
        Check(Accepted(Config(std::string("\\\\server\\share\\.wisp-recorder-") + Session)), "unc_storage_syntax_accepted");
        Check(ParseCommand(CommandLine("stop"), command) && command.kind == CommandKind::Stop && command.destination.empty(), "stop_has_no_payload");
        Check(ParseCommand(CommandLine("stop", {}, "9223372036854775807"), command) && command.request == (std::numeric_limits<std::int64_t>::max)(), "request_exact_int64_maximum");
        Check(ParseCommand(Start("4294967295", "18446744073709551615", "18446744073709551615"), command) &&
            command.processId == (std::numeric_limits<std::uint32_t>::max)() && command.window == (std::numeric_limits<std::uint64_t>::max)(), "unsigned_identity_exact_maximum");
        auto line = CommandLine("stop"); line += '\r';
        Check(Accepted(line), "framed_terminal_cr_accepted");
        line = CommandLine("stop"); line.append(MaximumLineBytes - line.size(), ' ');
        Check(Accepted(line), "exact_line_limit_accepted");
        line += ' '; Check(!Accepted(line), "line_over_limit_rejected");
    }
    void ExactSchemaAndNumbers()
    {
        auto mode = Config(); Replace(mode, "\"losslessVideo\":false", "\"losslessVideo\":true");
        Command lossless;
        Check(ParseCommand(mode, lossless) && lossless.losslessVideo, "explicit_lossless_mode_preserved");
        mode = Config(); Replace(mode, ",\"losslessVideo\":false", "");
        Check(!Accepted(mode), "missing_encoding_mode_rejected");
        mode = Config(); Replace(mode, "\"losslessVideo\":false", "\"losslessVideo\":1");
        Check(!Accepted(mode), "encoding_mode_requires_boolean");
        mode = Config(); Replace(mode, "\"v\":2", "\"v\":1");
        Check(!Accepted(mode), "old_protocol_cannot_ignore_encoding_mode");
        for (const char* value : { "0", "-1", "9223372036854775808", "1.0", "1e0", "+1", "01", "true", "\"1\"", "null" })
            Check(!Accepted(CommandLine("stop", {}, value)), "request_numeric_grammar_and_bounds");
        for (const char* value : { "0", "-1", "4294967296", "1e1", "\"42\"" })
            Check(!Accepted(Start(value)), "pid_uint32_no_coercion");
        for (const char* value : { "0", "-1", "+1", "1.0", "1e3", "18446744073709551616", " 1", "", "000000000000000000001" })
            Check(!Accepted(Start("42", value)), "window_positive_decimal_uint64_only");
        auto value = Start(); Replace(value, "\"window\":\"123\"", "\"window\":123");
        Check(!Accepted(value), "window_requires_string");
        Check(!Accepted(CommandLine("stop", ",\"v\":1")), "duplicate_key_rejected");
        Check(!Accepted(CommandLine("stop", ",\"\\u0076\":1")), "escaped_duplicate_key_rejected");
        Check(!Accepted(CommandLine("stop", ",\"extra\":0")), "unknown_key_rejected");
        Check(!Accepted(CommandLine("stop", ",\"extra\":{}")), "nested_object_rejected");
        Check(!Accepted(CommandLine("stop", ",\"extra\":[]")), "array_rejected");
        Check(!Accepted(CommandLine("anything")), "unknown_command_rejected");
        value = Config(); Replace(value, "\"gameAudio\":true", "\"gameAudio\":1"); Check(!Accepted(value), "boolean_not_numeric");
        value = Config(); Replace(value, "\"gameAudio\":true", "\"gameAudio\":false"); Check(!Accepted(value), "v1_requests_game_audio");
        value = Config(); Replace(value, "\"durationSeconds\":60", "\"durationSeconds\":31"); Check(!Accepted(value), "duration_step_validated");
        value = Config(); Replace(value, "\"height\":1080", "\"height\":1081"); Check(!Accepted(value), "resolution_allowlist");
        value = Config(); Replace(value, "\"frameRate\":60", "\"frameRate\":120"); Check(!Accepted(value), "frame_rate_allowlist");
        value = Config(); Replace(value, "\"quality\":75", "\"quality\":101"); Check(!Accepted(value), "quality_upper_bound");
        value = CommandLine("stop"); Replace(value, Session, "00000000000000000000000000000000"); Check(!Accepted(value), "empty_guid_rejected");
        Command cleared; Check(ParseCommand(Start(), cleared), "output_clear_fixture");
        Check(!ParseCommand("{}", cleared) && cleared.kind == CommandKind::Invalid && cleared.session.empty() && cleared.processId == 0, "failure_clears_prior_command");
    }
    void UnicodeAndPaths()
    {
        Command escaped, raw;
        const auto suffix = std::string("\\.wisp-recorder-") + Session;
        const auto encodedSuffix = std::string(R"(\\.wisp-recorder-)") + Session;
        Check(ParseCommand(ConfigEncoded(std::string(R"(C:\\Clips\\caf\u00e9\\\uD83D\uDE97)") + encodedSuffix), escaped), "valid_escaped_surrogate_pair");
        Check(ParseCommand(Config(std::string("C:\\Clips\\caf\xc3\xa9\\\xf0\x9f\x9a\x97") + suffix), raw), "valid_raw_utf8_supplementary_path");
        Check(escaped.spoolDirectory == raw.spoolDirectory, "raw_and_escaped_unicode_equal_utf16");
        for (const std::string bad : { std::string("\\uD800"), std::string("\\uDC00"), std::string("\\uD800\\u0041"),
            std::string("\\u0000"), std::string("\\uGGGG"), std::string("\\x41"), std::string("\xc0\x80"),
            std::string("\xed\xa0\x80"), std::string("\xf4\x90\x80\x80"), std::string("\xe2\x82"), std::string("\x80") })
            Check(!Accepted(ConfigEncoded(std::string(R"(C:\\Clips\\)") + bad + encodedSuffix)), "malformed_unicode_or_escape_rejected");
        for (const char* prefix : { "relative\\", "C:relative\\", "C:\\\\", "C:\\..\\", "C:\\NUL\\", "C:\\clips.\\",
            "C:\\clips \\", "C:\\clips:stream\\", "\\\\?\\C:\\", "\\\\.\\C:\\", "C:\\bad\nname\\" })
            Check(!Accepted(Config(std::string(prefix) + ".wisp-recorder-" + Session)), "unsafe_path_syntax_rejected");
        Check(!Accepted(Save("C:\\Clips\\different.mp4")), "save_basename_requires_reserved_guid");
        Check(!Accepted(Config("C:\\Clips\\different")), "spool_basename_requires_session_guid");
        auto nul = Config(); nul.insert(5, 1, '\0'); Check(!Accepted(nul), "raw_nul_rejected");
        Check(!Accepted(std::string("\xef\xbb\xbf") + CommandLine("stop")), "utf8_bom_rejected");
        Check(!Accepted(CommandLine("stop") + "\n"), "caller_must_supply_single_framed_line");
    }
    void OutputContracts()
    {
        std::string line;
        Result result{ Session, (std::numeric_limits<std::int64_t>::max)(), true, Reason::None, std::nullopt };
        Check(SerializeResult(result, line) && line.find("\"request\":9223372036854775807") != std::string::npos, "result_int64_not_double");
        Check(line.find("destination") == std::string::npos && line.find("processId") == std::string::npos && line.back() == '}', "response_contains_no_path_or_target");
        result.media = SavedMedia{ Clip, 1024, 1920, 1080, 60, 10, 10000010, false };
        Check(!SerializeResult(result, line) && line.empty(), "silent_media_requires_warning_reason");
        result.reason = Reason::AudioUnavailable;
        Check(SerializeResult(result, line) && line.find("\"hasAudio\":false") != std::string::npos && line.find("audio_unavailable") != std::string::npos, "valid_silent_media_explicit");
        result.media->hasAudio = true;
        Check(!SerializeResult(result, line), "audible_media_cannot_claim_silent_reason");
        result.reason = Reason::None;
        Check(SerializeResult(result, line), "valid_audible_media");
        result.media->sizeLimited = true;
        Check(!SerializeResult(result, line), "standard_media_cannot_claim_lossless_size_limit");
        result.media->losslessVideo = true;
        Check(SerializeResult(result, line) && line.find("\"sizeLimited\":true") != std::string::npos,
            "lossless_saved_size_limit_explicit");
        result.media->sizeLimited = false; result.media->losslessVideo = false;
        result.media->fileBytes = MaximumMediaBytes + 1; Check(!SerializeResult(result, line), "output_file_size_bound");
        result.media->fileBytes = 1024; result.media->end100ns = (std::numeric_limits<std::int64_t>::max)();
        Check(!SerializeResult(result, line), "output_duration_bound_no_overflow");
        result.media->start100ns = (std::numeric_limits<std::int64_t>::max)() - 100; Check(SerializeResult(result, line), "large_valid_media_timestamps_preserved");
        result.media->start100ns = -1; Check(!SerializeResult(result, line), "negative_media_time_rejected");
        result.media.reset(); result.ok = false; result.reason = Reason::StorageFailed;
        Check(SerializeResult(result, line) && line.find("storage_failed") != std::string::npos, "fixed_failure_reason_only");
        result.reason = static_cast<Reason>(999); Check(!SerializeResult(result, line) && line.empty(), "unknown_result_reason_rejected");
        for (unsigned index = 0; index <= static_cast<unsigned>(Reason::ParentClosed); ++index)
            Check(SerializeState(Session, State::Buffering, static_cast<Reason>(index), line) && line.find("\"request\":0") != std::string::npos, "state_reason_allowlist_serializes");
        for (unsigned index = 0; index <= static_cast<unsigned>(State::Error); ++index)
            Check(SerializeState(Session, static_cast<State>(index), Reason::None, line), "state_allowlist_serializes");
        Check(!SerializeState(Session, static_cast<State>(999), Reason::None, line) && line.empty(), "unknown_state_rejected");
        Check(SerializeState(Session, State::Paused, Reason::WindowMinimized, line) && line.find("\"state\":\"paused\"") != std::string::npos,
            "minimized_paused_state");
        Check(line.find("\"bufferReady\":false") != std::string::npos, "initial_pause_not_saveable");
        Check(SerializeState(Session, State::Paused, Reason::FocusLost, line, nullptr, true) &&
            line.find("\"bufferReady\":true") != std::string::npos, "paused_standard_buffer_remains_saveable");
        Check(!SerializeState(Session, State::Buffering, Reason::None, line, nullptr, true), "readiness_override_only_paused");
        Check(SerializeState(Session, State::Buffering, Reason::CaptureStale, line) && line.find("capture_stale") != std::string::npos,
            "stale_frames_remain_buffering");
        Check(line.find("losslessBuffer") == std::string::npos, "standard_state_omits_lossless_buffer");
        LosslessBuffer buffer{166666, 1234, 4096, true};
        Check(SerializeState(Session, State::Buffering, Reason::None, line, &buffer) &&
            line.find("\"losslessBuffer\":{\"duration100ns\":166666,\"payloadBytes\":1234,\"budgetBytes\":4096,\"sizeLimited\":true}") != std::string::npos,
            "lossless_buffer_exact_scalar_object");
        Check(!SerializeState(Session, State::Saving, Reason::None, line, &buffer) && line.empty(), "saving_omits_lossless_buffer");
        Check(!SerializeState(Session, State::Paused, Reason::FocusLost, line, &buffer), "unready_pause_cannot_report_buffer");
        Check(SerializeState(Session, State::Paused, Reason::FocusLost, line, &buffer, true) &&
            line.find("\"bufferReady\":true") != std::string::npos && line.find("\"losslessBuffer\":{") != std::string::npos,
            "paused_lossless_buffer_preserves_bounds");
        buffer.duration100ns = 0;
        Check(!SerializeState(Session, State::Buffering, Reason::None, line, &buffer), "lossless_buffer_empty_duration_rejected");
        buffer.duration100ns = -1;
        Check(!SerializeState(Session, State::Buffering, Reason::None, line, &buffer), "lossless_buffer_negative_duration_rejected");
        buffer.duration100ns = 3000000001ll;
        Check(!SerializeState(Session, State::Buffering, Reason::None, line, &buffer), "lossless_buffer_duration_bounded");
        buffer.duration100ns = 3000000000ll; buffer.payloadBytes = 0;
        Check(!SerializeState(Session, State::Buffering, Reason::None, line, &buffer), "lossless_buffer_empty_payload_rejected");
        buffer.payloadBytes = 4097;
        Check(!SerializeState(Session, State::Buffering, Reason::None, line, &buffer), "lossless_buffer_payload_cannot_exceed_budget");
        buffer.budgetBytes = 12ull * 1024 * 1024 * 1024 + 16ull * 1024 * 1024;
        buffer.payloadBytes = buffer.budgetBytes;
        Check(SerializeState(Session, State::Buffering, Reason::AudioUnavailable, line, &buffer), "lossless_buffer_maximum_valid");
        ++buffer.budgetBytes;
        Check(!SerializeState(Session, State::Buffering, Reason::None, line, &buffer), "lossless_buffer_budget_bounded");
        Check(SerializeState(Session, State::Error, Reason::LosslessStorageLow, line) &&
            line.find("lossless_storage_low") != std::string::npos && !IsRecoverable(Reason::LosslessStorageLow),
            "lossless_storage_low_specific_and_not_retried");
        Check(SerializeState(Session, State::Error, Reason::LosslessEncoderUnsupported, line) &&
            line.find("lossless_encoder_unsupported") != std::string::npos && !IsRecoverable(Reason::LosslessEncoderUnsupported),
            "unsupported_lossless_encoder_specific_and_not_retried");
        Check(!IsRecoverable(Reason::CaptureStale) && IsRecoverable(Reason::SchedulerLate) && IsRecoverable(Reason::AudioReconnecting),
            "interruption_recovery_allowlist");
        Check(!IsRecoverable(Reason::CleanupFailed) && !IsRecoverable(Reason::ProtocolError) &&
            !IsRecoverable(Reason::StorageFailed) && !IsRecoverable(Reason::UnsupportedGpu) && !IsRecoverable(Reason::EncoderFailed),
            "unsafe_or_unknown_errors_not_retried");
        Check(!SerializeState("unsafe\"text", State::Error, Reason::ProtocolError, line), "response_strings_cannot_inject_json");
        Check(SerializeState("ABCDEF0123456789ABCDEF0123456789", State::Waiting, Reason::None, line) &&
            line.find("abcdef0123456789abcdef0123456789") != std::string::npos, "output_guid_case_canonical");
    }
}

int main()
{
    try
    {
        ValidCommands(); ExactSchemaAndNumbers(); UnicodeAndPaths(); OutputContracts();
        std::cout << "{\"mode\":\"recorder_protocol_cpu_contracts\",\"passed\":" << checks
            << ",\"graphicsInitialized\":false,\"captureUsed\":false}\n";
        return 0;
    }
    catch (const Failure& failure) { std::cout << "{\"passed\":false,\"reason\":\"" << failure.contract << "\"}\n"; return 1; }
    catch (...) { std::cout << "{\"passed\":false,\"reason\":\"unexpected_contract_exception\"}\n"; return 1; }
}
