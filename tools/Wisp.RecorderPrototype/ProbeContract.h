#pragma once

#include <cstdint>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

namespace recorder
{
    enum class Mode { Help, SelfTest, Probe, Capture, Fixture };
    enum class Scenario { Normal, Resize, Minimize, Close };
    enum class StopReason
    {
        None, Completed, Cancelled, ProcessExited, WrongWindow, TargetClosed,
        Minimized, Hidden, ForegroundLost, SizeChanged, MonitorChanged,
        DeviceLost, DisplayColorChanged, FrameError, FrameClockInvalid, CaptureItemClosed,
        NoFrames, GameStarted, TeardownTimeout
    };

    inline const char* Name(StopReason reason) noexcept
    {
        switch (reason)
        {
        case StopReason::None: return "none";
        case StopReason::Completed: return "completed";
        case StopReason::Cancelled: return "cancelled";
        case StopReason::ProcessExited: return "process_exited";
        case StopReason::WrongWindow: return "window_identity_changed";
        case StopReason::TargetClosed: return "window_closed";
        case StopReason::Minimized: return "window_minimized";
        case StopReason::Hidden: return "window_hidden";
        case StopReason::ForegroundLost: return "foreground_lost";
        case StopReason::SizeChanged: return "size_changed";
        case StopReason::MonitorChanged: return "monitor_changed";
        case StopReason::DeviceLost: return "device_lost";
        case StopReason::DisplayColorChanged: return "display_color_changed";
        case StopReason::FrameError: return "frame_error";
        case StopReason::FrameClockInvalid: return "frame_clock_invalid";
        case StopReason::CaptureItemClosed: return "capture_item_closed";
        case StopReason::NoFrames: return "frame_delivery_stopped";
        case StopReason::GameStarted: return "game_started_during_fixture";
        case StopReason::TeardownTimeout: return "callback_teardown_timeout";
        }
        return "unknown";
    }

    struct Arguments
    {
        Mode mode = Mode::Help;
        Scenario scenario = Scenario::Normal;
        std::uint64_t window = 0;
        std::uint32_t processId = 0;
        std::uint64_t creationTime = 0;
        unsigned seconds = 10;
        unsigned buffers = 2;
        bool fixtureHdrMetadata = false;
        bool captureHdrMetadata = false;
    };

    inline std::uint64_t ParseUnsigned(const std::wstring& input)
    {
        if (input.empty()) throw std::invalid_argument("empty_numeric_argument");
        std::size_t start = 0;
        unsigned base = 10;
        if (input.size() > 2 && input[0] == L'0' && (input[1] == L'x' || input[1] == L'X'))
        {
            start = 2;
            base = 16;
        }
        std::uint64_t result = 0;
        for (std::size_t index = start; index < input.size(); ++index)
        {
            const wchar_t value = input[index];
            unsigned digit = 99;
            if (value >= L'0' && value <= L'9') digit = static_cast<unsigned>(value - L'0');
            else if (value >= L'a' && value <= L'f') digit = static_cast<unsigned>(value - L'a') + 10;
            else if (value >= L'A' && value <= L'F') digit = static_cast<unsigned>(value - L'A') + 10;
            if (digit >= base || result > (std::numeric_limits<std::uint64_t>::max() - digit) / base)
                throw std::invalid_argument("invalid_numeric_argument");
            result = result * base + digit;
        }
        return result;
    }

    inline Arguments ParseArguments(const std::vector<std::wstring>& values)
    {
        Arguments result;
        if (values.empty()) return result;
        const auto& mode = values[0];
        if (mode == L"--help") result.mode = Mode::Help;
        else if (mode == L"--self-test") result.mode = Mode::SelfTest;
        else if (mode == L"--probe") result.mode = Mode::Probe;
        else if (mode == L"--capture") result.mode = Mode::Capture;
        else if (mode == L"--fixture") result.mode = Mode::Fixture;
        else throw std::invalid_argument("unknown_mode");
        if (result.mode != Mode::Capture && result.mode != Mode::Fixture)
        {
            if (values.size() != 1) throw std::invalid_argument("unexpected_arguments");
            return result;
        }
        unsigned seen = 0;
        for (std::size_t index = 1; index < values.size(); )
        {
            const auto& option = values[index];
            if (option == L"--fixture-hdr-metadata")
            {
                if (result.mode != Mode::Fixture) throw std::invalid_argument("hdr_metadata_is_fixture_only");
                if ((seen & 64) != 0) throw std::invalid_argument("duplicate_argument");
                seen |= 64;
                result.fixtureHdrMetadata = true;
                ++index;
                continue;
            }
            if (option == L"--capture-hdr-metadata")
            {
                if (result.mode != Mode::Capture) throw std::invalid_argument("hdr_metadata_is_capture_only");
                if ((seen & 128) != 0) throw std::invalid_argument("duplicate_argument");
                seen |= 128;
                result.captureHdrMetadata = true;
                ++index;
                continue;
            }
            if (index + 1 >= values.size()) throw std::invalid_argument("missing_argument_value");
            const auto& value = values[index + 1];
            unsigned flag = 0;
            if (option == L"--hwnd")
            {
                flag = 1;
                result.window = ParseUnsigned(value);
                if (result.window == 0) throw std::invalid_argument("invalid_window");
            }
            else if (option == L"--pid")
            {
                flag = 2;
                const auto number = ParseUnsigned(value);
                if (number == 0 || number > std::numeric_limits<std::uint32_t>::max())
                    throw std::invalid_argument("invalid_process_id");
                result.processId = static_cast<std::uint32_t>(number);
            }
            else if (option == L"--creation-time")
            {
                flag = 4;
                result.creationTime = ParseUnsigned(value);
                if (result.creationTime == 0) throw std::invalid_argument("invalid_creation_time");
            }
            else if (option == L"--seconds")
            {
                flag = 8;
                const auto number = ParseUnsigned(value);
                if (number < 1 || number > 60) throw std::invalid_argument("duration_out_of_range");
                result.seconds = static_cast<unsigned>(number);
            }
            else if (option == L"--buffers")
            {
                flag = 16;
                const auto number = ParseUnsigned(value);
                if (number < 2 || number > 3) throw std::invalid_argument("buffers_out_of_range");
                result.buffers = static_cast<unsigned>(number);
            }
            else if (option == L"--scenario")
            {
                flag = 32;
                if (value == L"normal") result.scenario = Scenario::Normal;
                else if (value == L"resize") result.scenario = Scenario::Resize;
                else if (value == L"minimize") result.scenario = Scenario::Minimize;
                else if (value == L"close") result.scenario = Scenario::Close;
                else throw std::invalid_argument("unknown_scenario");
            }
            else throw std::invalid_argument("unknown_argument");
            if ((seen & flag) != 0) throw std::invalid_argument("duplicate_argument");
            seen |= flag;
            index += 2;
        }
        if (result.mode == Mode::Capture && ((seen & 7) != 7 || (seen & 32) != 0))
            throw std::invalid_argument("capture_requires_immutable_target");
        if (result.mode == Mode::Fixture && (seen & 7) != 0)
            throw std::invalid_argument("fixture_cannot_target_another_window");
        if (result.mode == Mode::Fixture && result.scenario != Scenario::Normal && result.seconds < 2)
            throw std::invalid_argument("fixture_transition_requires_two_seconds");
        return result;
    }

    struct TargetSnapshot
    {
        bool processAlive = true;
        bool windowExists = true;
        bool sameProcess = true;
        bool minimized = false;
        bool visible = true;
        bool foreground = true;
        bool sameSize = true;
        bool sameMonitor = true;
        bool deviceHealthy = true;
    };

    inline StopReason ValidateSnapshot(const TargetSnapshot& value, bool requireForeground) noexcept
    {
        if (!value.processAlive) return StopReason::ProcessExited;
        if (!value.windowExists) return StopReason::TargetClosed;
        if (!value.sameProcess) return StopReason::WrongWindow;
        if (value.minimized) return StopReason::Minimized;
        if (!value.visible) return StopReason::Hidden;
        if (requireForeground && !value.foreground) return StopReason::ForegroundLost;
        if (!value.sameSize) return StopReason::SizeChanged;
        if (!value.sameMonitor) return StopReason::MonitorChanged;
        if (!value.deviceHealthy) return StopReason::DeviceLost;
        return StopReason::None;
    }

    inline bool IsValidCaptureSize(int width, int height) noexcept
    {
        return width > 0 && height > 0 && width <= 8192 && height <= 8192 &&
            static_cast<std::uint64_t>(width) * static_cast<std::uint64_t>(height) <= 33554432;
    }
}
