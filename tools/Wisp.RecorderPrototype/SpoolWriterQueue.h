#pragma once

#include "RecorderHost.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <utility>
#include <vector>

namespace recorder::host
{
    // Work for the spool writer thread, in order: encoded packets, bounded by bytes, and calls that
    // must run on the writer between them. The writer owns the spool from its initialization on, so
    // the media thread never performs filesystem work: a slow write costs queue memory, not frames.
    class SpoolWriterQueue final
    {
    public:
        struct Packet
        {
            bool video = true;
            std::int64_t time = 0, duration = 0;
            bool clean = false;
            std::vector<std::uint8_t> bytes;
        };
        enum class Post { Accepted, WriterFailed, QueueFull };

        // Media thread. Storage behind the recording waits up to waitMs for room; after that the
        // packet is refused (QueueFull) and the recording cannot be kept.
        Post PostPacket(Packet&& packet, std::uint32_t waitMs, const std::atomic<bool>& writerFailed)
        {
            const auto size = static_cast<std::uint64_t>(packet.bytes.size());
            std::unique_lock<std::mutex> lock(mutex_);
            if (writerFailed.load()) return Post::WriterFailed;
            if (!SpoolQueueAdmits(queuedBytes_, size))
            {
                const auto began = std::chrono::steady_clock::now();
                drained_.wait_for(lock, std::chrono::milliseconds(waitMs),
                    [&] { return writerFailed.load() || SpoolQueueAdmits(queuedBytes_, size); });
                wait100ns_ += static_cast<std::uint64_t>(
                    std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - began).count() / 100);
                if (writerFailed.load() || !SpoolQueueAdmits(queuedBytes_, size)) return Post::QueueFull;
            }
            Item item;
            item.kind = Kind::Packet; item.packet = std::move(packet);
            items_.push_back(std::move(item));
            queuedBytes_ += size; peakBytes_ = (std::max)(peakBytes_, queuedBytes_);
            lock.unlock();
            signal_.notify_one();
            return Post::Accepted;
        }
        // A call the writer runs after the work queued before it; done, when set, runs after call.
        void PostCall(std::function<void()> call, std::function<void()> done = {})
        {
            Item item;
            item.kind = Kind::Call; item.call = std::move(call); item.done = std::move(done);
            {
                std::lock_guard<std::mutex> lock(mutex_);
                items_.push_back(std::move(item));
            }
            signal_.notify_one();
        }
        // Stopping. Queued packets are newer than anything a save retained, and a discarded buffer
        // never needs them: dropping them keeps a stop from writing up to SpoolQueueLimitBytes past
        // its deadline. Queued calls still run, in order. Returns the bytes dropped.
        std::uint64_t DropPackets() noexcept
        {
            std::uint64_t dropped = 0;
            {
                std::lock_guard<std::mutex> lock(mutex_);
                for (auto it = items_.begin(); it != items_.end();)
                {
                    if (it->kind != Kind::Packet) { ++it; continue; }
                    dropped += it->packet.bytes.size();
                    it = items_.erase(it);
                }
                queuedBytes_ -= dropped;
            }
            drained_.notify_all();
            return dropped;
        }
        // The writer returns once the work queued before this is done.
        void PostStop()
        {
            Item item;
            item.kind = Kind::Stop;
            {
                std::lock_guard<std::mutex> lock(mutex_);
                items_.push_back(std::move(item));
            }
            signal_.notify_one();
        }
        // The writer thread, until PostStop. write and run must not throw.
        template <class Write, class Run>
        void Serve(Write&& write, Run&& run)
        {
            for (;;)
            {
                Item item;
                {
                    std::unique_lock<std::mutex> lock(mutex_);
                    signal_.wait(lock, [this] { return !items_.empty(); });
                    item = std::move(items_.front()); items_.pop_front();
                    if (item.kind == Kind::Packet) queuedBytes_ -= item.packet.bytes.size();
                }
                // Capacity is free as soon as the packet leaves the queue, not once it is on disk.
                if (item.kind == Kind::Packet) drained_.notify_all();
                if (item.kind == Kind::Stop) { drained_.notify_all(); return; }
                if (item.kind == Kind::Call)
                {
                    run(item.call);
                    if (item.done) item.done();
                    continue;
                }
                write(item.packet);
                drained_.notify_all();
            }
        }
        std::uint64_t QueuedBytes() { std::lock_guard<std::mutex> lock(mutex_); return queuedBytes_; }
        std::uint64_t PeakBytes() { std::lock_guard<std::mutex> lock(mutex_); return peakBytes_; }
        std::uint64_t Wait100ns() { std::lock_guard<std::mutex> lock(mutex_); return wait100ns_; }

    private:
        enum class Kind { Packet, Call, Stop };
        struct Item
        {
            Kind kind = Kind::Packet;
            Packet packet;
            std::function<void()> call, done;
        };
        std::mutex mutex_;
        std::condition_variable signal_, drained_;
        std::deque<Item> items_;
        std::uint64_t queuedBytes_ = 0, peakBytes_ = 0, wait100ns_ = 0;
    };
}
