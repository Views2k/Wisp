#include "SyntheticTiming.h"
#include <windows.h>
#include <array>
#include <atomic>
#include <limits>

#if !defined(WISP_SYNTHETIC_HOST_FIXTURE)
#error Diagnostic instrumentation must not be linked into the product.
#endif

namespace recorder::synthetic_timing
{
    namespace
    {
        constexpr std::array<const char*, static_cast<unsigned>(Stage::Count)> Names{
            "host_tick", "capture_target", "video_pump", "video_submit", "capture_fill", "process_audio", "initialize_spool",
            "readiness", "save_begin", "append_video", "append_audio", "seal_video", "seal_audio", "flush_file_buffers",
            "write_file", "create_spool_file", "retain_snapshot" };
        struct Aggregate { std::uint64_t count = 0, total = 0, maximum = 0, bytes = 0, maximumBytes = 0; };
        struct Event
        {
            Stage stage = Stage::Tick;
            std::uint64_t sequence = 0, begin = 0, elapsed = 0, bytes = 0;
            std::int64_t mediaTime100ns = -1;
        };
        constexpr std::size_t SlowCapacity = 64;
        std::array<Aggregate, Names.size()> Totals{};
        std::array<Event, SlowCapacity> Slow{};
        std::uint64_t Frequency = 0, Origin = 0, Sequence = 0, SlowCount = 0;
        std::atomic<DWORD> Owner{ 0 };
        std::atomic<bool> Frozen{ false }, ClockFailed{ false };
        std::atomic<unsigned> OtherThreadObservations{ 0 };
        std::uint64_t Now() noexcept
        {
            LARGE_INTEGER value{};
            if (!QueryPerformanceCounter(&value) || value.QuadPart <= 0)
            { ClockFailed.store(true); return 0; }
            return static_cast<std::uint64_t>(value.QuadPart);
        }
        std::uint64_t Time100ns(std::uint64_t ticks) noexcept
        { return Frequency ? ticks / Frequency * 10000000 + ticks % Frequency * 10000000 / Frequency : 0; }
        void Add(std::uint64_t& total, std::uint64_t value) noexcept
        {
            const auto maximum = (std::numeric_limits<std::uint64_t>::max)();
            total = value > maximum - total ? maximum : total + value;
        }
        bool IsOwner() noexcept
        {
            const auto current = GetCurrentThreadId(); DWORD expected = 0;
            (void)Owner.compare_exchange_strong(expected, current);
            if (Owner.load() == current) return true;
            OtherThreadObservations.fetch_add(1); return false;
        }
    }
    bool Enabled() noexcept
    {
#if defined(WISP_SYNTHETIC_HOST_TIMING)
        return true;
#else
        return false;
#endif
    }
    void Initialize() noexcept
    {
        if (!Enabled()) return;
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) { ClockFailed.store(true); return; }
        Frequency = static_cast<std::uint64_t>(frequency.QuadPart); Origin = Now();
    }
    Scope::Scope(Stage stage, std::uint64_t bytes, std::int64_t mediaTime100ns) noexcept :
        stage_(stage), bytes_(bytes), mediaTime100ns_(mediaTime100ns)
    {
        if (!Enabled() || !Frequency || Frozen.load() || !IsOwner() || static_cast<unsigned>(stage) >= Names.size()) return;
        began_ = Now();
    }
    Scope::~Scope()
    {
        if (!began_ || Frozen.load()) return;
        const auto end = Now();
        if (end < began_) { ClockFailed.store(true); return; }
        const auto elapsed = end - began_;
        auto& aggregate = Totals[static_cast<unsigned>(stage_)];
        Add(aggregate.count, 1); Add(aggregate.total, elapsed); Add(aggregate.bytes, bytes_);
        if (elapsed > aggregate.maximum) aggregate.maximum = elapsed;
        if (bytes_ > aggregate.maximumBytes) aggregate.maximumBytes = bytes_;
        ++Sequence;
        if (Time100ns(elapsed) >= 10000) // Keep only >=1ms operations, in completion order.
        {
            Slow[SlowCount % SlowCapacity] = { stage_, Sequence, began_, elapsed, bytes_, mediaTime100ns_ };
            ++SlowCount;
        }
    }
    void Freeze() noexcept
    {
        if (Enabled() && IsOwner()) Frozen.store(true);
    }
    std::string Report()
    {
        std::string result = "{\"mode\":\"synthetic_host_timing\",\"enabled\":" + std::string(Enabled() ? "true" : "false") +
            ",\"frozenAtFirstFailure\":" + (Frozen.load() ? "true" : "false") +
            ",\"clockFailed\":" + (ClockFailed.load() ? "true" : "false") +
            ",\"otherThreadObservationsIgnored\":" + std::to_string(OtherThreadObservations.load()) +
            ",\"stages\":[";
        for (std::size_t index = 0; index < Names.size(); ++index)
        {
            const auto& value = Totals[index];
            if (index) result += ',';
            result += "{\"stage\":\"" + std::string(Names[index]) + "\",\"count\":" + std::to_string(value.count) +
                ",\"total100ns\":" + std::to_string(Time100ns(value.total)) + ",\"maximum100ns\":" + std::to_string(Time100ns(value.maximum)) +
                ",\"totalBytes\":" + std::to_string(value.bytes) + ",\"maximumBytes\":" + std::to_string(value.maximumBytes) + '}';
        }
        result += "],\"slowEvents\":[";
        const auto first = SlowCount > SlowCapacity ? SlowCount - SlowCapacity : 0;
        for (auto index = first; index < SlowCount; ++index)
        {
            const auto& value = Slow[index % SlowCapacity];
            if (index != first) result += ',';
            result += "{\"sequence\":" + std::to_string(value.sequence) + ",\"stage\":\"" + Names[static_cast<unsigned>(value.stage)] +
                "\",\"begin100ns\":" + std::to_string(value.begin >= Origin ? Time100ns(value.begin - Origin) : 0) +
                ",\"elapsed100ns\":" + std::to_string(Time100ns(value.elapsed)) + ",\"bytes\":" + std::to_string(value.bytes) +
                ",\"mediaTime100ns\":" + std::to_string(value.mediaTime100ns) + '}';
        }
        result += "],\"scope\":\"Completed scopes only; nested stage times overlap and must not be summed. Fixed stages and scalar metadata only.\"}\n";
        return result;
    }
}
