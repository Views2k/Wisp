#include "EncodedClipBuffer.h"

#include <array>
#include <iostream>
#include <limits>
#include <memory>
#include <type_traits>
#include <utility>
#include <vector>

namespace
{
    using namespace recorder::buffer;
    struct Failure { const char* contract; };
    unsigned checks = 0;
    void Check(bool value, const char* contract)
    {
        if (!value) throw Failure{ contract };
        ++checks;
    }

    Result Begin(EncodedClipBuffer& buffer, std::uint64_t epoch = 1, std::uint8_t value = 9)
    {
        const std::array<std::uint8_t, 2> header{ value, value };
        return buffer.BeginEpoch(epoch, header.data(), header.size());
    }

    Result Push(EncodedClipBuffer& buffer, MediaTime time, bool key,
        std::size_t bytes = 4, std::uint64_t epoch = 1, MediaTime duration = 10, std::uint8_t value = 7)
    {
        const std::vector<std::uint8_t> data(bytes, value);
        return buffer.Append(epoch, time, duration, key, data.data(), data.size());
    }

    void DurationAndGops()
    {
        EncodedClipBuffer buffer({ 30, 100 });
        Check(Begin(buffer) == Result::Accepted, "begin_epoch");
        Check(Push(buffer, 0, false) == Result::KeyframeRequired, "initial_keyframe_required");
        Check(Push(buffer, 0, true) == Result::Accepted, "initial_keyframe");
        Check(Push(buffer, 10, false) == Result::Accepted, "dependent_frame");
        Check(Push(buffer, 20, true) == Result::Accepted, "second_gop");
        Check(buffer.RollingDuration100ns() == 30, "duration_exact_limit");
        Check(Push(buffer, 30, false) == Result::Accepted, "evict_complete_gop");
        Check(buffer.RollingPacketCount() == 2 && buffer.RollingDuration100ns() == 20,
            "no_partial_gop_retention");
        Check(buffer.Retain() == Result::Accepted, "retain_shorter_decodable_window");
        auto clip = buffer.Retained();
        Check(clip->start100ns == 20 && clip->end100ns == 40 && clip->packets.front().cleanPoint,
            "actual_snapshot_bounds");
        Check(buffer.AccountedBytes() == 10, "snapshot_shares_charged_payloads");
    }

    void LongGopAndPacketLimits()
    {
        EncodedClipBuffer duration({ 20, 100 });
        Check(Begin(duration) == Result::Accepted, "duration_begin");
        Check(Push(duration, 0, true) == Result::Accepted && Push(duration, 10, false) == Result::Accepted,
            "long_gop_prefix");
        Check(Push(duration, 20, false) == Result::DurationLimit && duration.AwaitingKeyframe(),
            "long_gop_refused");
        Check(Push(duration, 30, false) == Result::KeyframeRequired, "refused_gop_cannot_resume_midstream");
        Check(Push(duration, 40, true) == Result::Accepted, "resume_on_clean_point");
        Check(Push(duration, 50, true, 4, 1, 21) == Result::DurationLimit, "single_duration_oversize");

        EncodedClipBuffer bytes({ 100, 10 });
        Check(Begin(bytes) == Result::Accepted && Push(bytes, 0, true) == Result::Accepted &&
            Push(bytes, 10, false) == Result::Accepted, "byte_exact_limit");
        Check(bytes.AccountedBytes() == 10, "configuration_included_in_budget");
        Check(Push(bytes, 20, false) == Result::ByteLimit && bytes.AccountedBytes() == 2,
            "byte_limited_long_gop_refused");
        Check(Push(bytes, 30, true, 11) == Result::ByteLimit && bytes.AccountedBytes() == 2,
            "oversize_packet_refused_before_copy");
        Check(Push(bytes, 40, true, 9) == Result::ByteLimit, "header_plus_packet_limit");
        Check(Push(bytes, 50, true, 8) == Result::Accepted, "header_plus_packet_exact_limit");
        Check(Push(bytes, 60, true, 8) == Result::Accepted && bytes.AccountedBytes() == 10,
            "old_gop_replaced_on_byte_limit");
    }

    void RetainedBudgetAndOwnership()
    {
        EncodedClipBuffer buffer({ 20, 14 });
        Check(Begin(buffer) == Result::Accepted && Push(buffer, 0, true) == Result::Accepted &&
            Push(buffer, 10, false) == Result::Accepted, "retention_prefix");
        Check(buffer.Retain() == Result::Accepted, "first_save");
        auto clip = buffer.Retained();
        Check(buffer.Retain() == Result::RetainedClipExists && buffer.Retained() == clip,
            "repeat_save_does_not_overwrite");
        Check(Push(buffer, 20, true) == Result::Accepted && buffer.AccountedBytes() == 14,
            "evicted_but_retained_bytes_stay_charged");
        Check(Push(buffer, 30, false) == Result::ByteLimit && buffer.AccountedBytes() == 10,
            "retained_data_prevents_budget_escape");
        Check(clip->start100ns == 0 && clip->end100ns == 20 && clip->packets.size() == 2,
            "refusal_preserves_retained_footage");
        buffer.ReleaseRetained();
        Check(buffer.Retain() == Result::RetainedClipExists && buffer.AccountedBytes() == 10,
            "external_snapshot_owner_keeps_slot_and_budget");
        auto payload = clip->packets.front().payload;
        std::weak_ptr<const Payload> lifetime = payload;
        clip.reset();
        Check(buffer.AccountedBytes() == 6, "individual_payload_owner_stays_charged");
        Check(Push(buffer, 40, true, 8) == Result::Accepted && buffer.AccountedBytes() == 14,
            "remaining_external_payload_counts_with_new_rolling");
        payload.reset();
        Check(lifetime.expired() && buffer.AccountedBytes() == 10, "last_payload_release_returns_budget");
        Check(buffer.Retain() == Result::Accepted && buffer.Retained()->start100ns == 40,
            "save_allowed_after_snapshot_release");
    }

    void EpochAndDiscontinuity()
    {
        EncodedClipBuffer buffer({ 100, 16 });
        Check(Begin(buffer) == Result::Accepted && Push(buffer, 0, true) == Result::Accepted,
            "epoch_one_packet");
        Check(buffer.Retain() == Result::Accepted, "epoch_one_retained");
        auto first = buffer.Retained();
        Check(Begin(buffer, 1, 3) == Result::EpochNotIncreasing, "config_change_needs_new_epoch");
        Check(Begin(buffer, 2, 3) == Result::Accepted && buffer.AwaitingKeyframe(), "new_epoch_clears_rolling");
        Check(buffer.AccountedBytes() == 8 && first->configuration.h264SequenceHeader->Bytes()[0] == 9,
            "retained_configuration_unchanged_and_charged");
        Check(Push(buffer, 0, true, 4, 1) == Result::EpochMismatch, "stale_epoch_refused");
        Check(Push(buffer, 0, false, 4, 2) == Result::KeyframeRequired, "new_epoch_keyframe_required");
        Check(Push(buffer, 0, true, 4, 2) == Result::Accepted, "new_epoch_may_reset_clock");
        buffer.ReleaseRetained();
        first.reset();
        Check(buffer.AccountedBytes() == 6, "old_epoch_released");
        Check(Push(buffer, 20, false, 4, 2) == Result::Discontinuity && buffer.AwaitingKeyframe(),
            "unannounced_gap_rejected");
        Check(Push(buffer, 30, false, 4, 2) == Result::KeyframeRequired, "gap_cannot_keep_dependent_packet");
        Check(Push(buffer, 40, true, 4, 2) == Result::Accepted, "gap_clean_restart");
        buffer.MarkDiscontinuity();
        Check(Push(buffer, 80, true, 4, 2) == Result::Accepted && buffer.Retain() == Result::Accepted,
            "explicit_discontinuity_allows_forward_restart");
        Check(buffer.Retained()->start100ns == 80 && buffer.Retained()->end100ns == 90 &&
            buffer.Retained()->configuration.epoch == 2 &&
            buffer.Retained()->configuration.h264SequenceHeader->Bytes()[0] == 3,
            "snapshot_never_bridges_gap_or_epoch");

        EncodedClipBuffer pinned({ 100, 6 });
        Check(Begin(pinned) == Result::Accepted && Push(pinned, 0, true) == Result::Accepted &&
            pinned.Retain() == Result::Accepted, "pinned_config_limit_setup");
        Check(Begin(pinned, 2) == Result::ByteLimit && pinned.AccountedBytes() == 6,
            "new_config_cannot_escape_retained_budget");
        Check(Push(pinned, 0, true, 4, 2) == Result::NoConfiguration, "failed_epoch_is_inactive");
        pinned.ReleaseRetained();
        Check(Begin(pinned, 2) == Result::Accepted, "failed_epoch_can_retry_after_release");
    }

    void InvalidInputsAndImmutableCopy()
    {
        EncodedClipBuffer invalid({ 0, 100 });
        Check(Begin(invalid) == Result::InvalidLimits && invalid.Retain() == Result::InvalidLimits,
            "invalid_duration_limit");
        EncodedClipBuffer noBytes({ 100, 0 });
        Check(Begin(noBytes) == Result::InvalidLimits, "invalid_byte_limit");
        EncodedClipBuffer buffer({ 100, 100 });
        Check(Push(buffer, 0, true) == Result::NoConfiguration && buffer.Retain() == Result::NoFrames,
            "missing_configuration_and_frames");
        Check(buffer.BeginEpoch(1, nullptr, 1) == Result::InvalidConfiguration, "null_configuration");
        const std::uint8_t headerByte = 9;
        Check(buffer.BeginEpoch(1, &headerByte, 0) == Result::InvalidConfiguration &&
            buffer.BeginEpoch(1, &headerByte, 65537) == Result::InvalidConfiguration,
            "empty_or_oversized_configuration");
        Check(Begin(buffer, 0) == Result::EpochNotIncreasing, "zero_epoch_refused");
        Check(Begin(buffer) == Result::Accepted, "invalid_input_setup");
        Check(Push(buffer, -1, true) == Result::InvalidTimestamp, "negative_time");
        Check(Push(buffer, 0, true, 4, 1, 0) == Result::InvalidTimestamp, "zero_duration");
        Check(Push(buffer, (std::numeric_limits<MediaTime>::max)(), true) == Result::InvalidTimestamp,
            "timestamp_addition_overflow");
        Check(buffer.Append(1, 0, 10, true, nullptr, 4) == Result::InvalidPayload, "null_payload");
        std::array<std::uint8_t, 4> source{ 1, 2, 3, 4 };
        Check(buffer.Append(1, 0, 10, true, source.data(), source.size()) == Result::Accepted,
            "payload_copy");
        source.fill(99);
        Check(buffer.Retain() == Result::Accepted && buffer.Retained()->packets.front().payload->Bytes()[0] == 1,
            "caller_mutation_cannot_change_snapshot");
        Check(Push(buffer, 0, true) == Result::InvalidTimestamp && buffer.AwaitingKeyframe(), "duplicate_time");
        Check(Push(buffer, 5, true) == Result::InvalidTimestamp, "overlapping_time");
        Check(Push(buffer, 10, true) == Result::Accepted, "valid_time_after_refusal");
        Check(buffer.Append(1, 20, 10, false, source.data(), 0) == Result::InvalidPayload &&
            buffer.AwaitingKeyframe(), "empty_payload_breaks_gop");
    }

    void SnapshotOutlivesBuffer()
    {
        std::shared_ptr<const Clip> clip;
        std::weak_ptr<const Payload> payload;
        {
            EncodedClipBuffer buffer({ 100, 100 });
            Check(Begin(buffer) == Result::Accepted && Push(buffer, 0, true) == Result::Accepted &&
                buffer.Retain() == Result::Accepted, "lifetime_setup");
            clip = buffer.Retained();
            payload = clip->packets.front().payload;
        }
        Check(!payload.expired() && clip->packets.front().payload->Bytes()[0] == 7 && clip->end100ns == 10,
            "snapshot_survives_buffer_destruction");
        clip.reset();
        Check(payload.expired(), "snapshot_final_release");
    }
}

int main()
{
    static_assert(std::is_const_v<std::remove_reference_t<decltype(*std::declval<Packet>().payload)>>);
    try
    {
        DurationAndGops();
        LongGopAndPacketLimits();
        RetainedBudgetAndOwnership();
        EpochAndDiscontinuity();
        InvalidInputsAndImmutableCopy();
        SnapshotOutlivesBuffer();
        std::cout << "{\"mode\":\"compressed_buffer_cpu_contracts\",\"passed\":" << checks
            << ",\"graphicsInitialized\":false,\"captureUsed\":false}\n";
        return 0;
    }
    catch (const Failure& error)
    {
        std::cout << "{\"passed\":false,\"reason\":\"" << error.contract << "\"}\n";
        return 1;
    }
    catch (...)
    {
        std::cout << "{\"passed\":false,\"reason\":\"unexpected_contract_exception\"}\n";
        return 1;
    }
}
