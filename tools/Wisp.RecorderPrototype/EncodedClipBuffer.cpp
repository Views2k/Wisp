#include "EncodedClipBuffer.h"

#include <algorithm>
#include <atomic>
#include <iterator>
#include <limits>
#include <new>
#include <stdexcept>
#include <utility>

namespace recorder::buffer
{
    namespace detail
    {
        struct ByteBudget
        {
            explicit ByteBudget(std::uint64_t maximumBytes) : maximum(maximumBytes) {}
            const std::uint64_t maximum;
            std::atomic<std::uint64_t> used{ 0 };

            bool Fits(std::uint64_t size) const noexcept
            {
                const auto current = used.load();
                return size <= maximum && current <= maximum - size;
            }

            bool Reserve(std::uint64_t size) noexcept
            {
                auto current = used.load();
                do
                {
                    if (size > maximum || current > maximum - size) return false;
                } while (!used.compare_exchange_weak(current, current + size));
                return true;
            }

            void Release(std::uint64_t size) noexcept { used.fetch_sub(size); }
        };
    }

    Payload::Payload(const std::uint8_t* data, std::size_t size,
        std::shared_ptr<detail::ByteBudget> budget) : bytes_(data, data + size), budget_(std::move(budget)) {}

    Payload::~Payload() { budget_->Release(static_cast<std::uint64_t>(bytes_.size())); }

    EncodedClipBuffer::EncodedClipBuffer(Limits limits) : limits_(limits),
        budget_(std::make_shared<detail::ByteBudget>(limits.maximumCompressedBytes)) {}

    bool EncodedClipBuffer::ValidLimits() const noexcept
    {
        return limits_.maximumDuration100ns > 0 && limits_.maximumCompressedBytes > 0;
    }

    std::uint64_t EncodedClipBuffer::AccountedBytes() const noexcept { return budget_->used.load(); }

    MediaTime EncodedClipBuffer::RollingDuration100ns() const noexcept
    {
        return rolling_.empty() ? 0 : lastEnd100ns_ - rolling_.front().timestamp100ns;
    }

    Result EncodedClipBuffer::CopyPayload(const std::uint8_t* data, std::size_t size,
        std::shared_ptr<const Payload>& result)
    {
        const auto bytes = static_cast<std::uint64_t>(size);
        if (!budget_->Reserve(bytes)) return Result::ByteLimit;
        // Reserve before the allocation. Once the object exists, its destructor
        // owns the reservation, including a failed shared_ptr allocation.
        bool reservationOwned = true;
        try
        {
            auto payload = std::unique_ptr<Payload>(new Payload(data, size, budget_));
            reservationOwned = false;
            result = std::shared_ptr<const Payload>(std::move(payload));
            return Result::Accepted;
        }
        catch (const std::bad_alloc&)
        {
            if (reservationOwned) budget_->Release(bytes);
            return Result::AllocationFailed;
        }
        catch (const std::length_error&)
        {
            if (reservationOwned) budget_->Release(bytes);
            return Result::AllocationFailed;
        }
    }

    Result EncodedClipBuffer::BeginEpoch(std::uint64_t epoch, const std::uint8_t* header, std::size_t size)
    {
        if (!ValidLimits()) return Result::InvalidLimits;
        if (!header || size == 0 || size > 65536) return Result::InvalidConfiguration;
        if (epoch == 0 || epoch <= lastEpoch_) return Result::EpochNotIncreasing;
        MarkDiscontinuity();
        configuration_ = {};
        std::shared_ptr<const Payload> copied;
        const Result copiedResult = CopyPayload(header, size, copied);
        if (copiedResult != Result::Accepted) return copiedResult;
        configuration_ = { epoch, std::move(copied) };
        lastEpoch_ = epoch;
        hasAcceptedTime_ = false;
        lastEnd100ns_ = 0;
        return Result::Accepted;
    }

    void EncodedClipBuffer::MarkDiscontinuity() noexcept { rolling_.clear(); }

    bool EncodedClipBuffer::RemoveOldestGop(bool incomingCleanPoint)
    {
        const auto next = std::find_if(std::next(rolling_.begin()), rolling_.end(),
            [](const Packet& packet) { return packet.cleanPoint; });
        if (next != rolling_.end())
        {
            rolling_.erase(rolling_.begin(), next);
            return true;
        }
        if (incomingCleanPoint)
        {
            rolling_.clear();
            return true;
        }
        return false;
    }

    Result EncodedClipBuffer::Append(std::uint64_t epoch, MediaTime timestamp100ns, MediaTime duration100ns,
        bool cleanPoint, const std::uint8_t* data, std::size_t size)
    {
        if (!ValidLimits()) return Result::InvalidLimits;
        if (!configuration_.h264SequenceHeader) return Result::NoConfiguration;
        if (epoch != configuration_.epoch) return Result::EpochMismatch;
        const auto reject = [this](Result reason) { MarkDiscontinuity(); return reason; };
        if (timestamp100ns < 0 || duration100ns <= 0 ||
            timestamp100ns > (std::numeric_limits<MediaTime>::max)() - duration100ns)
            return reject(Result::InvalidTimestamp);
        if (hasAcceptedTime_ && timestamp100ns < lastEnd100ns_)
            return reject(Result::InvalidTimestamp);
        if (!data || size == 0) return reject(Result::InvalidPayload);
        if (duration100ns > limits_.maximumDuration100ns) return reject(Result::DurationLimit);
        if (static_cast<std::uint64_t>(size) > limits_.maximumCompressedBytes)
            return reject(Result::ByteLimit);
        if (rolling_.empty() && !cleanPoint) return Result::KeyframeRequired;
        if (!rolling_.empty() && timestamp100ns != lastEnd100ns_)
            return reject(Result::Discontinuity);

        const MediaTime end = timestamp100ns + duration100ns;
        while (!rolling_.empty())
        {
            const bool tooLong = end - rolling_.front().timestamp100ns > limits_.maximumDuration100ns;
            const bool tooLarge = !budget_->Fits(static_cast<std::uint64_t>(size));
            if (!tooLong && !tooLarge) break;
            if (!RemoveOldestGop(cleanPoint)) return reject(tooLong ? Result::DurationLimit : Result::ByteLimit);
        }
        std::shared_ptr<const Payload> copied;
        const Result copiedResult = CopyPayload(data, size, copied);
        if (copiedResult != Result::Accepted) return reject(copiedResult);
        try
        {
            rolling_.push_back({ timestamp100ns, duration100ns, cleanPoint, std::move(copied) });
        }
        catch (const std::bad_alloc&) { return reject(Result::AllocationFailed); }
        catch (const std::length_error&) { return reject(Result::AllocationFailed); }
        lastEnd100ns_ = end;
        hasAcceptedTime_ = true;
        return Result::Accepted;
    }

    Result EncodedClipBuffer::Retain()
    {
        if (!ValidLimits()) return Result::InvalidLimits;
        if (!outstandingClip_.expired()) return Result::RetainedClipExists;
        if (rolling_.empty()) return Result::NoFrames;
        try
        {
            auto snapshot = std::make_shared<Clip>();
            snapshot->configuration = configuration_;
            snapshot->packets.assign(rolling_.begin(), rolling_.end());
            snapshot->start100ns = rolling_.front().timestamp100ns;
            snapshot->end100ns = lastEnd100ns_;
            retained_ = std::move(snapshot);
            outstandingClip_ = retained_;
        }
        catch (const std::bad_alloc&) { return Result::AllocationFailed; }
        catch (const std::length_error&) { return Result::AllocationFailed; }
        return Result::Accepted;
    }
}
