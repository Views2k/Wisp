#include "RecorderProtocol.h"

#include <array>
#include <charconv>
#include <initializer_list>
#include <limits>
#include <utility>

namespace recorder::protocol
{
    namespace
    {
        struct Invalid {};
        void Require(bool condition) { if (!condition) throw Invalid{}; }
        bool Digit(char value) noexcept { return value >= '0' && value <= '9'; }
        int Hex(char value) noexcept
        {
            if (Digit(value)) return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }
        bool Guid(std::string_view value) noexcept
        {
            if (value.size() != 32) return false;
            bool nonzero = false;
            for (char digit : value) { if (Hex(digit) < 0) return false; nonzero |= digit != '0'; }
            return nonzero;
        }
        std::string Ascii(const std::wstring& text)
        {
            std::string result;
            result.reserve(text.size());
            for (wchar_t value : text) { Require(value > 0 && value <= 127); result.push_back(static_cast<char>(value)); }
            return result;
        }
        std::string GuidText(const std::wstring& text)
        {
            auto result = Ascii(text); Require(Guid(result));
            for (auto& digit : result) if (digit >= 'A' && digit <= 'F') digit = static_cast<char>(digit + ('a' - 'A'));
            return result;
        }
        void CodePoint(std::wstring& out, std::uint32_t value)
        {
            Require(value != 0 && value <= 0x10ffff && !(value >= 0xd800 && value <= 0xdfff));
            if (value < 0x10000) out.push_back(static_cast<wchar_t>(value));
            else
            {
                value -= 0x10000;
                out.push_back(static_cast<wchar_t>(0xd800 + (value >> 10)));
                out.push_back(static_cast<wchar_t>(0xdc00 + (value & 0x3ff)));
            }
        }
        struct Value
        {
            enum class Type { String, Integer, Boolean } type = Type::String;
            std::wstring text;
            std::int64_t integer = 0;
            bool boolean = false;
        };
        struct Field { std::wstring key; Value value; };
        class Parser
        {
        public:
            explicit Parser(std::string_view line) : line_(line) {}
            void Parse()
            {
                Space(); Take('{'); Space();
                Require(Peek() != '}');
                while (true)
                {
                    Require(count_ < fields_.size());
                    auto key = String();
                    for (std::size_t index = 0; index < count_; ++index) Require(fields_[index].key != key);
                    Space(); Take(':'); Space();
                    auto value = Primitive();
                    fields_[count_++] = { std::move(key), std::move(value) };
                    Space();
                    if (Peek() == '}') { ++position_; break; }
                    Take(','); Space();
                }
                Space(); Require(position_ == line_.size());
            }
            void Keys(std::initializer_list<const wchar_t*> names, std::initializer_list<const wchar_t*> optional = {}) const
            {
                for (const auto* name : names) (void)Get(name);
                std::size_t expected = names.size();
                for (const auto* name : optional) if (Has(name)) ++expected;
                Require(count_ == expected);
            }
            bool Has(const wchar_t* key) const noexcept
            {
                for (std::size_t index = 0; index < count_; ++index) if (fields_[index].key == key) return true;
                return false;
            }
            const Value& Get(const wchar_t* key) const
            {
                for (std::size_t index = 0; index < count_; ++index) if (fields_[index].key == key) return fields_[index].value;
                throw Invalid{};
            }
            const std::wstring& Text(const wchar_t* key) const { const auto& value = Get(key); Require(value.type == Value::Type::String); return value.text; }
            std::int64_t Integer(const wchar_t* key) const { const auto& value = Get(key); Require(value.type == Value::Type::Integer); return value.integer; }
            bool Boolean(const wchar_t* key) const { const auto& value = Get(key); Require(value.type == Value::Type::Boolean); return value.boolean; }
        private:
            char Peek() const { Require(position_ < line_.size()); return line_[position_]; }
            void Take(char expected) { Require(Peek() == expected); ++position_; }
            void Space() { while (position_ < line_.size() && (line_[position_] == ' ' || line_[position_] == '\t')) ++position_; }
            std::uint32_t EscapeUnit()
            {
                Require(line_.size() - position_ >= 4);
                std::uint32_t value = 0;
                for (unsigned index = 0; index < 4; ++index) { const int digit = Hex(line_[position_++]); Require(digit >= 0); value = value * 16 + static_cast<unsigned>(digit); }
                return value;
            }
            std::wstring String()
            {
                Take('"');
                std::wstring out;
                while (true)
                {
                    const auto byte = static_cast<unsigned char>(Peek()); ++position_;
                    if (byte == '"') return out;
                    Require(byte >= 0x20);
                    if (byte == '\\')
                    {
                        const char escape = Peek(); ++position_;
                        switch (escape)
                        {
                        case '"': out.push_back(L'"'); break;
                        case '\\': out.push_back(L'\\'); break;
                        case '/': out.push_back(L'/'); break;
                        case 'b': out.push_back(L'\b'); break;
                        case 'f': out.push_back(L'\f'); break;
                        case 'n': out.push_back(L'\n'); break;
                        case 'r': out.push_back(L'\r'); break;
                        case 't': out.push_back(L'\t'); break;
                        case 'u':
                        {
                            auto value = EscapeUnit();
                            if (value >= 0xd800 && value <= 0xdbff)
                            {
                                Take('\\'); Take('u'); const auto low = EscapeUnit();
                                Require(low >= 0xdc00 && low <= 0xdfff);
                                value = 0x10000 + ((value - 0xd800) << 10) + (low - 0xdc00);
                            }
                            CodePoint(out, value); break;
                        }
                        default: throw Invalid{};
                        }
                    }
                    else if (byte < 0x80) out.push_back(static_cast<wchar_t>(byte));
                    else
                    {
                        unsigned continuation = 0;
                        std::uint32_t value = 0, minimum = 0;
                        if (byte >= 0xc2 && byte <= 0xdf) { continuation = 1; value = byte & 0x1f; minimum = 0x80; }
                        else if (byte >= 0xe0 && byte <= 0xef) { continuation = 2; value = byte & 0x0f; minimum = 0x800; }
                        else if (byte >= 0xf0 && byte <= 0xf4) { continuation = 3; value = byte & 7; minimum = 0x10000; }
                        else throw Invalid{};
                        for (unsigned index = 0; index < continuation; ++index)
                        {
                            const auto next = static_cast<unsigned char>(Peek()); ++position_;
                            Require((next & 0xc0) == 0x80); value = (value << 6) | (next & 0x3f);
                        }
                        Require(value >= minimum); CodePoint(out, value);
                    }
                }
            }
            Value Primitive()
            {
                Value value;
                if (Peek() == '"') { value.text = String(); return value; }
                if (line_.substr(position_, 4) == "true") { position_ += 4; value.type = Value::Type::Boolean; value.boolean = true; return value; }
                if (line_.substr(position_, 5) == "false") { position_ += 5; value.type = Value::Type::Boolean; return value; }
                value.type = Value::Type::Integer;
                const auto start = position_;
                if (Peek() == '-') ++position_;
                const char first = Peek(); Require(Digit(first)); ++position_;
                if (first != '0') while (position_ < line_.size() && Digit(line_[position_])) ++position_;
                const auto parsed = std::from_chars(line_.data() + start, line_.data() + position_, value.integer);
                Require(parsed.ec == std::errc{} && parsed.ptr == line_.data() + position_);
                return value;
            }
            std::string_view line_;
            std::size_t position_ = 0, count_ = 0;
            std::array<Field, 13> fields_{};
        };
        bool Resolution(std::uint32_t height) noexcept { return height == 360 || height == 480 || height == 720 || height == 1080 || height == 1440 || height == 2160; }
        std::uint32_t U32(std::int64_t value) { Require(value > 0 && value <= (std::numeric_limits<std::uint32_t>::max)()); return static_cast<std::uint32_t>(value); }
        std::uint64_t Decimal(const std::wstring& text)
        {
            const auto ascii = Ascii(text); Require(!ascii.empty() && ascii.size() <= 20);
            for (char value : ascii) Require(Digit(value));
            std::uint64_t result = 0;
            const auto parsed = std::from_chars(ascii.data(), ascii.data() + ascii.size(), result);
            Require(parsed.ec == std::errc{} && parsed.ptr == ascii.data() + ascii.size() && result > 0);
            return result;
        }
        bool Separator(wchar_t value) noexcept { return value == L'\\' || value == L'/'; }
        bool Alpha(wchar_t value) noexcept { return (value >= L'A' && value <= L'Z') || (value >= L'a' && value <= L'z'); }
        bool Reserved(std::wstring_view component)
        {
            auto end = component.find(L'.');
            auto name = std::wstring(component.substr(0, end));
            for (auto& value : name) if (value >= L'a' && value <= L'z') value = static_cast<wchar_t>(value - (L'a' - L'A'));
            if (name == L"CON" || name == L"PRN" || name == L"AUX" || name == L"NUL" || name == L"CONIN$" || name == L"CONOUT$") return true;
            return name.size() == 4 && (name.substr(0, 3) == L"COM" || name.substr(0, 3) == L"LPT") &&
                ((name[3] >= L'1' && name[3] <= L'9') || name[3] == 0x00b9 || name[3] == 0x00b2 || name[3] == 0x00b3);
        }
        std::wstring_view PathBase(const std::wstring& path)
        {
            Require(path.size() >= 4 && path.size() <= 4096);
            std::size_t start = 0;
            unsigned minimumComponents = 1;
            if (Alpha(path[0]) && path[1] == L':' && Separator(path[2])) start = 3;
            else { Require(Separator(path[0]) && Separator(path[1]) && !Separator(path[2])); start = 2; minimumComponents = 3; }
            unsigned components = 0;
            std::wstring_view last;
            for (std::size_t index = start; index <= path.size(); ++index)
            {
                if (index < path.size() && !Separator(path[index]))
                {
                    const auto value = path[index];
                    Require(value >= 0x20 && value != L':' && value != L'<' && value != L'>' && value != L'"' && value != L'|' && value != L'?' && value != L'*');
                    continue;
                }
                Require(index > start);
                last = std::wstring_view(path).substr(start, index - start);
                Require(last != L"." && last != L".." && last.back() != L'.' && last.back() != L' ' && !Reserved(last));
                ++components; start = index + 1;
            }
            Require(components >= minimumComponents);
            return last;
        }
        std::wstring Wide(std::string_view value) { return std::wstring(value.begin(), value.end()); }
        template<class T> void Number(std::string& line, T value)
        {
            std::array<char, 32> text{};
            const auto result = std::to_chars(text.data(), text.data() + text.size(), value);
            Require(result.ec == std::errc{}); line.append(text.data(), result.ptr);
        }
        void AppendGuid(std::string& line, std::string_view guid)
        {
            Require(Guid(guid));
            for (char value : guid) line.push_back(value >= 'A' && value <= 'F' ? static_cast<char>(value + ('a' - 'A')) : value);
        }
        void Common(std::string& line, std::string_view session, std::int64_t request, const char* type)
        {
            Require(Guid(session));
            line = "{\"v\":2,\"session\":\""; AppendGuid(line, session);
            line += "\",\"request\":"; Number(line, request);
            line += ",\"type\":\""; line += type; line += '"';
        }
    }

    bool ParseCommand(std::string_view line, Command& result) noexcept
    {
        result = {};
        try
        {
            Require(!line.empty() && line.size() <= MaximumLineBytes && line.find('\n') == std::string_view::npos);
            if (line.back() == '\r') line.remove_suffix(1);
            Require(line.find('\r') == std::string_view::npos);
            Parser parser(line); parser.Parse();
            Require(parser.Integer(L"v") == 2);
            Command command;
            command.session = GuidText(parser.Text(L"session"));
            command.request = parser.Integer(L"request"); Require(command.request > 0);
            const auto& kind = parser.Text(L"command");
            if (kind == L"config")
            {
                parser.Keys({ L"v", L"session", L"request", L"command", L"durationSeconds", L"height", L"frameRate", L"quality", L"gameAudio", L"spoolDirectory", L"losslessVideo" },
                    { L"borderlessAllowed", L"systemAudio" });
                if (parser.Has(L"borderlessAllowed")) command.borderlessAllowed = parser.Boolean(L"borderlessAllowed");
                if (parser.Has(L"systemAudio")) command.systemAudio = parser.Boolean(L"systemAudio");
                command.kind = CommandKind::Config;
                command.durationSeconds = U32(parser.Integer(L"durationSeconds"));
                command.height = U32(parser.Integer(L"height")); command.frameRate = U32(parser.Integer(L"frameRate"));
                command.quality = U32(parser.Integer(L"quality")); command.gameAudio = parser.Boolean(L"gameAudio");
                command.losslessVideo = parser.Boolean(L"losslessVideo");
                Require(command.durationSeconds >= 30 && command.durationSeconds <= 300 && command.durationSeconds % 30 == 0 &&
                    Resolution(command.height) && (command.frameRate == 30 || command.frameRate == 60) && command.quality >= 10 && command.quality <= 100 && command.gameAudio);
                command.spoolDirectory = parser.Text(L"spoolDirectory");
                Require(PathBase(command.spoolDirectory) == Wide(".wisp-recorder-" + command.session));
            }
            else if (kind == L"start" || kind == L"resume")
            {
                parser.Keys({ L"v", L"session", L"request", L"command", L"processId", L"window", L"creationFileTime" });
                command.kind = kind == L"start" ? CommandKind::Start : CommandKind::Resume;
                command.processId = U32(parser.Integer(L"processId"));
                command.window = Decimal(parser.Text(L"window")); command.creationFileTime = Decimal(parser.Text(L"creationFileTime"));
            }
            else if (kind == L"save")
            {
                parser.Keys({ L"v", L"session", L"request", L"command", L"clipId", L"destination" });
                command.kind = CommandKind::Save; command.clipId = GuidText(parser.Text(L"clipId"));
                command.destination = parser.Text(L"destination");
                Require(PathBase(command.destination) == Wide(command.clipId + ".mp4"));
            }
            else if (kind == L"stop" || kind == L"pause")
            {
                parser.Keys({ L"v", L"session", L"request", L"command" });
                command.kind = kind == L"stop" ? CommandKind::Stop : CommandKind::Pause;
            }
            else throw Invalid{};
            result = std::move(command); return true;
        }
        catch (...) { result = {}; return false; }
    }

    const char* Name(State state) noexcept
    {
        switch (state)
        {
        case State::Waiting: return "waiting"; case State::Buffering: return "buffering";
        case State::Saving: return "saving"; case State::Stopped: return "stopped"; case State::Error: return "error";
        case State::Paused: return "paused"; case State::Reconnecting: return "reconnecting";
        default: return nullptr;
        }
    }
    const char* Name(Reason reason) noexcept
    {
        switch (reason)
        {
        case Reason::None: return "none"; case Reason::WaitingForGame: return "waiting_for_game";
        case Reason::TargetExited: return "target_exited"; case Reason::TargetChanged: return "target_changed";
        case Reason::WindowClosed: return "window_closed"; case Reason::WindowMinimized: return "window_minimized";
        case Reason::WindowResized: return "window_resized"; case Reason::FocusLost: return "focus_lost";
        case Reason::FullscreenRequired: return "fullscreen_required";
        case Reason::UnsupportedOs: return "unsupported_os"; case Reason::UnsupportedGpu: return "unsupported_gpu";
        case Reason::LosslessEncoderUnsupported: return "lossless_encoder_unsupported";
        case Reason::UnsupportedFormat: return "unsupported_format"; case Reason::CaptureFailed: return "capture_failed";
        case Reason::EncoderFailed: return "encoder_failed"; case Reason::AudioFailed: return "audio_failed";
        case Reason::AudioCaptureFailed: return "audio_capture_failed"; case Reason::AudioUnavailable: return "audio_unavailable";
        case Reason::CaptureStale: return "capture_stale"; case Reason::CaptureReconnecting: return "capture_reconnecting";
        case Reason::EncoderReconnecting: return "encoder_reconnecting"; case Reason::AudioReconnecting: return "audio_reconnecting";
        case Reason::SchedulerLate: return "scheduler_late";
        case Reason::BufferFull: return "buffer_full"; case Reason::NoKeyframe: return "no_keyframe";
        case Reason::NotReady: return "not_ready"; case Reason::SaveInProgress: return "save_in_progress";
        case Reason::StorageFailed: return "storage_failed"; case Reason::MuxFailed: return "mux_failed";
        case Reason::LosslessStorageLow: return "lossless_storage_low";
        case Reason::ProtocolError: return "protocol_error"; case Reason::Cancelled: return "cancelled";
        case Reason::CleanupFailed: return "cleanup_failed";
        case Reason::Stopped: return "stopped"; case Reason::ParentClosed: return "parent_closed";
        default: return nullptr;
        }
    }
    bool IsRecoverable(Reason reason) noexcept
    {
        switch (reason)
        {
        case Reason::TargetExited: case Reason::TargetChanged: case Reason::WindowClosed:
        case Reason::WindowMinimized: case Reason::WindowResized: case Reason::FocusLost: case Reason::FullscreenRequired:
        case Reason::CaptureReconnecting: case Reason::EncoderReconnecting:
        case Reason::AudioReconnecting: case Reason::SchedulerLate: return true;
        default: return false;
        }
    }

    bool SerializeResult(const Result& result, std::string& line) noexcept
    {
        line.clear();
        try
        {
            const auto* reason = Name(result.reason); Require(reason != nullptr && result.request > 0);
            Require(result.ok || (!result.media && result.reason != Reason::None));
            if (result.ok && !result.media) Require(result.reason == Reason::None);
            if (result.media)
            {
                const auto& media = *result.media;
                Require(!media.sizeLimited || media.losslessVideo);
                Require(result.ok && Guid(media.clipId) && media.fileBytes > 0 && media.fileBytes <= MaximumMediaBytes &&
                    Resolution(media.height) && media.width == (media.height == 480 ? 854u : media.height * 16 / 9) &&
                    (media.frameRate == 30 || media.frameRate == 60) && media.start100ns >= 0 && media.end100ns > media.start100ns &&
                    media.end100ns - media.start100ns <= 3000000000ll && result.reason == (media.hasAudio ? Reason::None : Reason::AudioUnavailable));
            }
            Common(line, result.session, result.request, "result");
            line += result.ok ? ",\"ok\":true,\"reason\":\"" : ",\"ok\":false,\"reason\":\"";
            line += reason; line += '"';
            if (result.media)
            {
                const auto& media = *result.media;
                line += ",\"clipId\":\""; AppendGuid(line, media.clipId); line += "\",\"fileBytes\":"; Number(line, media.fileBytes);
                line += ",\"width\":"; Number(line, media.width); line += ",\"height\":"; Number(line, media.height);
                line += ",\"frameRate\":"; Number(line, media.frameRate); line += ",\"start100ns\":"; Number(line, media.start100ns);
                line += ",\"end100ns\":"; Number(line, media.end100ns);
                line += media.hasAudio ? ",\"hasAudio\":true" : ",\"hasAudio\":false";
                line += media.losslessVideo ? ",\"losslessVideo\":true" : ",\"losslessVideo\":false";
                line += media.sizeLimited ? ",\"sizeLimited\":true" : ",\"sizeLimited\":false";
            }
            line += '}'; Require(line.size() <= MaximumLineBytes); return true;
        }
        catch (...) { line.clear(); return false; }
    }
    bool SerializeState(std::string_view session, State state, Reason reason, std::string& line,
        const LosslessBuffer* losslessBuffer, bool bufferReady) noexcept
    {
        line.clear();
        try
        {
            const auto* stateName = Name(state); const auto* reasonName = Name(reason);
            Require(stateName != nullptr && reasonName != nullptr);
            Require(!bufferReady || state == State::Paused);
            if (losslessBuffer)
                Require((state == State::Buffering || (state == State::Paused && bufferReady)) && losslessBuffer->duration100ns > 0 &&
                    losslessBuffer->duration100ns <= 3000000000ll && losslessBuffer->payloadBytes > 0 &&
                    losslessBuffer->payloadBytes <= losslessBuffer->budgetBytes &&
                    losslessBuffer->budgetBytes <= 12ull * 1024 * 1024 * 1024 + 16ull * 1024 * 1024);
            Common(line, session, 0, "state");
            line += ",\"state\":\""; line += stateName; line += "\",\"reason\":\""; line += reasonName; line += '"';
            if (state == State::Paused) line += bufferReady ? ",\"bufferReady\":true" : ",\"bufferReady\":false";
            if (losslessBuffer)
            {
                line += ",\"losslessBuffer\":{\"duration100ns\":"; Number(line, losslessBuffer->duration100ns);
                line += ",\"payloadBytes\":"; Number(line, losslessBuffer->payloadBytes);
                line += ",\"budgetBytes\":"; Number(line, losslessBuffer->budgetBytes);
                line += losslessBuffer->sizeLimited ? ",\"sizeLimited\":true}" : ",\"sizeLimited\":false}";
            }
            line += '}';
            Require(line.size() <= MaximumLineBytes); return true;
        }
        catch (...) { line.clear(); return false; }
    }
}
