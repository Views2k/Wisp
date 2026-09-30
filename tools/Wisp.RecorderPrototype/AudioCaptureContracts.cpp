#include "ProcessAudioCapture.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <iostream>
#include <limits>
#include <memory>
#include <utility>

namespace
{
    using namespace recorder::audio;
    using Microsoft::WRL::ComPtr;
    struct Failure { const char* contract; };
    unsigned checks = 0;
    void Check(bool value, const char* contract)
    {
        if (!value) throw Failure{ contract };
        ++checks;
    }

    class FakeUnknown final : public IUnknown
    {
    public:
        explicit FakeUnknown(unsigned& destroyed) : destroyed_(destroyed) {}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER;
            *result = nullptr;
            if (iid != __uuidof(IUnknown)) return E_NOINTERFACE;
            *result = static_cast<IUnknown*>(this);
            AddRef();
            return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
        ULONG STDMETHODCALLTYPE Release() override
        {
            const ULONG references = --references_;
            if (!references) delete this;
            return references;
        }
    private:
        ~FakeUnknown() { ++destroyed_; }
        std::atomic<ULONG> references_{ 1 };
        unsigned& destroyed_;
    };

    ComPtr<IUnknown> Fake(unsigned& destroyed)
    {
        ComPtr<IUnknown> result;
        result.Attach(new FakeUnknown(destroyed));
        return result;
    }

    void FormatContracts()
    {
        const auto preferred = PreferredFormat();
        Check(IsPreferredFormat(preferred), "preferred_pcm_format");
        Check(preferred.nAvgBytesPerSec == 192000 && preferred.nBlockAlign == 4,
            "stereo_pcm16_byte_rate");
        for (unsigned field = 0; field < 7; ++field)
        {
            auto format = preferred;
            switch (field)
            {
            case 0: format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT; break;
            case 1: format.nChannels = 1; break;
            case 2: format.nSamplesPerSec = 44100; break;
            case 3: format.wBitsPerSample = 32; break;
            case 4: format.nBlockAlign = 8; break;
            case 5: format.nAvgBytesPerSec = 96000; break;
            default: format.cbSize = 22; break;
            }
            Check(!IsPreferredFormat(format), "changed_format_rejected");
        }
    }

    void QueueBoundsAndOwnership()
    {
        PacketQueue queue({ 4, 2, 16 });
        Check(queue.ValidLimits() && queue.BeginSource() && !queue.BeginSource(), "single_source_claim");
        std::array<BYTE, 8> data{ 1, 2, 3, 4, 5, 6, 7, 8 };
        Check(queue.Push(data.data(), 2, 0, 0, 1000) == QueueResult::Accepted, "first_pcm_packet");
        data.fill(0);
        Check(queue.Push(data.data(), 2, 0, 2, 2000) == QueueResult::Accepted, "exact_queue_byte_limit");
        Check(queue.Push(data.data(), 1, 0, 4, 3000) == QueueResult::QueueLimit, "no_old_packet_overwrite");
        Check(queue.Snapshot().queuedBytes == 16 && queue.Snapshot().acceptedPackets == 2,
            "refusal_does_not_change_queue");
        std::unique_ptr<const Packet> first;
        Check(queue.TryPop(first) && first->frames == 2 && first->samples.size() == 4,
            "consumer_receives_complete_packet");
        const auto* bytes = reinterpret_cast<const BYTE*>(first->samples.data());
        Check(bytes[0] == 1 && bytes[7] == 8 && first->qpc100ns == 1000 && first->timestampValid,
            "copy_ownership_and_original_100ns_time");
        Check(queue.Snapshot().queuedBytes == 8, "consumer_bytes_not_queue_budget");
        Check(queue.Push(data.data(), 2, 0, 4, 3000) == QueueResult::Accepted,
            "pop_releases_only_queue_capacity");
        Check(bytes[0] == 1, "later_push_cannot_change_consumer_packet");
        queue.Close();
        Check(queue.Push(data.data(), 1, 0, 6, 4000) == QueueResult::Closed, "closed_queue_refuses_new_data");
        Check(queue.TryPop(first) && queue.TryPop(first) && !queue.TryPop(first), "closed_queue_can_be_drained");
        Check(queue.Snapshot().queuedBytes == 0 && queue.Snapshot().peakQueuedBytes == 16,
            "queue_accounting_returns_to_zero");

        PacketQueue count({ 4, 1, 32 });
        Check(count.Push(data.data(), 1, 0, 0, 1000) == QueueResult::Accepted &&
            count.Push(data.data(), 1, 0, 1, 2000) == QueueResult::QueueLimit, "packet_count_limit");
        PacketQueue invalid({ 0, 1, 4 });
        Check(!invalid.ValidLimits() && invalid.Push(data.data(), 1, 0, 0, 1000) == QueueResult::InvalidLimits,
            "invalid_queue_limit");
        PacketQueue oversized({ 4, 2, 8 });
        Check(oversized.Push(data.data(), 3, AUDCLNT_BUFFERFLAGS_SILENT, 0, 1000) == QueueResult::QueueLimit,
            "single_packet_byte_cap");
    }

    void FlagsAndTiming()
    {
        PacketQueue queue({ 4, 16, 256 });
        std::array<BYTE, 16> data{};
        Check(queue.Push(nullptr, 4, AUDCLNT_BUFFERFLAGS_SILENT, 0, 1000) == QueueResult::Accepted,
            "silent_null_buffer_valid");
        std::unique_ptr<const Packet> packet;
        Check(queue.TryPop(packet) && packet->silent && packet->samples.size() == 8 &&
            std::all_of(packet->samples.begin(), packet->samples.end(), [](auto value) { return value == 0; }),
            "silence_zero_filled");
        Check(queue.Push(data.data(), 4, AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY, 4, 2000) == QueueResult::Accepted &&
            queue.TryPop(packet) && packet->discontinuity, "source_discontinuity_preserved");
        Check(queue.Push(data.data(), 4, 0, 12, 3000) == QueueResult::Accepted && queue.TryPop(packet) &&
            !packet->discontinuity, "device_position_does_not_invent_discontinuity");
        Check(queue.Push(data.data(), 4, AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR,
            (std::numeric_limits<std::uint64_t>::max)(), (std::numeric_limits<std::uint64_t>::max)()) == QueueResult::Accepted,
            "invalid_timestamp_fields_not_interpreted");
        Check(queue.TryPop(packet) && packet->timestampError && !packet->timestampValid &&
            packet->qpc100ns == 0 && packet->devicePositionFrames == 0 && packet->discontinuity,
            "invalid_timestamp_never_fabricated");
        Check(queue.Push(data.data(), 4, 0, 20, 5000) == QueueResult::Accepted && queue.TryPop(packet) &&
            packet->discontinuity, "recovery_after_bad_timestamp_marks_boundary");
        Check(queue.Push(data.data(), 4, 0, 24, 6000) == QueueResult::Accepted && queue.TryPop(packet) &&
            !packet->discontinuity, "continuous_packet_after_recovery");
        Check(queue.Push(data.data(), 4, 0, 28, 6000) == QueueResult::InvalidTimestamp,
            "duplicate_qpc_rejected");
        Check(queue.Push(data.data(), 4, 0, 28, 5999) == QueueResult::InvalidTimestamp,
            "backward_qpc_rejected");
        Check(queue.Push(data.data(), 4, 0, 28, 0) == QueueResult::InvalidTimestamp, "zero_valid_qpc_rejected");
        Check(queue.Push(data.data(), 4, 0, (std::numeric_limits<std::uint64_t>::max)() - 2, 7000) == QueueResult::InvalidTimestamp,
            "device_position_overflow");
        Check(queue.Push(data.data(), 4, 0, 28, static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)())) ==
            QueueResult::InvalidTimestamp, "timestamp_end_overflow");
        Check(queue.Push(nullptr, 4, 0, 28, 7000) == QueueResult::InvalidPacket, "non_silent_null_refused");
        Check(queue.Push(data.data(), 0, 0, 28, 7000) == QueueResult::InvalidPacket, "empty_packet_refused");
        Check(queue.Push(data.data(), 5, 0, 28, 7000) == QueueResult::PacketLimit, "oversized_packet_before_copy");
        Check(queue.Push(data.data(), 4, 8, 28, 7000) == QueueResult::InvalidPacket, "unknown_audio_flags_refused");
        const auto stats = queue.Snapshot();
        Check(stats.silentPackets == 1 && stats.timestampErrorPackets == 1 && stats.discontinuityPackets == 3,
            "flag_counts_are_explicit");
        Check(stats.nativeDiscontinuityPackets == 1, "native_flag_not_inferred_device_gap");
        PacketQueue virtualSource({ 4, 2, 64 });
        Check(virtualSource.Push(data.data(), 4, 0, 0, 1000) == QueueResult::Accepted &&
            virtualSource.Push(data.data(), 4, 0, 0, 2000) == QueueResult::Accepted &&
            virtualSource.Snapshot().discontinuityPackets == 0,
            "virtual_zero_device_positions_with_valid_qpc_are_not_gaps");
    }

    void ActivationHandoff()
    {
        unsigned destroyed = 0;
        detail::ActivationMailbox mailbox;
        HRESULT hr = E_PENDING;
        ComPtr<IUnknown> output;
        Check(!mailbox.Take(hr, output) && !mailbox.Completed(), "cannot_take_before_completion");
        Check(mailbox.Publish(S_OK, Fake(destroyed)) && mailbox.Completed(), "publish_activation_result");
        Check(destroyed == 0 && mailbox.Take(hr, output) && hr == S_OK && output,
            "activation_ownership_transfers_once");
        Check(!mailbox.Take(hr, output), "result_cannot_be_taken_twice");
        Check(!mailbox.Publish(S_OK, Fake(destroyed)) && destroyed == 1, "duplicate_completion_releases_payload");
        output.Reset();
        Check(destroyed == 2, "transferred_result_final_release");

        auto pending = std::make_shared<detail::ActivationMailbox>();
        auto callbackOwner = pending;
        std::weak_ptr<detail::ActivationMailbox> lifetime = pending;
        pending->Abandon();
        pending.reset();
        Check(!lifetime.expired(), "callback_state_outlives_worker_timeout");
        Check(!callbackOwner->Publish(S_OK, Fake(destroyed)) && destroyed == 3,
            "late_completion_discards_interface");
        Check(callbackOwner->Completed() && !callbackOwner->Take(hr, output) && !output,
            "abandoned_result_cannot_start_audio");
        callbackOwner.reset();
        Check(lifetime.expired(), "late_callback_state_final_release");

        detail::ActivationMailbox race;
        Check(race.Publish(S_OK, Fake(destroyed)), "completion_before_timeout");
        race.Abandon();
        Check(destroyed == 4 && !race.Take(hr, output), "timeout_discards_already_published_result");
        detail::ActivationMailbox failed;
        Check(failed.Publish(E_ACCESSDENIED, {}) && failed.Take(hr, output) && hr == E_ACCESSDENIED && !output,
            "failed_activation_preserves_hresult_without_fallback");
    }

    void ContinuousOptionsAndCounters()
    {
        Options options;
        Check(ValidateOptions(options) && options.mode == CaptureMode::TimedFixture &&
            options.maximumCaptureMs == 10000, "finite_fixture_default_unchanged");
        options.maximumCaptureMs = 60000;
        Check(ValidateOptions(options), "finite_duration_upper_boundary");
        options.maximumCaptureMs = 60001;
        Check(!ValidateOptions(options), "finite_duration_bound_preserved");
        options.maximumCaptureMs = 0;
        Check(!ValidateOptions(options), "zero_duration_not_implicit_continuous");
        options.mode = CaptureMode::UntilStopped;
        Check(ValidateOptions(options), "explicit_continuous_capture");
        options.maximumCaptureMs = 1;
        Check(!ValidateOptions(options), "continuous_rejects_conflicting_duration");
        options.maximumCaptureMs = 0;
        options.activationTimeoutMs = 0;
        Check(!ValidateOptions(options), "continuous_activation_still_bounded_below");
        options.activationTimeoutMs = 30001;
        Check(!ValidateOptions(options), "continuous_activation_still_bounded_above");
        options.activationTimeoutMs = 30000;
        Check(ValidateOptions(options), "continuous_activation_upper_boundary");
        options.mode = static_cast<CaptureMode>(99);
        Check(!ValidateOptions(options), "unknown_capture_mode_refused");

        constexpr auto maximum = (std::numeric_limits<std::uint64_t>::max)();
        QueueSnapshot stats;
        stats.acceptedFrames = 6ull * 60 * 60 * SampleRate;
        Check(detail::CountersCanAdvance(stats, 4800, 0, false), "six_hour_queue_counters_supported");
        stats.acceptedFrames = maximum - 4800;
        Check(detail::CountersCanAdvance(stats, 4800, 0, false), "exact_frame_counter_boundary");
        Check(!detail::CountersCanAdvance(stats, 4801, 0, false), "frame_counter_overflow_refused");
        stats = {}; stats.acceptedPackets = maximum;
        Check(!detail::CountersCanAdvance(stats, 1, 0, false), "packet_counter_overflow_refused");
        stats = {}; stats.silentPackets = maximum;
        Check(!detail::CountersCanAdvance(stats, 1, AUDCLNT_BUFFERFLAGS_SILENT, false) &&
            detail::CountersCanAdvance(stats, 1, 0, false), "silent_counter_only_advances_when_set");
        stats = {}; stats.discontinuityPackets = maximum;
        Check(!detail::CountersCanAdvance(stats, 1, 0, true) &&
            detail::CountersCanAdvance(stats, 1, 0, false), "recovery_discontinuity_counter_bound");
        stats = {}; stats.nativeDiscontinuityPackets = maximum;
        Check(!detail::CountersCanAdvance(stats, 1, AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY, true) &&
            detail::CountersCanAdvance(stats, 1, 0, true), "native_discontinuity_counter_bound");
        stats = {}; stats.timestampErrorPackets = maximum;
        Check(!detail::CountersCanAdvance(stats, 1, AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR, true) &&
            detail::CountersCanAdvance(stats, 1, 0, false), "timestamp_error_counter_bound");
    }
}

int main()
{
    try
    {
        FormatContracts();
        QueueBoundsAndOwnership();
        FlagsAndTiming();
        ActivationHandoff();
        ContinuousOptionsAndCounters();
        std::cout << "{\"mode\":\"process_audio_cpu_contracts\",\"passed\":" << checks
            << ",\"audioActivated\":false,\"captureUsed\":false,\"playbackUsed\":false}\n";
        return 0;
    }
    catch (const Failure& error)
    {
        std::cout << "{\"passed\":false,\"reason\":\"" << error.contract << "\"}\n";
        return 1;
    }
    catch (...)
    {
        std::cout << "{\"passed\":false,\"reason\":\"unexpected_audio_contract_exception\"}\n";
        return 1;
    }
}
