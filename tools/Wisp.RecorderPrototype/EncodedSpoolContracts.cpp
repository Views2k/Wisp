#include "EncodedSpool.h"

#include <array>
#include <algorithm>
#include <cstring>
#include <cwchar>
#include <limits>
#include <stdexcept>

namespace recorder::spool
{
    namespace
    {
        void Expect(bool value, int& count)
        { if (!value) throw ContractFailure{ count + 1 }; ++count; }
        std::wstring GuidName()
        {
            GUID id{};
            if (FAILED(CoCreateGuid(&id))) throw std::runtime_error("fixture_guid_failed");
            std::array<wchar_t, 33> name{};
            if (swprintf_s(name.data(), name.size(), L"%08x%04x%04x%02x%02x%02x%02x%02x%02x%02x%02x",
                static_cast<unsigned int>(id.Data1), static_cast<unsigned int>(id.Data2), static_cast<unsigned int>(id.Data3),
                static_cast<unsigned int>(id.Data4[0]), static_cast<unsigned int>(id.Data4[1]), static_cast<unsigned int>(id.Data4[2]),
                static_cast<unsigned int>(id.Data4[3]), static_cast<unsigned int>(id.Data4[4]), static_cast<unsigned int>(id.Data4[5]),
                static_cast<unsigned int>(id.Data4[6]), static_cast<unsigned int>(id.Data4[7])) != 32) throw std::runtime_error("fixture_guid_name_failed");
            return name.data();
        }
        std::wstring Session(const std::wstring& parent)
        { return parent + (parent.empty() || parent.back() != L'\\' ? L"\\" : L"") + L".wisp-recorder-" + GuidName(); }
        Configuration Format(bool audio = false)
        {
            Configuration config{}; config.epoch = 1; config.h264SequenceHeader = { 0, 0, 0, 1 };
            if (audio) config.aacUserData = { 0x11, 0x90 }; // Opaque fixture bytes, not claimed valid MF configuration.
            return config;
        }
        constexpr MediaTime Second = 10000000;
        struct ContractHandle
        {
            HANDLE value = INVALID_HANDLE_VALUE;
            ~ContractHandle() { if (value != INVALID_HANDLE_VALUE) (void)CloseHandle(value); }
            bool Close() noexcept
            {
                if (value == INVALID_HANDLE_VALUE) return true;
                if (!CloseHandle(value)) return false;
                value = INVALID_HANDLE_VALUE; return true;
            }
        };
        std::uint64_t Little(const std::uint8_t* data, unsigned bytes)
        {
            std::uint64_t value = 0;
            for (unsigned index = 0; index < bytes; ++index) value |= static_cast<std::uint64_t>(data[index]) << (index * 8);
            return value;
        }
        void VerifyOwnership(const std::wstring& path, const std::wstring& name, int& tests)
        {
            ContractHandle data, companion;
            data.value = CreateFileW((path + L"\\" + name).c_str(), FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            companion.value = CreateFileW((path + L"\\" + name + L".owner").c_str(), GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            Expect(data.value != INVALID_HANDLE_VALUE && companion.value != INVALID_HANDLE_VALUE, tests);
            LARGE_INTEGER size{};
            Expect(GetFileSizeEx(companion.value, &size) && size.QuadPart == OwnershipRecordBytes, tests);
            std::array<std::uint8_t, OwnershipRecordBytes> record{};
            DWORD read = 0;
            Expect(ReadFile(companion.value, record.data(), OwnershipRecordBytes, &read, nullptr) && read == OwnershipRecordBytes, tests);
            Expect(std::memcmp(record.data(), "WSCOWN01", 8) == 0 && Little(record.data() + 8, 4) == 1 &&
                Little(record.data() + 12, 4) == OwnershipRecordBytes, tests);
            const auto session = path.substr(path.size() - 32);
            bool namesMatch = true;
            for (std::size_t index = 0; index < session.size(); ++index)
                namesMatch &= record[16 + index] == static_cast<std::uint8_t>(session[index]);
            for (std::size_t index = 0; index < name.size(); ++index)
                namesMatch &= record[72 + index] == static_cast<std::uint8_t>(name[index]);
            Expect(namesMatch && std::all_of(record.begin() + 72 + name.size(), record.end(),
                [](std::uint8_t value) { return value == 0; }), tests);
            FILE_ID_INFO identity{};
            Expect(GetFileInformationByHandleEx(data.value, FileIdInfo, &identity, sizeof(identity)) &&
                Little(record.data() + 48, 8) == identity.VolumeSerialNumber &&
                std::memcmp(record.data() + 56, identity.FileId.Identifier, 16) == 0, tests);
            Expect(companion.Close() && data.Close(), tests);
        }
        std::uint32_t ReadAll(const Snapshot& snapshot, Track track, std::uint8_t expected, int& tests)
        {
            std::unique_ptr<PacketCursor> cursor, duplicate;
            Expect(SUCCEEDED(snapshot.OpenCursor(track, cursor)), tests);
            Expect(snapshot.OpenCursor(track, duplicate) == HRESULT_FROM_WIN32(ERROR_BUSY), tests);
            Packet packet{}; std::uint32_t count = 0;
            for (;;)
            {
                const auto result = cursor->Next(packet);
                if (result == ReadResult::End) break;
                Expect(result == ReadResult::Packet && !packet.bytes.empty() && packet.bytes.front() == expected, tests);
                ++count;
            }
            Expect(SUCCEEDED(cursor->Close()), tests);
            return count;
        }
    }
    int RunSpoolCpuContracts()
    {
        int tests = 0;
        Limits limits{};
        Expect(ValidateRetentionBudget({}) == nullptr, tests);
        Expect(ValidateRetentionBudget({1, 1}) == nullptr, tests);
        for (std::uint64_t bytes : {0ull, MaximumLosslessVideoBytes + 1, (std::numeric_limits<std::uint64_t>::max)()})
            Expect(ValidateRetentionBudget({bytes, 1}) != nullptr, tests);
        for (std::uint64_t bytes : {0ull, MaximumRetainedAudioBytes + 1, (std::numeric_limits<std::uint64_t>::max)()})
            Expect(ValidateRetentionBudget({1, bytes}) != nullptr, tests);
        Expect(ValidateLimits(limits) != nullptr, tests);
        limits.maximumFileBytes = 4096;
        Expect(ValidateLimits(limits) == nullptr, tests);
        Expect(limits.maximumPacketBytes == StandardMaximumPacketBytes && StandardMaximumPacketBytes == 16u * 1024 * 1024, tests);
        for (std::uint32_t bytes : {1u, StandardMaximumPacketBytes + 1, MaximumPacketBytes})
        { auto changed = limits; changed.maximumPacketBytes = bytes; Expect(ValidateLimits(changed) == nullptr, tests); }
        for (std::uint32_t bytes : {0u, MaximumPacketBytes + 1})
        { auto invalid = limits; invalid.maximumPacketBytes = bytes; Expect(ValidateLimits(invalid) != nullptr, tests); }
        for (MediaTime span : { MediaTime{ 0 }, MediaTime{ -1 }, MaximumDuration + 1 })
        { auto invalid = limits; invalid.maximumDuration100ns = span; Expect(ValidateLimits(invalid) != nullptr, tests); }
        for (std::uint64_t bytes : { 0ull, 4095ull, 64ull * 1024 * 1024 * 1024 + 1 })
        { auto invalid = limits; invalid.maximumFileBytes = bytes; Expect(ValidateLimits(invalid) != nullptr, tests); }
        for (std::uint32_t count : { 0u, 15u, 200001u })
        { auto invalid = limits; invalid.maximumRecords = count; Expect(ValidateLimits(invalid) != nullptr, tests); }
        for (std::uint32_t count : { 0u, 3u, 4097u })
        { auto invalid = limits; invalid.maximumFiles = count; Expect(ValidateLimits(invalid) != nullptr, tests); }
        const std::wstring leaf = L".wisp-recorder-0123456789abcdef0123456789abcdef";
        Expect(ValidateSessionPath(L"C:\\" + leaf), tests);
        Expect(ValidateSessionPath(L"D:\\Clips Library\\" + leaf), tests);
        for (const auto* prefix : { L"", L".\\", L"\\\\server\\share\\", L"\\\\?\\C:\\", L"C:/", L"C:\\\\",
            L"C:\\..\\", L"C:\\.\\", L"C:\\bad.\\", L"C:\\bad \\", L"C:\\file:stream\\", L"C:\\CON\\",
            L"C:\\nul.txt\\", L"C:\\COM1\\", L"C:\\LPT9\\" })
            Expect(!ValidateSessionPath(prefix + leaf), tests);
        Expect(!ValidateSessionPath(L"C:\\" + leaf + L"\\"), tests);
        Expect(!ValidateSessionPath(L"C:\\" + leaf + L"x"), tests);
        Expect(!ValidateSessionPath(L"C:\\.wisp-recorder-00000000000000000000000000000000"), tests);
        Expect(!ValidateSessionPath(L"C:\\.wisp-recorder-0123456789ABCDEF0123456789abcdef"), tests);
        auto nul = L"C:\\" + leaf; nul.insert(4, 1, L'\0');
        Expect(!ValidateSessionPath(nul), tests);
        const std::wstring exportName = L"0123456789abcdef0123456789abcdef.mp4";
        Expect(ValidateExportName(exportName), tests);
        for (const auto* name : { L"00000000000000000000000000000000.mp4", L"../0123456789abcdef0123456789abcdef.mp4",
            L"0123456789abcdef0123456789abcdef.MP4", L"0123456789abcdef0123456789abcdef.mp4:stream", L"anything.mp4" })
            Expect(!ValidateExportName(name), tests);
        EncodedSpool inert;
        std::array<std::uint8_t, 1> byte{ 1 };
        Expect(!inert.AppendVideo(0, Second, true, byte.data(), byte.size()), tests);
        std::shared_ptr<const Snapshot> snapshot;
        Expect(!inert.Retain(Second, snapshot), tests);
        AvailablePlan plan;
        Expect(!inert.PlanAvailable(Second, {}, plan) && plan.bounds.videoPackets == 0, tests);
        Expect(!inert.RetainAvailable(Second, {}, snapshot, plan) && !snapshot, tests);
        HANDLE file = nullptr;
        Expect(FAILED(inert.CreateNewExport(exportName, file)) && !file, tests);
        Expect(SUCCEEDED(inert.Close()) && inert.Result().closed, tests);
        return tests;
    }
    int RunSpoolFileContracts(const std::wstring& parent)
    {
        int tests = 0;
        // Caller chooses an isolated existing local fixture directory. No capture,
        // process, junction creation, directory scan, or recursive deletion occurs.
        Limits limits{}; limits.maximumFileBytes = 1024 * 1024;
        std::array<std::uint8_t, 256> payload{}; payload.fill(0x5a);
        {
            const std::vector<std::uint8_t> large(StandardMaximumPacketBytes + 1u, 0x5a);
            auto largeLimits = limits; largeLimits.maximumFileBytes = 64ull * 1024 * 1024;
            EncodedSpool standard;
            Expect(standard.Initialize(Session(parent), largeLimits, Format()), tests);
            Expect(!standard.AppendVideo(0, Second, true, large.data(), large.size()) &&
                std::strcmp(standard.Result().reason, "spool_packet_size_invalid") == 0, tests);
            Expect(standard.Result().committedPackets == 0 && SUCCEEDED(standard.Close()), tests);
            largeLimits.maximumPacketBytes = MaximumPacketBytes;
            EncodedSpool lossless;
            Expect(lossless.Initialize(Session(parent), largeLimits, Format()), tests);
            Expect(lossless.AppendVideo(0, Second, true, large.data(), large.size()), tests);
            std::shared_ptr<const Snapshot> snapshot; AvailablePlan plan;
            Expect(lossless.RetainAvailable(30 * Second, {}, snapshot, plan) && plan.videoPayloadBytes == large.size(), tests);
            std::unique_ptr<PacketCursor> cursor; Packet packet;
            Expect(SUCCEEDED(snapshot->OpenCursor(Track::Video, cursor)), tests);
            Expect(cursor->Next(packet) == ReadResult::Packet && packet.bytes == large, tests);
            Expect(cursor->Next(packet) == ReadResult::End && SUCCEEDED(cursor->Close()), tests);
            cursor.reset(); snapshot.reset(); Expect(SUCCEEDED(lossless.Close()), tests);
        }
        {
            const auto path = Session(parent);
            Expect(SUCCEEDED(PreflightSessionParent(path)), tests);
            Expect(GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES && GetLastError() == ERROR_FILE_NOT_FOUND, tests);
            Expect(PreflightSessionParent(L"relative\\.wisp-recorder-00000000000000000000000000000000") == E_INVALIDARG, tests);
            Expect(FAILED(PreflightSessionParent(Session(parent + L"\\missing-" + GuidName()))), tests);
        }
        {
            EncodedSpool spool;
            Expect(spool.Initialize(Session(parent), limits, Format()), tests);
            AvailablePlan plan;
            Expect(!spool.PlanAvailable(30 * Second, {}, plan) && plan.bounds.videoPackets == 0 && !plan.sizeLimited, tests);
            constexpr MediaTime frame = Second / 60;
            Expect(spool.AppendVideo(0, frame, true, payload.data(), payload.size()), tests);
            const auto before = spool.Result();
            Expect(spool.PlanAvailable(30 * Second, {256, 1}, plan) && !plan.sizeLimited &&
                plan.bounds.start100ns == 0 && plan.bounds.end100ns == frame && plan.bounds.videoPackets == 1 &&
                plan.videoPayloadBytes == 256 && plan.audioPayloadBytes == 0, tests);
            const auto after = spool.Result();
            Expect(before.accountedFileBytes == after.accountedFileBytes && before.ownedFiles == after.ownedFiles &&
                before.rollingRecords == after.rollingRecords && after.snapshotRecords == 0, tests);
            std::shared_ptr<const Snapshot> snapshot, duplicate;
            Expect(spool.RetainAvailable(30 * Second, {256, 1}, snapshot, plan) && !plan.sizeLimited, tests);
            Expect(spool.PlanAvailable(30 * Second, {256, 1}, plan) && !plan.sizeLimited, tests); // Planning never takes the save's pin.
            Expect(!spool.RetainAvailable(30 * Second, {256, 1}, duplicate, plan) && !duplicate && plan.bounds.videoPackets == 0, tests);
            Expect(spool.AppendVideo(frame, frame + 1, false, payload.data(), payload.size()), tests);
            Expect(!spool.PlanAvailable(30 * Second, {256, 1}, plan) &&
                std::strcmp(spool.Result().reason, "spool_no_keyframe_within_byte_budget") == 0 && plan.videoPayloadBytes == 0, tests);
            const MediaTime third = 2 * frame + 1;
            Expect(spool.AppendVideo(third, frame + 1, true, payload.data(), payload.size()), tests);
            Expect(spool.PlanAvailable(30 * Second, {256, 1}, plan) && plan.sizeLimited &&
                plan.bounds.start100ns == third && plan.bounds.end100ns == third + frame + 1 && plan.bounds.videoPackets == 1, tests);
            Expect(ReadAll(*snapshot, Track::Video, 0x5a, tests) == 1, tests); // The active GOP's retained prefix did not grow.
            snapshot.reset();
            Expect(spool.RetainAvailable(30 * Second, {256, 1}, snapshot, plan) && plan.sizeLimited &&
                snapshot->Range().start100ns == third && snapshot->Range().videoPackets == 1, tests);
            snapshot.reset(); Expect(SUCCEEDED(spool.Close()), tests);
        }
        for (const MediaTime audioStart : { MediaTime{207}, MediaTime{213334}, MediaTime{213335} })
        {
            EncodedSpool spool;
            Expect(spool.Initialize(Session(parent), limits, Format(true)), tests);
            constexpr MediaTime frame = Second / 60;
            Expect(spool.AppendVideo(0, frame, true, payload.data(), 1), tests);
            Expect(spool.AppendVideo(frame, frame + 1, false, payload.data(), 1), tests);
            Expect(spool.AppendAudio(audioStart, 213333, payload.data(), 1), tests);
            Expect(spool.AppendAudio(audioStart + 213333, 213334, payload.data(), 1), tests);
            AvailablePlan plan;
            std::shared_ptr<const Snapshot> snapshot;
            Expect(!spool.Retain(30 * Second, snapshot) && !snapshot, tests); // Standard selection is unchanged.
            if (audioStart <= 213334)
            {
                Expect(spool.PlanAvailable(30 * Second, {}, plan) && !plan.sizeLimited &&
                    plan.bounds.start100ns == 0 && plan.bounds.end100ns == 2 * frame + 1 &&
                    plan.bounds.audioStart100ns == audioStart && plan.bounds.videoPackets == 2, tests);
                Expect(spool.RetainAvailable(30 * Second, {}, snapshot, plan) &&
                    snapshot->Range().start100ns == 0 && snapshot->Range().audioStart100ns == audioStart, tests);
                Expect(ReadAll(*snapshot, Track::Video, 0x5a, tests) == 2, tests);
                Expect(ReadAll(*snapshot, Track::Audio, 0x5a, tests) == (audioStart == 207 ? 2u : 1u), tests);
                snapshot.reset();
            }
            else
            {
                Expect(!spool.PlanAvailable(30 * Second, {}, plan) && plan.bounds.videoPackets == 0 &&
                    std::strcmp(spool.Result().reason, "spool_no_keyframe_in_requested_span") == 0, tests);
                Expect(!spool.RetainAvailable(30 * Second, {}, snapshot, plan) && !snapshot &&
                    plan.bounds.videoPackets == 0, tests);
            }
            Expect(SUCCEEDED(spool.Close()), tests);
        }
        {
            EncodedSpool spool;
            Expect(spool.Initialize(Session(parent), limits, Format()), tests);
            for (int frame = 0; frame < 5; ++frame)
                Expect(spool.AppendVideo(frame * Second, Second, frame % 2 == 0, payload.data(), payload.size()), tests);
            AvailablePlan plan;
            Expect(spool.PlanAvailable(30 * Second, {1280, 1}, plan) && !plan.sizeLimited && plan.videoPayloadBytes == 1280, tests);
            Expect(spool.PlanAvailable(4 * Second, {1280, 1}, plan) && !plan.sizeLimited &&
                plan.bounds.start100ns == 2 * Second && plan.bounds.end100ns == 5 * Second, tests); // IDR alignment alone is not size limiting.
            Expect(!spool.PlanAvailable(30 * Second, {255, 1}, plan) && plan.bounds.videoPackets == 0, tests);
            Expect(!spool.PlanAvailable(0, {}, plan) && plan.bounds.videoPackets == 0, tests);
            Expect(!spool.PlanAvailable(30 * Second, {0, 1}, plan) && plan.bounds.videoPackets == 0, tests);
            std::shared_ptr<const Snapshot> snapshot;
            Expect(spool.RetainAvailable(30 * Second, {768, 1}, snapshot, plan) && plan.sizeLimited &&
                plan.videoPayloadBytes == 768 && plan.bounds.start100ns == 2 * Second && plan.bounds.end100ns == 5 * Second &&
                plan.bounds.videoPackets == 3, tests);
            Expect(ReadAll(*snapshot, Track::Video, 0x5a, tests) == 3, tests);
            snapshot.reset();
            Expect(spool.Retain(30 * Second, snapshot) && snapshot->Range().videoPackets == 5, tests); // Explicit lossless planning never evicts source history.
            snapshot.reset(); Expect(SUCCEEDED(spool.Close()), tests);
        }
        {
            EncodedSpool spool;
            const auto path = Session(parent);
            Expect(spool.Initialize(path, limits, Format()), tests);
            VerifyOwnership(path, L"configuration.bin", tests);
            Expect(spool.Result().accountedFileBytes == 28 + OwnershipRecordBytes && spool.Result().ownedFiles == 2, tests);
            EncodedSpool collision;
            Expect(!collision.Initialize(path, limits, Format()), tests); // Never reopen an existing session.
            Expect(SUCCEEDED(collision.Close()), tests);
            for (int frame = 0; frame < 11; ++frame)
                Expect(spool.AppendVideo(frame * Second, Second, frame % 2 == 0, payload.data(), payload.size()), tests);
            VerifyOwnership(path, L"v0000000000000001.bin", tests);
            std::shared_ptr<const Snapshot> snapshot, duplicate;
            Expect(spool.Retain(5 * Second, snapshot), tests);
            Expect(snapshot->Range().start100ns == 6 * Second && snapshot->Range().end100ns == 11 * Second &&
                snapshot->Range().videoPackets == 5, tests);
            Expect(!spool.Retain(Second, duplicate), tests);
            Expect(spool.Close() == HRESULT_FROM_WIN32(ERROR_BUSY), tests);
            for (int frame = 11; frame < 16; ++frame)
                Expect(spool.AppendVideo(frame * Second, Second, frame % 2 == 0, payload.data(), payload.size()), tests);
            Expect(ReadAll(*snapshot, Track::Video, 0x5a, tests) == 5, tests); // Immutable active-file prefix.
            std::unique_ptr<PacketCursor> held;
            Expect(SUCCEEDED(snapshot->OpenCursor(Track::Video, held)), tests);
            snapshot.reset();
            Expect(!spool.Retain(Second, duplicate), tests); // Cursor alone still pins the snapshot.
            Expect(SUCCEEDED(held->Close()), tests); held.reset();
            Expect(spool.Result().snapshotRecords == 0, tests);
            HANDLE exported = nullptr, second = nullptr;
            const auto name = GuidName() + L".mp4";
            Expect(SUCCEEDED(spool.CreateNewExport(name, exported)) && exported, tests);
            Expect(FAILED(spool.CreateNewExport(name, second)) && !second, tests);
            Expect(CloseHandle(exported) != FALSE, tests);
            Expect(SUCCEEDED(spool.Close()), tests);
        }
        {
            EncodedSpool spool;
            auto constrainedLimits = limits; constrainedLimits.maximumFileBytes = 4096;
            const auto path = Session(parent);
            Expect(spool.Initialize(path, constrainedLimits, Format()), tests);
            // Each single-packet GOP costs288 media/header bytes +128 proof
            // bytes. Nine fit under the unchanged4096-byte quota.
            for (int frame = 0; frame < 9; ++frame)
                Expect(spool.AppendVideo(frame * Second, Second, true, payload.data(), payload.size()), tests);
            std::shared_ptr<const Snapshot> snapshot;
            Expect(spool.Retain(30 * Second, snapshot) && snapshot->Range().videoPackets == 9, tests);
            Expect(!spool.AppendVideo(9 * Second, Second, true, payload.data(), payload.size()), tests);
            Expect(std::strcmp(spool.Result().reason, "spool_capacity_reached") == 0 &&
                !spool.Result().poisoned && spool.Result().accountedFileBytes <= 4096, tests);
            Expect(ReadAll(*snapshot, Track::Video, 0x5a, tests) == 9, tests); // Quota refusal preserved pinned media.
            snapshot.reset();
            Expect(spool.AppendVideo(9 * Second, Second, true, payload.data(), payload.size()), tests);
            Expect(GetFileAttributesW((path + L"\\v0000000000000001.bin").c_str()) == INVALID_FILE_ATTRIBUTES &&
                GetLastError() == ERROR_FILE_NOT_FOUND, tests);
            Expect(GetFileAttributesW((path + L"\\v0000000000000001.bin.owner").c_str()) == INVALID_FILE_ATTRIBUTES &&
                GetLastError() == ERROR_FILE_NOT_FOUND, tests);
            Expect(spool.Result().accountedFileBytes <= 4096 && spool.Result().ownedFiles <= constrainedLimits.maximumFiles, tests);
            Expect(!spool.Retain(30 * Second, snapshot) &&
                std::strcmp(spool.Result().reason, "spool_requested_history_evicted_by_capacity") == 0, tests);
            AvailablePlan plan;
            Expect(spool.PlanAvailable(30 * Second, {}, plan) && plan.sizeLimited &&
                plan.bounds.end100ns == 10 * Second && plan.videoPayloadBytes == payload.size(), tests);
            Expect(spool.RetainAvailable(30 * Second, {}, snapshot, plan) && plan.sizeLimited &&
                snapshot->Range().start100ns == plan.bounds.start100ns && snapshot->Range().videoPackets == plan.bounds.videoPackets, tests);
            Expect(ReadAll(*snapshot, Track::Video, 0x5a, tests) == plan.bounds.videoPackets, tests);
            snapshot.reset();
            Expect(spool.PlanAvailable(Second, {}, plan) && !plan.sizeLimited && plan.bounds.start100ns == 9 * Second, tests);
            Expect(SUCCEEDED(spool.Close()), tests);
        }
        {
            EncodedSpool spool;
            Expect(spool.Initialize(Session(parent), limits, Format()), tests);
            for (int frame = 0; frame < 302; ++frame)
                Expect(spool.AppendVideo(frame * Second, Second, frame % 2 == 0, payload.data(), 1), tests);
            std::shared_ptr<const Snapshot> snapshot;
            Expect(spool.Retain(MaximumDuration, snapshot), tests);
            Expect(snapshot->Range().end100ns - snapshot->Range().start100ns == MaximumDuration &&
                snapshot->Range().start100ns == 2 * Second, tests); // Cannot accidentally hand mux 302 seconds.
            snapshot.reset(); Expect(SUCCEEDED(spool.Close()), tests);
        }
        {
            EncodedSpool spool;
            Expect(spool.Initialize(Session(parent), limits, Format(true)), tests);
            for (int frame = 0; frame < 469; ++frame)
            {
                const MediaTime time = static_cast<MediaTime>(frame) * 1024 * Second / 48000;
                const MediaTime end = static_cast<MediaTime>(frame + 1) * 1024 * Second / 48000;
                Expect(spool.AppendAudio(time, end - time, payload.data(), 1), tests);
            }
            Expect(!spool.AppendAudio(100053334, 213333, payload.data(), 1), tests); // One-tick gap is not silently normalized.
            for (int frame = 0; frame < 12; ++frame)
                Expect(spool.AppendVideo(frame * Second, Second, frame % 2 == 0, payload.data(), 1), tests);
            std::shared_ptr<const Snapshot> snapshot;
            Expect(spool.Retain(5 * Second, snapshot), tests);
            Expect(snapshot->Range().start100ns == 6 * Second && snapshot->Range().end100ns == 10 * Second &&
                snapshot->Range().audioStart100ns >= snapshot->Range().start100ns &&
                snapshot->Range().audioStart100ns - snapshot->Range().start100ns <= 213334 &&
                snapshot->Range().audioEnd100ns >= snapshot->Range().end100ns &&
                snapshot->Range().audioEnd100ns - snapshot->Range().end100ns <= 213334, tests);
            Expect(ReadAll(*snapshot, Track::Audio, 0x5a, tests) == 187, tests);
            snapshot.reset();
            AvailablePlan plan;
            Expect(spool.PlanAvailable(5 * Second, {1000, 187}, plan) && !plan.sizeLimited &&
                plan.bounds.start100ns == 6 * Second && plan.bounds.end100ns == 10 * Second && plan.audioPayloadBytes == 187, tests);
            Expect(spool.RetainAvailable(5 * Second, {1000, 186}, snapshot, plan) && plan.sizeLimited &&
                plan.bounds.start100ns == 8 * Second && plan.bounds.end100ns == 10 * Second &&
                plan.videoPayloadBytes == 2 && plan.audioPayloadBytes == 94 && plan.bounds.audioPackets == 94, tests);
            Expect(snapshot->Range().audioStart100ns == plan.bounds.audioStart100ns &&
                snapshot->Range().audioEnd100ns == plan.bounds.audioEnd100ns, tests);
            Expect(ReadAll(*snapshot, Track::Audio, 0x5a, tests) == 94, tests);
            snapshot.reset(); Expect(SUCCEEDED(spool.Close()), tests);
        }
        {
            EncodedSpool spool;
            Expect(spool.Initialize(Session(parent), limits, Format()), tests);
            Expect(!spool.AppendVideo(0, Second, false, payload.data(), 1), tests);
            Expect(spool.AppendVideo(0, Second, true, payload.data(), 1), tests);
            Expect(!spool.AppendVideo(2 * Second, Second, true, payload.data(), 1), tests);
            Expect(!spool.AppendVideo((std::numeric_limits<MediaTime>::max)(), 1, true, payload.data(), 1), tests);
            Expect(spool.AppendVideo(Second, Second, false, payload.data(), 1), tests);
            Expect(spool.Result().committedPackets == 2, tests);
            Expect(SUCCEEDED(spool.Close()), tests);
        }
        {
            EncodedSpool spool;
            const auto directory = Session(parent);
            Expect(spool.Initialize(directory, limits, Format()), tests);
            Expect(spool.AppendVideo(0, Second, true, payload.data(), 1), tests);
            std::shared_ptr<const Snapshot> snapshot;
            Expect(spool.Retain(Second, snapshot), tests);
            Expect(spool.DiscardOwnedBuffer() == HRESULT_FROM_WIN32(ERROR_BUSY), tests);
            Expect(ReadAll(*snapshot, Track::Video, 0x5a, tests) == 1, tests);
            HANDLE exported = nullptr;
            const auto name = GuidName() + L".mp4";
            Expect(SUCCEEDED(spool.CreateNewExport(name, exported)), tests);
            Expect(CloseHandle(exported) != FALSE, tests);
            snapshot.reset();
            Expect(SUCCEEDED(spool.DiscardOwnedBuffer()), tests);
            Expect(GetFileAttributesW(directory.c_str()) == INVALID_FILE_ATTRIBUTES && GetLastError() == ERROR_FILE_NOT_FOUND, tests);
            Expect(GetFileAttributesW((parent + L"\\" + name).c_str()) != INVALID_FILE_ATTRIBUTES, tests);
            Expect(spool.Result().closed && spool.Result().accountedFileBytes == 0, tests);
            Expect(SUCCEEDED(spool.Close()), tests);
        }
        return tests;
    }
}
