#include "SpoolWriterQueue.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <iostream>
#include <mutex>
#include <thread>
#include <vector>

namespace
{
    using namespace recorder::host;
    using Clock = std::chrono::steady_clock;
    using Post = SpoolWriterQueue::Post;
    struct Failure { const char* contract; };
    unsigned checks = 0;
    void Check(bool value, const char* contract)
    {
        if (!value) throw Failure{ contract };
        ++checks;
    }

    std::uint64_t ElapsedMs(Clock::time_point began)
    {
        return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - began).count());
    }

    SpoolWriterQueue::Packet MakePacket(std::size_t bytes, std::int64_t time = 0)
    {
        SpoolWriterQueue::Packet packet;
        packet.time = time;
        packet.bytes.assign(bytes, static_cast<std::uint8_t>(7));
        return packet;
    }

    // Stops a writer that a failed check left running, so the failure is reported instead of
    // ending the process.
    struct Writer
    {
        SpoolWriterQueue& queue;
        std::function<void()> release;
        std::thread thread;
        ~Writer()
        {
            if (!thread.joinable()) return;
            if (release) release();
            queue.PostStop();
            thread.join();
        }
    };

    // A drive slower than the recording: a queue near SpoolQueueLimitBytes needs about eight
    // seconds to write at this speed.
    constexpr std::uint64_t SlowBytesPerSecond = 64ull * 1024 * 1024;
    constexpr std::size_t BacklogPacketBytes = 16 * 1024 * 1024;

    void RefusalsAreBounded()
    {
        SpoolWriterQueue queue;
        std::atomic<bool> failed{ true };
        Check(queue.PostPacket(MakePacket(16), 1000, failed) == Post::WriterFailed && queue.QueuedBytes() == 0,
            "failed_writer_refuses_without_waiting");
        failed.store(false);
        const auto began = Clock::now();
        Check(queue.PostPacket(MakePacket(0), 50, failed) == Post::QueueFull && ElapsedMs(began) >= 40,
            "unadmitted_packet_waits_then_refuses");
        Check(queue.QueuedBytes() == 0 && queue.Wait100ns() > 0, "refusal_counts_wait_not_bytes");
        Check(queue.PostPacket(MakePacket(16), 0, failed) == Post::Accepted && queue.QueuedBytes() == 16 &&
            queue.PeakBytes() == 16, "accepted_packet_counted");
        Check(queue.DropPackets() == 16 && queue.QueuedBytes() == 0 && queue.PeakBytes() == 16,
            "drop_returns_bytes_and_keeps_peak");
    }

    void DropKeepsCallsInOrder()
    {
        SpoolWriterQueue queue;
        std::atomic<bool> failed{ false };
        std::mutex mutex;
        std::condition_variable changed;
        bool inHand = false, release = false;
        std::vector<std::int64_t> log; // Written only by the writer; read after it joins.
        const auto open = [&]
        {
            {
                std::lock_guard<std::mutex> lock(mutex);
                release = true;
            }
            changed.notify_all();
        };
        Writer writer{ queue, open, std::thread([&]
        {
            queue.Serve([&](SpoolWriterQueue::Packet& packet)
            {
                {
                    std::unique_lock<std::mutex> lock(mutex);
                    inHand = true; changed.notify_all();
                    changed.wait(lock, [&] { return release; });
                }
                log.push_back(packet.time);
            }, [](std::function<void()>& call) { call(); });
        }) };
        Check(queue.PostPacket(MakePacket(8, 1), 0, failed) == Post::Accepted, "packet_in_hand_posted");
        {
            std::unique_lock<std::mutex> lock(mutex);
            Check(changed.wait_for(lock, std::chrono::seconds(5), [&] { return inHand; }), "writer_takes_first_packet");
        }
        queue.PostCall([&] { log.push_back(101); });
        Check(queue.PostPacket(MakePacket(8, 2), 0, failed) == Post::Accepted, "queued_packet_posted");
        queue.PostCall([&] { log.push_back(102); });
        Check(queue.PostPacket(MakePacket(8, 3), 0, failed) == Post::Accepted, "last_packet_posted");
        Check(queue.DropPackets() == 16 && queue.QueuedBytes() == 0, "drop_removes_only_queued_packets");
        open();
        queue.PostStop();
        writer.thread.join();
        Check(log == std::vector<std::int64_t>{ 1, 101, 102 }, "packet_in_hand_and_calls_finish_in_order");
    }

    void StopNearTheQueueLimitFinishesBeforeTheDeadline()
    {
        SpoolWriterQueue queue;
        std::atomic<bool> failed{ false };
        std::atomic<std::uint64_t> written{ 0 };
        Writer writer{ queue, {}, std::thread([&]
        {
            queue.Serve([&](SpoolWriterQueue::Packet& packet)
            {
                std::this_thread::sleep_for(std::chrono::microseconds(
                    static_cast<std::int64_t>(packet.bytes.size() * 1000000ull / SlowBytesPerSecond)));
                written += packet.bytes.size();
            }, [](std::function<void()>& call) { call(); });
        }) };
        // Fill toward the limit faster than the drive drains it.
        std::uint64_t posted = 0;
        while (posted + BacklogPacketBytes <= SpoolQueueLimitBytes &&
            queue.PostPacket(MakePacket(BacklogPacketBytes), 0, failed) == Post::Accepted)
            posted += BacklogPacketBytes;
        const auto backlog = queue.QueuedBytes();
        Check(backlog >= SpoolQueueLimitBytes - 4 * BacklogPacketBytes, "backlog_near_queue_limit");
        // Writing this backlog would outlast the helper's stop deadline.
        Check(backlog * 1000 / SlowBytesPerSecond > StopLimitMs, "backlog_write_exceeds_stop_deadline");

        // The clean stop as MediaSession::Close performs it: drop the queued packets, close the
        // spool on the writer, then stop the writer.
        const auto began = Clock::now();
        const auto writtenBefore = written.load();
        const auto dropped = queue.DropPackets();
        std::mutex mutex;
        std::condition_variable closed;
        bool spoolClosed = false;
        queue.PostCall([] {}, [&]
        {
            std::lock_guard<std::mutex> lock(mutex);
            spoolClosed = true; closed.notify_all();
        });
        {
            std::unique_lock<std::mutex> lock(mutex);
            Check(closed.wait_for(lock, std::chrono::milliseconds(StopLimitMs), [&] { return spoolClosed; }),
                "spool_close_runs_after_the_packet_in_hand");
        }
        queue.PostStop();
        writer.thread.join();
        const auto elapsed = ElapsedMs(began);
        Check(dropped <= backlog && dropped + 2 * BacklogPacketBytes >= backlog && queue.QueuedBytes() == 0,
            "queued_packets_dropped");
        Check(written.load() - writtenBefore <= 2 * BacklogPacketBytes, "stop_writes_only_packets_in_hand");
        // The clean stop path: the audio source's bound, then the spool. Both fit the deadline.
        Check(elapsed + AudioStopMs < StopLimitMs, "clean_stop_within_stop_deadline");
        std::cout << "{\"backlogBytes\":" << backlog << ",\"droppedBytes\":" << dropped
            << ",\"stopMs\":" << elapsed << ",\"stopLimitMs\":" << StopLimitMs << "}\n";
    }
}

int main()
{
    try
    {
        RefusalsAreBounded();
        DropKeepsCallsInOrder();
        StopNearTheQueueLimitFinishesBeforeTheDeadline();
        std::cout << "{\"mode\":\"spool_stop_cpu_contracts\",\"passed\":" << checks
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
