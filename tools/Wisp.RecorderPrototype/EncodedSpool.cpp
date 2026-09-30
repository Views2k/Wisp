#include "EncodedSpool.h"

#include <winternl.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cwchar>
#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace recorder::spool
{
    namespace
    {
        constexpr std::uint32_t RecordHeaderBytes = 32;
        constexpr MediaTime AudioChunkTime = 2ll * 10000000;
        constexpr MediaTime MaximumAudioDuration = 213334; // 1024 frames at48kHz, rounded up.
        struct Failure { const char* reason; HRESULT hr; bool io; };
        void Require(bool value, const char* reason, HRESULT hr = E_INVALIDARG)
        { if (!value) throw Failure{ reason, hr, false }; }
        void Check(BOOL value, const char* reason)
        { if (!value) throw Failure{ reason, HRESULT_FROM_WIN32(GetLastError()), true }; }
        struct Handle
        {
            HANDLE value = nullptr;
            ~Handle() { if (value) (void)CloseHandle(value); }
            HRESULT Close() noexcept
            {
                if (!value) return S_OK;
                if (!CloseHandle(value)) return HRESULT_FROM_WIN32(GetLastError());
                value = nullptr;
                return S_OK;
            }
        };
        void CloseChecked(Handle& handle)
        {
            const HRESULT hr = handle.Close();
            if (FAILED(hr)) throw Failure{ "spool_handle_close_failed", hr, true };
        }
        void Flush(HANDLE handle) { Check(FlushFileBuffers(handle), "spool_flush_failed"); }

        bool ValidComponent(const std::wstring& path, std::size_t first, std::size_t length) noexcept
        {
            if (!length || length > 255 || path[first + length - 1] == L'.' || path[first + length - 1] == L' ')
                return false;
            for (std::size_t i = first; i < first + length; ++i)
                if (path[i] < 32 || path[i] == L'/' || path[i] == L':' || path[i] == L'"' ||
                    path[i] == L'<' || path[i] == L'>' || path[i] == L'|' || path[i] == L'?' || path[i] == L'*') return false;
            // NT relative names would bypass DOS reserved-name interpretation. Refuse it explicitly.
            std::size_t stem = 0;
            while (stem < length && path[first + stem] != L'.') ++stem;
            const auto is = [&](const wchar_t* text)
            { return stem == wcslen(text) && _wcsnicmp(path.c_str() + first, text, stem) == 0; };
            if (is(L"CON") || is(L"PRN") || is(L"AUX") || is(L"NUL") || is(L"CONIN$") || is(L"CONOUT$")) return false;
            if (stem == 4 && (_wcsnicmp(path.c_str() + first, L"COM", 3) == 0 ||
                _wcsnicmp(path.c_str() + first, L"LPT", 3) == 0) && path[first + 3] >= L'1' && path[first + 3] <= L'9') return false;
            return true;
        }
        void OpenNative(Handle& out, HANDLE parent, const std::wstring& name, ACCESS_MASK access,
            ULONG sharing, ULONG disposition, bool directory)
        {
            Require(!out.value && !name.empty() && name.size() <= 30004, "spool_native_name_invalid");
            if (parent)
                Require(name.find_first_of(L"\\/:") == std::wstring::npos && ValidComponent(name, 0, name.size()),
                    "spool_relative_name_invalid");
            UNICODE_STRING string{};
            string.Buffer = const_cast<PWSTR>(name.data());
            string.Length = static_cast<USHORT>(name.size() * sizeof(wchar_t));
            string.MaximumLength = string.Length;
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes, &string, OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE, parent, nullptr);
            IO_STATUS_BLOCK status{};
            const NTSTATUS result = NtCreateFile(&out.value, access | SYNCHRONIZE, &attributes, &status,
                nullptr, directory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, sharing, disposition,
                FILE_SYNCHRONOUS_IO_NONALERT | (directory ? FILE_DIRECTORY_FILE : FILE_NON_DIRECTORY_FILE), nullptr, 0);
            if (result < 0)
            {
                out.value = nullptr;
                throw Failure{ "spool_native_open_failed", HRESULT_FROM_NT(result), true };
            }
            FILE_ATTRIBUTE_TAG_INFO info{};
            Check(GetFileInformationByHandleEx(out.value, FileAttributeTagInfo, &info, sizeof(info)), "spool_attributes_failed");
            Require((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0 &&
                ((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == directory, "spool_reparse_or_type_rejected");
        }
        void Seek(HANDLE file, std::uint64_t offset)
        {
            Require(offset <= static_cast<std::uint64_t>((std::numeric_limits<LONGLONG>::max)()), "spool_offset_invalid");
            LARGE_INTEGER position{}; position.QuadPart = static_cast<LONGLONG>(offset);
            Check(SetFilePointerEx(file, position, nullptr, FILE_BEGIN), "spool_seek_failed");
        }
        void RequireLocalVolume(HANDLE parent)
        {
            // Volume GUID paths do not exist for network shares. Query the held
            // object, not a drive-letter mapping that could change after open.
            // The private path is never returned in evidence or diagnostics.
            std::array<wchar_t, 32768> path{};
            const DWORD length = GetFinalPathNameByHandleW(parent, path.data(), static_cast<DWORD>(path.size()),
                FILE_NAME_OPENED | VOLUME_NAME_GUID);
            if (!length) throw Failure{ "spool_local_volume_unverified", HRESULT_FROM_WIN32(GetLastError()), false };
            Require(length < path.size() && length >= 49 && wcsncmp(path.data(), L"\\\\?\\Volume{", 11) == 0 &&
                path[47] == L'}' && path[48] == L'\\', "spool_local_volume_unverified");
        }
        void OpenSessionParent(Handle& out, const std::wstring& path)
        {
            Require(ValidateSessionPath(path), "spool_session_path_invalid");
            const auto split = path.find_last_of(L'\\');
            const auto parent = path.substr(0, split == 2 ? 3 : split);
            OpenNative(out, nullptr, L"\\??\\" + parent,
                FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY,
                FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_OPEN, true);
            RequireLocalVolume(out.value);
        }
        void Write(HANDLE file, const void* data, DWORD bytes)
        {
            DWORD done = 0;
            Check(WriteFile(file, data, bytes, &done, nullptr), "spool_write_failed");
            if (done != bytes) throw Failure{ "spool_short_write", HRESULT_FROM_WIN32(ERROR_WRITE_FAULT), true };
        }
        void Read(HANDLE file, void* data, DWORD bytes)
        {
            DWORD done = 0;
            Check(ReadFile(file, data, bytes, &done, nullptr), "spool_read_failed");
            if (done != bytes) throw Failure{ "spool_short_read", HRESULT_FROM_WIN32(ERROR_HANDLE_EOF), true };
        }
        template<class T> void Put(std::uint8_t* out, T value) noexcept
        { for (std::size_t i = 0; i < sizeof(T); ++i) { out[i] = static_cast<std::uint8_t>(value & 255); value >>= 8; } }
        struct Record
        {
            MediaTime time = 0, duration = 0;
            std::uint64_t offset = 0;
            std::uint32_t bytes = 0;
            bool clean = false;
        };
        std::array<std::uint8_t, RecordHeaderBytes> Header(Track track, const Record& record) noexcept
        {
            std::array<std::uint8_t, RecordHeaderBytes> bytes{};
            bytes[0] = 'W'; bytes[1] = 'S'; bytes[2] = 'P'; bytes[3] = '1';
            bytes[4] = 1; bytes[5] = track == Track::Video ? 1 : 2; bytes[6] = record.clean ? 1 : 0;
            Put(bytes.data() + 8, static_cast<std::uint64_t>(record.time));
            Put(bytes.data() + 16, static_cast<std::uint64_t>(record.duration));
            Put(bytes.data() + 24, record.bytes);
            return bytes;
        }
        bool ValidTime(MediaTime time, MediaTime duration) noexcept
        { return time >= 0 && duration > 0 && time <= (std::numeric_limits<MediaTime>::max)() - duration; }
        struct File
        {
            Handle handle, ownership;
            std::wstring name;
            FILE_ID_INFO identity{};
            Track track = Track::Video;
            bool configuration = false, sealed = false, retired = false;
            std::uint64_t chargedBytes = 0, committedBytes = 0;
            MediaTime first = 0, end = 0;
            std::atomic<std::uint32_t> pins{ 0 };
            std::vector<Record> records; // Writer only; snapshots copy immutable offsets/limits.
        };
        struct State
        {
            Handle parent, directory;
            Limits limits{};
            Configuration configuration;
            std::wstring session;
            std::vector<std::shared_ptr<File>> files;
            std::shared_ptr<File> video, audio;
            std::atomic<bool> snapshotActive{ false }, ioFailed{ false };
            std::atomic<std::uint32_t> snapshotRecords{ 0 };
            std::uint32_t records = 0;
            std::uint64_t bytes = 0, nextFile = 1, committedPackets = 0;
            MediaTime videoEnd = 0, audioEnd = 0, lastAudioTime = 0;
            MediaTime lastCapacityEvictedGop = 0;
            bool capacityEvicted = false;
            bool haveVideo = false, haveAudio = false;
        };
        struct LocatedRecord
        {
            std::shared_ptr<File> file;
            Record record;
            std::uint64_t committedLimit = 0;
        };
        void Seal(const std::shared_ptr<File>& file)
        { if (file && !file->sealed) { Flush(file->handle.value); file->sealed = true; } }
        void Retire(State& state, const std::shared_ptr<File>& file)
        {
            if (file->retired) return;
            Require(file->sealed && !file->configuration, "spool_active_file_retirement_rejected");
            state.records -= static_cast<std::uint32_t>(file->records.size());
            std::vector<Record>().swap(file->records);
            file->retired = true;
        }
        void Reap(State& state)
        {
            for (auto it = state.files.begin(); it != state.files.end();)
            {
                const auto& file = *it;
                if (!file->retired || file->pins.load() != 0) { ++it; continue; }
                FILE_DISPOSITION_INFO disposition{ TRUE };
                Check(SetFileInformationByHandle(file->handle.value, FileDispositionInfo, &disposition, sizeof(disposition)),
                    "spool_owned_delete_failed");
                // Only this tracked handle is deleted; readers pin before opening.
                // Bytes stay charged if disposition or close fails.
                CloseChecked(file->handle);
                Check(SetFileInformationByHandle(file->ownership.value, FileDispositionInfo, &disposition, sizeof(disposition)),
                    "spool_ownership_delete_failed");
                CloseChecked(file->ownership);
                state.bytes -= file->chargedBytes;
                it = state.files.erase(it);
            }
        }
        MediaTime EarliestVideo(const State& state) noexcept
        {
            for (const auto& file : state.files)
                if (!file->configuration && file->track == Track::Video && !file->retired && !file->records.empty()) return file->first;
            return state.videoEnd;
        }
        void RetireAudioBefore(State& state, MediaTime boundary)
        {
            for (const auto& file : state.files)
                if (!file->configuration && file->track == Track::Audio && !file->retired && file->sealed && file->end <= boundary)
                    Retire(state, file);
        }
        void TrimDuration(State& state, MediaTime incomingEnd, bool nextVideoGop)
        {
            const MediaTime cutoff = incomingEnd > state.limits.maximumDuration100ns ? incomingEnd - state.limits.maximumDuration100ns : 0;
            for (const auto& file : state.files)
            {
                if (file->configuration || file->retired || file->track != Track::Video || !file->sealed || file->first >= cutoff) continue;
                if (file != state.video || nextVideoGop) Retire(state, file);
            }
            const MediaTime audioBoundary = state.haveVideo ? EarliestVideo(state) : cutoff;
            RetireAudioBefore(state, audioBoundary);
            Reap(state);
        }
        bool Fits(const State& state, std::uint64_t bytes, bool newFile, std::uint32_t records) noexcept
        {
            return bytes <= state.limits.maximumFileBytes - state.bytes &&
                (state.files.size() + (newFile ? 1u : 0u)) * 2 <= state.limits.maximumFiles &&
                static_cast<std::uint64_t>(state.records) + state.snapshotRecords.load() + records <= state.limits.maximumRecords;
        }
        void MakeRoom(State& state, std::uint64_t bytes, bool newFile, bool nextVideoGop)
        {
            Reap(state);
            while (!Fits(state, bytes, newFile, 1))
            {
                std::shared_ptr<File> oldest;
                for (const auto& file : state.files)
                    if (!file->configuration && !file->retired && file->track == Track::Video && file->sealed &&
                        (file != state.video || nextVideoGop)) { oldest = file; break; }
                if (!oldest) throw Failure{ "spool_capacity_reached", HRESULT_FROM_WIN32(ERROR_DISK_FULL), false };
                state.lastCapacityEvictedGop = oldest->first; state.capacityEvicted = true;
                Retire(state, oldest);
                RetireAudioBefore(state, EarliestVideo(state));
                Reap(state);
            }
        }
        std::shared_ptr<File> CreateFile(State& state, Track track, bool configuration = false)
        {
            Require((state.files.size() + 1) * 2 <= state.limits.maximumFiles, "spool_file_limit");
            Require(OwnershipRecordBytes <= state.limits.maximumFileBytes - state.bytes, "spool_ownership_exceeds_quota");
            auto file = std::make_shared<File>();
            file->track = track; file->configuration = configuration;
            if (configuration) file->name = L"configuration.bin";
            else
            {
                Require(state.nextFile != (std::numeric_limits<std::uint64_t>::max)(), "spool_file_sequence_exhausted");
                std::array<wchar_t, 32> name{};
                const int count = swprintf_s(name.data(), name.size(), L"%c%016llx.bin",
                    track == Track::Video ? L'v' : L'a', static_cast<unsigned long long>(state.nextFile++));
                Require(count > 0, "spool_file_name_failed");
                file->name = name.data();
            }
            // Reserve metadata before creating anything. A created file is always tracked.
            state.files.push_back(file);
            OpenNative(file->handle, state.directory.value, file->name,
                FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | DELETE, FILE_SHARE_READ, FILE_CREATE, false);
            Check(GetFileInformationByHandleEx(file->handle.value, FileIdInfo, &file->identity, sizeof(file->identity)),
                "spool_file_identity_failed");
            // WSCOWN01 fixed record, not a path or a mutable session-wide log.
            // A partial record or a file created before proof commits is left
            // unproven after a crash and must be preserved by orphan cleanup.
            std::array<std::uint8_t, OwnershipRecordBytes> ownership{};
            std::memcpy(ownership.data(), "WSCOWN01", 8);
            Put(ownership.data() + 8, std::uint32_t{ 1 });
            Put(ownership.data() + 12, OwnershipRecordBytes);
            Require(state.session.size() == 32 && file->name.size() < 32, "spool_ownership_name_invalid");
            for (std::size_t index = 0; index < state.session.size(); ++index)
                ownership[16 + index] = static_cast<std::uint8_t>(state.session[index]);
            Put(ownership.data() + 48, file->identity.VolumeSerialNumber);
            std::memcpy(ownership.data() + 56, file->identity.FileId.Identifier, sizeof(file->identity.FileId.Identifier));
            for (std::size_t index = 0; index < file->name.size(); ++index)
                ownership[72 + index] = static_cast<std::uint8_t>(file->name[index]);
            OpenNative(file->ownership, state.directory.value, file->name + L".owner",
                FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | DELETE, FILE_SHARE_READ, FILE_CREATE, false);
            file->chargedBytes += OwnershipRecordBytes; state.bytes += OwnershipRecordBytes;
            Write(file->ownership.value, ownership.data(), OwnershipRecordBytes);
            Flush(file->ownership.value);
            return file;
        }
    }

    const char* ValidateLimits(const Limits& value) noexcept
    {
        if (value.maximumDuration100ns <= 0 || value.maximumDuration100ns > MaximumDuration) return "spool_duration_invalid";
        if (value.maximumFileBytes < 4096 || value.maximumFileBytes > 64ull * 1024 * 1024 * 1024) return "spool_byte_limit_invalid";
        if (value.maximumRecords < 16 || value.maximumRecords > 200000) return "spool_record_limit_invalid";
        if (value.maximumFiles < 4 || value.maximumFiles > 4096) return "spool_file_limit_invalid";
        return nullptr;
    }
    bool ValidateSessionPath(const std::wstring& path) noexcept
    {
        constexpr wchar_t prefix[] = L".wisp-recorder-";
        constexpr std::size_t prefixLength = (sizeof(prefix) / sizeof(wchar_t)) - 1;
        if (path.size() < 3 + prefixLength + 32 || path.size() > 30000 ||
            !((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) ||
            path[1] != L':' || path[2] != L'\\') return false;
        std::size_t first = 3;
        while (first < path.size())
        {
            const auto slash = path.find(L'\\', first);
            const auto length = (slash == std::wstring::npos ? path.size() : slash) - first;
            if (!ValidComponent(path, first, length)) return false;
            if (slash == std::wstring::npos) break;
            first = slash + 1;
        }
        if (first >= path.size() || path.size() - first != prefixLength + 32 || path.compare(first, prefixLength, prefix) != 0) return false;
        bool nonzero = false;
        for (std::size_t i = first + prefixLength; i < path.size(); ++i)
        {
            const wchar_t c = path[i];
            if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) return false;
            nonzero = nonzero || c != L'0';
        }
        return nonzero;
    }
    bool ValidateExportName(const std::wstring& name) noexcept
    {
        if (name.size() != 36 || name.compare(32, 4, L".mp4") != 0) return false;
        bool nonzero = false;
        for (std::size_t i = 0; i < 32; ++i)
        {
            const wchar_t c = name[i];
            if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) return false;
            nonzero = nonzero || c != L'0';
        }
        return nonzero;
    }

    struct Snapshot::Impl
    {
        std::shared_ptr<State> state;
        Bounds bounds{};
        std::vector<LocatedRecord> video, audio;
        std::vector<std::shared_ptr<File>> pinned;
        std::atomic<bool> cursors[2]{ { false }, { false } };
        bool charged = false;
        ~Impl()
        {
            if (!charged) return;
            for (const auto& file : pinned) --file->pins;
            state->snapshotRecords.fetch_sub(static_cast<std::uint32_t>(video.size() + audio.size()));
            state->snapshotActive.store(false);
        }
    };
    struct PacketCursor::Impl
    {
        std::shared_ptr<Snapshot::Impl> snapshot;
        Track track = Track::Video;
        std::size_t position = 0;
        std::shared_ptr<File> current;
        Handle reader;
        const char* reason = "cursor_ready";
        HRESULT hr = S_OK;
        bool closed = false;
    };
    Snapshot::Snapshot(std::shared_ptr<Impl> value) : impl_(std::move(value)) {}
    Snapshot::~Snapshot() = default;
    const Configuration& Snapshot::Format() const noexcept { return impl_->state->configuration; }
    const Bounds& Snapshot::Range() const noexcept { return impl_->bounds; }
    HRESULT Snapshot::OpenCursor(Track track, std::unique_ptr<PacketCursor>& output) const noexcept
    {
        if (output || (track != Track::Video && track != Track::Audio)) return E_INVALIDARG;
        const auto index = track == Track::Video ? 0u : 1u;
        bool expected = false;
        if (!impl_->cursors[index].compare_exchange_strong(expected, true)) return HRESULT_FROM_WIN32(ERROR_BUSY);
        try
        {
            auto cursor = std::make_unique<PacketCursor::Impl>();
            cursor->snapshot = impl_; cursor->track = track;
            output.reset(new PacketCursor(std::move(cursor)));
            return S_OK;
        }
        catch (...) { impl_->cursors[index].store(false); return E_OUTOFMEMORY; }
    }
    PacketCursor::PacketCursor(std::unique_ptr<Impl> value) : impl_(std::move(value)) {}
    PacketCursor::~PacketCursor() { (void)Close(); }
    HRESULT PacketCursor::Close() noexcept
    {
        if (!impl_ || impl_->closed) return impl_ ? impl_->hr : S_OK;
        const HRESULT hr = impl_->reader.Close();
        if (FAILED(hr)) { impl_->reason = "spool_cursor_close_failed"; impl_->hr = hr; return hr; }
        impl_->current.reset();
        impl_->snapshot->cursors[impl_->track == Track::Video ? 0 : 1].store(false);
        impl_->snapshot.reset();
        impl_->closed = true;
        return impl_->hr;
    }
    const char* PacketCursor::Reason() const noexcept { return impl_ ? impl_->reason : "cursor_missing"; }
    HRESULT PacketCursor::Error() const noexcept { return impl_ ? impl_->hr : E_UNEXPECTED; }
    ReadResult PacketCursor::Next(Packet& output) noexcept
    {
        output.bytes.clear();
        if (!impl_ || FAILED(impl_->hr)) return ReadResult::Failed;
        if (impl_->closed) return ReadResult::End;
        try
        {
            auto& value = *impl_;
            const auto& records = value.track == Track::Video ? value.snapshot->video : value.snapshot->audio;
            if (value.position == records.size())
            {
                if (FAILED(Close())) return ReadResult::Failed;
                return ReadResult::End;
            }
            const auto& item = records[value.position];
            if (value.current != item.file)
            {
                CloseChecked(value.reader);
                OpenNative(value.reader, value.snapshot->state->directory.value, item.file->name,
                    FILE_READ_DATA | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, FILE_OPEN, false);
                FILE_ID_INFO identity{};
                Check(GetFileInformationByHandleEx(value.reader.value, FileIdInfo, &identity, sizeof(identity)), "spool_reader_identity_failed");
                Require(identity.VolumeSerialNumber == item.file->identity.VolumeSerialNumber &&
                    memcmp(identity.FileId.Identifier, item.file->identity.FileId.Identifier, sizeof(identity.FileId.Identifier)) == 0,
                    "spool_reader_identity_changed");
                value.current = item.file;
            }
            const auto& record = item.record;
            Require(record.bytes <= MaximumPacketBytes && record.offset <= item.committedLimit &&
                RecordHeaderBytes + static_cast<std::uint64_t>(record.bytes) <= item.committedLimit - record.offset,
                "spool_snapshot_boundary_invalid");
            Seek(value.reader.value, record.offset);
            std::array<std::uint8_t, RecordHeaderBytes> header{};
            Read(value.reader.value, header.data(), static_cast<DWORD>(header.size()));
            Require(header == Header(value.track, record), "spool_record_header_changed");
            output.bytes.resize(record.bytes);
            Read(value.reader.value, output.bytes.data(), record.bytes);
            output.timestamp100ns = record.time; output.duration100ns = record.duration; output.cleanPoint = record.clean;
            ++value.position;
            return ReadResult::Packet;
        }
        catch (const Failure& error) { impl_->reason = error.reason; impl_->hr = error.hr; impl_->snapshot->state->ioFailed.store(true); }
        catch (const std::bad_alloc&) { impl_->reason = "spool_reader_allocation_failed"; impl_->hr = E_OUTOFMEMORY; }
        catch (...) { impl_->reason = "spool_reader_failed"; impl_->hr = E_FAIL; }
        output.bytes.clear();
        return ReadResult::Failed;
    }

    struct EncodedSpool::Impl { DWORD owner = GetCurrentThreadId(); std::shared_ptr<State> state = std::make_shared<State>(); };
    EncodedSpool::EncodedSpool() noexcept = default;
    EncodedSpool::~EncodedSpool() { (void)Close(); }
    HRESULT PreflightSessionParent(const std::wstring& path) noexcept
    {
        Handle parent;
        HRESULT result = S_OK;
        try { OpenSessionParent(parent, path); }
        catch (const Failure& failure) { result = failure.hr; }
        catch (const std::bad_alloc&) { result = E_OUTOFMEMORY; }
        catch (...) { result = E_FAIL; }
        const auto closed = parent.Close();
        return FAILED(closed) ? closed : result;
    }
    bool EncodedSpool::Initialize(const std::wstring& path, const Limits& limits, const Configuration& configuration) noexcept
    {
        if (impl_ || evidence_.closed) { evidence_.reason = "spool_not_fresh"; evidence_.hr = E_UNEXPECTED; return false; }
        try
        {
            if (const char* reason = ValidateLimits(limits)) throw Failure{ reason, E_INVALIDARG, false };
            Require(ValidateSessionPath(path), "spool_session_path_invalid");
            Require(configuration.epoch && !configuration.h264SequenceHeader.empty() && configuration.h264SequenceHeader.size() <= 65536 &&
                configuration.aacUserData.size() <= 1024, "spool_configuration_invalid");
            const std::uint64_t configurationBytes = 24 + configuration.h264SequenceHeader.size() + configuration.aacUserData.size();
            Require(configurationBytes + OwnershipRecordBytes <= limits.maximumFileBytes, "spool_configuration_exceeds_quota");
            impl_ = std::make_unique<Impl>();
            auto& state = *impl_->state;
            state.limits = limits; state.configuration = configuration;
            state.session = path.substr(path.size() - 32);
            const auto split = path.find_last_of(L'\\');
            OpenSessionParent(state.parent, path);
            OpenNative(state.directory, state.parent.value, path.substr(split + 1),
                FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | FILE_ADD_FILE | DELETE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_CREATE, true);
            auto file = CreateFile(state, Track::Video, true);
            file->chargedBytes += configurationBytes; state.bytes += configurationBytes;
            std::array<std::uint8_t, 24> header{};
            header[0] = 'W'; header[1] = 'S'; header[2] = 'C'; header[3] = '1';
            Put(header.data() + 8, configuration.epoch);
            Put(header.data() + 16, static_cast<std::uint32_t>(configuration.h264SequenceHeader.size()));
            Put(header.data() + 20, static_cast<std::uint32_t>(configuration.aacUserData.size()));
            Write(file->handle.value, header.data(), static_cast<DWORD>(header.size()));
            Write(file->handle.value, configuration.h264SequenceHeader.data(), static_cast<DWORD>(configuration.h264SequenceHeader.size()));
            if (!configuration.aacUserData.empty())
                Write(file->handle.value, configuration.aacUserData.data(), static_cast<DWORD>(configuration.aacUserData.size()));
            Flush(file->handle.value); file->committedBytes = configurationBytes; file->sealed = true;
            evidence_.initialized = true; evidence_.reason = "spool_initialized";
            return true;
        }
        catch (const Failure& error) { evidence_.reason = error.reason; evidence_.hr = error.hr; evidence_.poisoned = error.io; }
        catch (const std::bad_alloc&) { evidence_.reason = "spool_allocation_failed"; evidence_.hr = E_OUTOFMEMORY; }
        catch (...) { evidence_.reason = "spool_initialization_failed"; evidence_.hr = E_FAIL; }
        return false;
    }
    bool EncodedSpool::AppendVideo(MediaTime time, MediaTime duration, bool clean, const std::uint8_t* bytes, std::size_t size) noexcept
    { return Append(Track::Video, time, duration, clean, bytes, size); }
    bool EncodedSpool::AppendAudio(MediaTime time, MediaTime duration, const std::uint8_t* bytes, std::size_t size) noexcept
    { return Append(Track::Audio, time, duration, false, bytes, size); }
    bool EncodedSpool::Append(Track track, MediaTime time, MediaTime duration, bool clean, const std::uint8_t* bytes, std::size_t size) noexcept
    {
        try
        {
            Require(impl_ && evidence_.initialized && !evidence_.closed && !evidence_.poisoned && !impl_->state->ioFailed.load(),
                "spool_not_writable", E_UNEXPECTED);
            Require(impl_->owner == GetCurrentThreadId(), "spool_wrong_writer", E_UNEXPECTED);
            auto& state = *impl_->state;
            Require(state.committedPackets != (std::numeric_limits<std::uint64_t>::max)(), "spool_packet_count_exhausted");
            Require(bytes && size && size <= MaximumPacketBytes && (track == Track::Video || size <= 65536), "spool_packet_size_invalid");
            Require(ValidTime(time, duration), "spool_packet_time_invalid");
            const bool video = track == Track::Video;
            Require(video || !state.configuration.aacUserData.empty(), "spool_audio_not_configured");
            Require(!video || (state.haveVideo ? time == state.videoEnd : clean), "spool_video_discontinuity_or_keyframe_required");
            Require(video || (duration >= 213333 && duration <= MaximumAudioDuration), "spool_audio_duration_invalid");
            Require(video || !state.haveAudio || (time > state.lastAudioTime && time == state.audioEnd), "spool_audio_discontinuity");
            auto& current = video ? state.video : state.audio;
            const bool newFile = !current || (video ? clean : time - current->first >= AudioChunkTime);
            if (newFile) Seal(current);
            TrimDuration(state, video ? time + duration : (state.haveVideo ? state.videoEnd : time + duration), video && newFile);
            const std::uint64_t addedBytes = RecordHeaderBytes + size;
            MakeRoom(state, addedBytes + (newFile ? OwnershipRecordBytes : 0), newFile, video && newFile);
            if (newFile) current = CreateFile(state, track);
            Require(current && !current->retired && !current->sealed, "spool_current_file_invalid");
            const Record record{ time, duration, current->committedBytes, static_cast<std::uint32_t>(size), clean };
            current->records.push_back(record); // Allocate before writing; failed tails never become committed records.
            try
            {
                current->chargedBytes += addedBytes; state.bytes += addedBytes;
                Seek(current->handle.value, record.offset);
                const auto header = Header(track, record);
                Write(current->handle.value, header.data(), static_cast<DWORD>(header.size()));
                Write(current->handle.value, bytes, static_cast<DWORD>(size));
            }
            catch (...) { current->records.pop_back(); throw; }
            current->committedBytes += addedBytes;
            if (current->records.size() == 1) current->first = time;
            current->end = time + duration;
            ++state.records; ++state.committedPackets;
            if (video) { state.videoEnd = time + duration; state.haveVideo = true; }
            else { state.audioEnd = time + duration; state.lastAudioTime = time; state.haveAudio = true; }
            evidence_.reason = "spool_packet_committed"; evidence_.hr = S_OK;
            return true;
        }
        catch (const Failure& error) { evidence_.reason = error.reason; evidence_.hr = error.hr; evidence_.poisoned |= error.io; }
        catch (const std::bad_alloc&) { evidence_.reason = "spool_allocation_failed"; evidence_.hr = E_OUTOFMEMORY; }
        catch (...) { evidence_.reason = "spool_append_failed"; evidence_.hr = E_FAIL; evidence_.poisoned = true; }
        return false;
    }
    bool EncodedSpool::Retain(MediaTime span, std::shared_ptr<const Snapshot>& output) noexcept
    {
        try
        {
            Require(impl_ && evidence_.initialized && !evidence_.closed, "spool_not_readable", E_UNEXPECTED);
            Require(impl_->owner == GetCurrentThreadId(), "spool_wrong_writer", E_UNEXPECTED);
            auto& state = *impl_->state;
            Require(!output && !state.snapshotActive.load(), "spool_snapshot_exists", HRESULT_FROM_WIN32(ERROR_BUSY));
            Require(span > 0 && span <= state.limits.maximumDuration100ns, "spool_requested_span_invalid");
            const bool withAudio = !state.configuration.aacUserData.empty();
            Require(state.haveVideo && (!withAudio || state.haveAudio), "spool_no_complete_frames", HRESULT_FROM_WIN32(ERROR_NO_DATA));
            const MediaTime availableEnd = withAudio ? (std::min)(state.videoEnd, state.audioEnd) : state.videoEnd;
            MediaTime end = 0, audioStart = 0;
            bool foundAudio = false;
            for (const auto& file : state.files)
            {
                if (file->configuration || file->retired) continue;
                for (const auto& record : file->records)
                    if (file->track == Track::Video && record.time + record.duration <= availableEnd) end = record.time + record.duration;
                    else if (file->track == Track::Audio && !foundAudio) { audioStart = record.time; foundAudio = true; }
            }
            Require(end > 0, "spool_no_complete_frames", HRESULT_FROM_WIN32(ERROR_NO_DATA));
            const MediaTime cutoff = (std::max)(end > span ? end - span : 0, withAudio ? audioStart : 0);
            Require(!state.capacityEvicted || state.lastCapacityEvictedGop < cutoff,
                "spool_requested_history_evicted_by_capacity", HRESULT_FROM_WIN32(ERROR_NO_DATA));
            MediaTime start = 0; bool found = false;
            std::uint32_t videoCount = 0, audioCount = 0;
            for (const auto& file : state.files)
            {
                if (file->configuration || file->retired || file->track != Track::Video) continue;
                for (const auto& record : file->records)
                {
                    if (!found && record.clean && record.time >= cutoff && record.time < end) { start = record.time; found = true; }
                    if (found && record.time >= start && record.time + record.duration <= end) ++videoCount;
                }
            }
            Require(found && videoCount, "spool_no_keyframe_in_requested_span", HRESULT_FROM_WIN32(ERROR_NO_DATA));
            if (withAudio)
                for (const auto& file : state.files)
                    if (!file->configuration && !file->retired && file->track == Track::Audio)
                        for (const auto& record : file->records) if (record.time >= start && record.time < end) ++audioCount;
            Require(static_cast<std::uint64_t>(state.records) + videoCount + audioCount <= state.limits.maximumRecords,
                "spool_snapshot_metadata_limit", HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_MEMORY));
            auto snapshot = std::make_shared<Snapshot::Impl>();
            snapshot->state = impl_->state; snapshot->video.reserve(videoCount); snapshot->audio.reserve(audioCount);
            snapshot->pinned.reserve(state.files.size());
            snapshot->bounds.start100ns = start; snapshot->bounds.end100ns = end;
            for (const auto& file : state.files)
            {
                if (file->configuration || file->retired) continue;
                bool included = false;
                for (const auto& record : file->records)
                {
                    if (file->track == Track::Video ? record.time >= start && record.time + record.duration <= end :
                        withAudio && record.time >= start && record.time < end)
                    {
                        auto& target = file->track == Track::Video ? snapshot->video : snapshot->audio;
                        target.push_back({ file, record, file->committedBytes }); included = true;
                    }
                }
                if (included) { Flush(file->handle.value); snapshot->pinned.push_back(file); }
            }
            Require(snapshot->video.size() == videoCount && snapshot->audio.size() == audioCount, "spool_snapshot_count_changed");
            snapshot->bounds.videoPackets = videoCount; snapshot->bounds.audioPackets = audioCount;
            if (withAudio)
            {
                Require(!snapshot->audio.empty(), "spool_audio_coverage_missing");
                snapshot->bounds.audioStart100ns = snapshot->audio.front().record.time;
                const auto& last = snapshot->audio.back().record;
                snapshot->bounds.audioEnd100ns = last.time + last.duration;
                Require(snapshot->bounds.audioStart100ns >= start && snapshot->bounds.audioStart100ns - start <= MaximumAudioDuration &&
                    snapshot->bounds.audioEnd100ns >= end && snapshot->bounds.audioEnd100ns - end <= MaximumAudioDuration,
                    "spool_audio_coverage_missing");
            }
            std::shared_ptr<const Snapshot> retained(new Snapshot(snapshot));
            for (const auto& file : snapshot->pinned) ++file->pins;
            state.snapshotRecords.fetch_add(videoCount + audioCount); snapshot->charged = true;
            state.snapshotActive.store(true);
            output = std::move(retained);
            evidence_.reason = "spool_snapshot_retained"; evidence_.hr = S_OK;
            return true;
        }
        catch (const Failure& error) { evidence_.reason = error.reason; evidence_.hr = error.hr; evidence_.poisoned |= error.io; }
        catch (const std::bad_alloc&) { evidence_.reason = "spool_snapshot_allocation_failed"; evidence_.hr = E_OUTOFMEMORY; }
        catch (...) { evidence_.reason = "spool_snapshot_failed"; evidence_.hr = E_FAIL; }
        return false;
    }
    HRESULT EncodedSpool::CreateNewExport(const std::wstring& name, HANDLE& output) noexcept
    {
        if (output || !impl_ || !evidence_.initialized || evidence_.closed ||
            impl_->owner != GetCurrentThreadId() || !ValidateExportName(name)) return E_INVALIDARG;
        try
        {
            Handle handle;
            OpenNative(handle, impl_->state->parent.value, name, FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ, FILE_CREATE, false);
            output = handle.value; handle.value = nullptr;
            return S_OK;
        }
        catch (const Failure& error) { return error.hr; }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }
    HRESULT EncodedSpool::DiscardOwnedBuffer() noexcept
    {
        if (!impl_ || !evidence_.initialized || evidence_.closed ||
            impl_->owner != GetCurrentThreadId()) return E_UNEXPECTED;
        auto& state = *impl_->state;
        if (state.snapshotActive.load()) return HRESULT_FROM_WIN32(ERROR_BUSY);
        if (evidence_.poisoned || state.ioFailed.load()) return E_UNEXPECTED;
        for (const auto& file : state.files)
            if (!file->handle.value || !file->ownership.value || file->pins.load() != 0) return HRESULT_FROM_WIN32(ERROR_BUSY);
        try
        {
            for (const auto& file : state.files)
            {
                FILE_DISPOSITION_INFO disposition{ TRUE };
                Check(SetFileInformationByHandle(file->handle.value, FileDispositionInfo, &disposition, sizeof(disposition)),
                    "spool_discard_file_failed");
                CloseChecked(file->handle);
                Check(SetFileInformationByHandle(file->ownership.value, FileDispositionInfo, &disposition, sizeof(disposition)),
                    "spool_discard_ownership_failed");
                CloseChecked(file->ownership);
            }
            // No enumeration or recursive removal: an unexpected untracked
            // child causes this exact directory deletion to fail closed.
            FILE_DISPOSITION_INFO disposition{ TRUE };
            Check(SetFileInformationByHandle(state.directory.value, FileDispositionInfo, &disposition, sizeof(disposition)),
                "spool_discard_directory_failed");
            CloseChecked(state.directory);
            CloseChecked(state.parent);
            state.files.clear(); state.video.reset(); state.audio.reset();
            state.bytes = 0; state.records = 0;
            evidence_.closed = true; evidence_.reason = "spool_discarded";
            return S_OK;
        }
        catch (const Failure& error)
        {
            evidence_.reason = error.reason; evidence_.hr = error.hr;
            evidence_.poisoned = true; evidence_.cleanupHr = error.hr;
            return error.hr;
        }
        catch (...)
        {
            evidence_.reason = "spool_discard_failed"; evidence_.hr = E_FAIL;
            evidence_.poisoned = true; evidence_.cleanupHr = E_FAIL;
            return E_FAIL;
        }
    }
    HRESULT EncodedSpool::Close() noexcept
    {
        if (evidence_.closed) return evidence_.cleanupHr;
        if (!impl_) { evidence_.closed = true; return evidence_.cleanupHr; }
        if (impl_->owner != GetCurrentThreadId()) return E_UNEXPECTED;
        auto& state = *impl_->state;
        if (state.snapshotActive.load()) return HRESULT_FROM_WIN32(ERROR_BUSY);
        const auto remember = [this](HRESULT hr) { if (FAILED(hr) && SUCCEEDED(evidence_.cleanupHr)) evidence_.cleanupHr = hr; };
        for (const auto& file : state.files)
        {
            if (file->handle.value && !FlushFileBuffers(file->handle.value)) remember(HRESULT_FROM_WIN32(GetLastError()));
            remember(file->handle.Close());
            remember(file->ownership.Close());
        }
        remember(state.directory.Close()); remember(state.parent.Close());
        evidence_.closed = true;
        return evidence_.cleanupHr;
    }
    Evidence EncodedSpool::Result() const noexcept
    {
        auto result = evidence_;
        if (impl_)
        {
            const auto& state = *impl_->state;
            result.accountedFileBytes = state.bytes; result.committedPackets = state.committedPackets;
            result.ownedFiles = static_cast<std::uint32_t>(state.files.size() * 2); result.rollingRecords = state.records;
            result.snapshotRecords = state.snapshotRecords.load();
            result.poisoned |= state.ioFailed.load();
        }
        return result;
    }
}
