#include "../Mp4ClipWriter.h"
#include <mfapi.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <codecapi.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <array>
#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <iostream>
#include <limits>
#include <memory>
#include <string>
#include <thread>

namespace
{
    using Microsoft::WRL::ComPtr;
    namespace exporting = recorder::exporting;
    constexpr UINT Width = 1280, Height = 720, Frames = 16, Rate = 60;
    constexpr DWORD InputBytes = 15096410;
    constexpr char InputSha256[] = "22985ce5cf9a3a185258d3e972b33a5193ba7cdf4fd2691b48484922b2f1ee5c";
    struct Fixture
    {
        UINT width, height, frames;
        DWORD bytes;
        const char* sha256;
        bool repeatedParameterSets;
        DWORD maximumReadbackBytes;
    };
    constexpr Fixture StandardFixture{Width, Height, Frames, InputBytes, InputSha256, false, 4u * 1024 * 1024};
    constexpr Fixture EntropyFixture{3840, 2160, 2, 39908122,
        "e208d52b7da95c3cbb5aea0e15b3d51eb887fa688f0838ee8139c52289968463", true, 64u * 1024 * 1024};
    struct Failure { const char* reason; HRESULT hr; };
    void Require(bool value, const char* reason) { if (!value) throw Failure{reason, E_FAIL}; }
    void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{reason, hr}; }
    void Win(bool value, const char* reason) { if (!value) throw Failure{reason, HRESULT_FROM_WIN32(GetLastError())}; }
    LONGLONG FrameTime(UINT frame) { return static_cast<LONGLONG>(frame) * 10000000 / Rate; }

    class Handle
    {
    public:
        explicit Handle(HANDLE value = nullptr) : value_(value) {}
        ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        Handle(Handle&& other) noexcept : value_(other.value_) { other.value_ = nullptr; }
        HANDLE Get() const { return value_; }
    private:
        HANDLE value_;
    };
    class Deadline
    {
    public:
        Deadline() : done_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
        {
            Win(done_.Get() != nullptr, "watchdog_event_failed");
            thread_ = std::thread([this] {
                if (WaitForSingleObject(done_.Get(), 15000) != WAIT_OBJECT_0)
                    TerminateProcess(GetCurrentProcess(), 14);
            });
        }
        ~Deadline() { SetEvent(done_.Get()); if (thread_.joinable()) thread_.join(); }
    private:
        Handle done_;
        std::thread thread_;
    };
    void RequireApplicationsClosed()
    {
        Handle processes(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
        Win(processes.Get() != INVALID_HANDLE_VALUE, "process_enumeration_failed");
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        Win(Process32FirstW(processes.Get(), &entry) != FALSE, "process_enumeration_failed");
        do
        {
            Require(_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"Wisp.exe") != 0, "close_forza_and_wisp_before_fixture");
        } while (Process32NextW(processes.Get(), &entry));
        Require(GetLastError() == ERROR_NO_MORE_FILES, "process_enumeration_failed");
    }
    std::wstring LocalPath(const wchar_t* value)
    {
        const std::wstring input(value);
        Require(input.size() > 3 && input.size() < 220 && input[1] == L':' && input[2] == L'\\' &&
            ((input[0] >= L'A' && input[0] <= L'Z') || (input[0] >= L'a' && input[0] <= L'z')) &&
            input.find_first_of(L"\r\n\"<>|?*") == std::wstring::npos && input.find(L':', 2) == std::wstring::npos,
            "absolute_local_path_required");
        wchar_t full[260]{};
        const DWORD count = GetFullPathNameW(input.c_str(), static_cast<DWORD>(std::size(full)), full, nullptr);
        Require(count > 3 && count < std::size(full), "path_resolution_failed");
        std::wstring result(full, count);
        Require(result.back() != L'\\' && result.back() != L'.' && result.back() != L' ', "invalid_path_leaf");
        Require(GetDriveTypeW(result.substr(0, 3).c_str()) == DRIVE_FIXED, "fixed_local_drive_required");
        return result;
    }
    std::vector<Handle> HoldParents(const std::wstring& path)
    {
        std::vector<Handle> held;
        for (size_t end = 2; end != std::wstring::npos; end = path.find(L'\\', end + 1))
        {
            const auto part = path.substr(0, end == 2 ? 3 : end);
            Handle directory(CreateFileW(part.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            Win(directory.Get() != INVALID_HANDLE_VALUE, "parent_directory_open_failed");
            BY_HANDLE_FILE_INFORMATION info{};
            Win(GetFileInformationByHandle(directory.Get(), &info) != FALSE, "parent_directory_stat_failed");
            Require((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
                (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0, "reparse_or_non_directory_parent");
            held.push_back(std::move(directory));
        }
        return held;
    }
    std::string Sha256(const std::vector<BYTE>& bytes)
    {
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, "hash_provider_failed");
        std::array<BYTE, 32> digest{};
        const auto status = BCryptHash(algorithm, nullptr, 0, const_cast<BYTE*>(bytes.data()),
            static_cast<ULONG>(bytes.size()), digest.data(), static_cast<ULONG>(digest.size()));
        const auto closed = BCryptCloseAlgorithmProvider(algorithm, 0);
        Require(status >= 0 && closed >= 0, "hash_failed");
        constexpr char digits[] = "0123456789abcdef";
        std::string text;
        for (const BYTE value : digest) { text.push_back(digits[value >> 4]); text.push_back(digits[value & 15]); }
        return text;
    }
    std::vector<BYTE> ReadPinned(const std::wstring& path, const Fixture& fixture)
    {
        Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
        Win(file.Get() != INVALID_HANDLE_VALUE, "fixture_input_open_failed");
        BY_HANDLE_FILE_INFORMATION info{};
        Win(GetFileInformationByHandle(file.Get(), &info) != FALSE, "fixture_input_stat_failed");
        Require((info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) == 0 &&
            info.nFileSizeHigh == 0 && info.nFileSizeLow == fixture.bytes, "fixture_input_shape_mismatch");
        std::vector<BYTE> bytes(fixture.bytes);
        DWORD actual = 0;
        Win(ReadFile(file.Get(), bytes.data(), fixture.bytes, &actual, nullptr) != FALSE, "fixture_input_read_failed");
        Require(actual == fixture.bytes && Sha256(bytes) == fixture.sha256, "fixture_input_hash_mismatch");
        return bytes;
    }

    struct Nal { size_t start, payload, end; UINT type; };
    size_t Prefix(const std::vector<BYTE>& bytes, size_t at)
    {
        if (at + 3 <= bytes.size() && bytes[at] == 0 && bytes[at + 1] == 0)
        {
            if (bytes[at + 2] == 1) return 3;
            if (at + 4 <= bytes.size() && bytes[at + 2] == 0 && bytes[at + 3] == 1) return 4;
        }
        return 0;
    }
    std::vector<Nal> Nals(const std::vector<BYTE>& bytes)
    {
        std::vector<Nal> nals;
        if (Prefix(bytes, 0))
        {
            size_t at = 0;
            while (at < bytes.size())
            {
                const size_t prefix = Prefix(bytes, at);
                Require(prefix && at + prefix < bytes.size(), "annexb_packet_invalid");
                size_t end = at + prefix + 1;
                while (end < bytes.size() && !Prefix(bytes, end)) ++end;
                nals.push_back({at, at + prefix, end, static_cast<UINT>(bytes[at + prefix] & 31)});
                Require(nals.size() <= 64, "nal_count_exceeded");
                at = end;
            }
        }
        else
        {
            size_t at = 0;
            while (at < bytes.size())
            {
                Require(bytes.size() - at >= 5, "avcc_packet_invalid");
                const UINT length = (static_cast<UINT>(bytes[at]) << 24) | (static_cast<UINT>(bytes[at + 1]) << 16) |
                    (static_cast<UINT>(bytes[at + 2]) << 8) | bytes[at + 3];
                Require(length && length <= bytes.size() - at - 4, "avcc_packet_invalid");
                nals.push_back({at, at + 4, at + 4 + length, static_cast<UINT>(bytes[at + 4] & 31)});
                Require(nals.size() <= 64, "nal_count_exceeded");
                at += 4 + length;
            }
        }
        Require(!nals.empty(), "packet_empty");
        return nals;
    }
    exporting::VideoFormat LosslessFormat(const Fixture& fixture = StandardFixture)
    {
        return {fixture.width, fixture.height, Rate, 0, MFVideoPrimaries_BT709, MFVideoTransFunc_709,
            MFVideoTransferMatrix_Identity, MFNominalRange_0_255, eAVEncH264VProfile_444,
            1, 1, 0, exporting::VideoEncoding::H264LosslessGbr444};
    }
    class Source final : public exporting::PacketSource
    {
    public:
        Source(const std::vector<BYTE>& bytes, const Fixture& fixture) : bytes_(bytes), nals_(Nals(bytes)), fixture_(fixture)
        {
            firstSlice_ = fixture.repeatedParameterSets ? 4u : 2u;
            Require(nals_.size() == fixture.frames + firstSlice_ && nals_[0].type == 7 && nals_[1].type == 8,
                "fixture_nal_layout_mismatch");
            if (fixture.repeatedParameterSets)
                Require(nals_[2].type == 7 && nals_[3].type == 8 &&
                    nals_[2].end - nals_[2].payload == nals_[0].end - nals_[0].payload &&
                    nals_[3].end - nals_[3].payload == nals_[1].end - nals_[1].payload &&
                    std::memcmp(bytes.data() + nals_[2].payload, bytes.data() + nals_[0].payload, nals_[0].end - nals_[0].payload) == 0 &&
                    std::memcmp(bytes.data() + nals_[3].payload, bytes.data() + nals_[1].payload, nals_[1].end - nals_[1].payload) == 0,
                    "fixture_repeated_parameters_mismatch");
            for (UINT frame = 0; frame < fixture.frames; ++frame)
            {
                Require(nals_[frame + firstSlice_].type == (frame == 0 ? 5u : 1u), "fixture_slice_layout_mismatch");
                largestPacket_ = (std::max)(largestPacket_, PacketEnd(frame) - PacketStart(frame));
            }
            if (fixture.repeatedParameterSets) Require(largestPacket_ == 39471729, "fixture_large_packet_mismatch");
            description_.h264SequenceHeader.assign(bytes.begin(), bytes.begin() + nals_[2].start);
            description_.videoPackets = fixture.frames;
            description_.end100ns = FrameTime(fixture.frames);
        }
        const exporting::ClipDescription& Description() const noexcept override { return description_; }
        HRESULT Read(bool audio, exporting::PacketView& packet) noexcept override
        {
            packet = {};
            if (audio || index_ == fixture_.frames) return S_FALSE;
            const size_t start = PacketStart(index_);
            packet = {bytes_.data() + start, PacketEnd(index_) - start, FrameTime(index_),
                FrameTime(index_ + 1) - FrameTime(index_), index_ == 0};
            ++index_;
            return S_OK;
        }
        const Nal& Expected(UINT frame) const { return nals_.at(frame + firstSlice_); }
        const std::vector<BYTE>& Bytes() const { return bytes_; }
        size_t LargestPacket() const { return largestPacket_; }
    private:
        size_t PacketStart(UINT frame) const { return nals_[frame == 0 ? 2u : frame + firstSlice_].start; }
        size_t PacketEnd(UINT frame) const { return nals_[frame + firstSlice_].end; }
        const std::vector<BYTE>& bytes_;
        std::vector<Nal> nals_;
        Fixture fixture_;
        UINT firstSlice_ = 2;
        size_t largestPacket_ = 0;
        exporting::ClipDescription description_{};
        UINT index_ = 0;
    };
    UINT Contracts()
    {
        exporting::ClipDescription description;
        description.h264SequenceHeader = {0, 0, 0, 1, 0x67};
        description.videoPackets = Frames;
        description.end100ns = FrameTime(Frames);
        UINT count = 0;
        const auto test = [&](const exporting::VideoFormat& format, bool expected) {
            Require((exporting::ValidateDescription(description, format) == nullptr) == expected, "format_contract_failed");
            ++count;
        };
        auto format = LosslessFormat();
        test(format, true);
        auto changed = format; changed.encoding = exporting::VideoEncoding::H264Baseline420; test(changed, false);
        changed = format; changed.matrix = MFVideoTransferMatrix_Unknown; test(changed, false);
        changed = format; changed.matrix = MFVideoTransferMatrix_BT709; test(changed, false);
        changed = format; changed.nominalRange = MFNominalRange_16_235; test(changed, false);
        changed = format; changed.profile = eAVEncH264VProfile_High; test(changed, false);
        changed = format; changed.chromaSiting = MFVideoChromaSubsampling_MPEG2; test(changed, false);
        changed = format; changed.bitrate = 1000000; test(changed, false);
        changed = format; changed.transfer = MFVideoTransFunc_2084; test(changed, false);
        changed = format; changed.width = 4096; test(changed, false);
        changed = format; changed.frameRate = 61; test(changed, false);
        changed = format; changed.encoding = static_cast<exporting::VideoEncoding>(255); test(changed, false);
        format = {Width, Height, Rate, 16000000, MFVideoPrimaries_BT709, MFVideoTransFunc_709,
            MFVideoTransferMatrix_BT709, MFNominalRange_16_235, eAVEncH264VProfile_Base};
        test(format, true);
        changed = format; changed.chromaSiting = MFVideoChromaSubsampling_MPEG2; test(changed, true);
        changed.chromaSiting |= MFVideoChromaSubsampling_ProgressiveChroma; test(changed, true);
        changed = format; changed.bitrate = 0; test(changed, false);
        changed = format; changed.profile = eAVEncH264VProfile_444; test(changed, false);
        changed = format; changed.matrix = MFVideoTransferMatrix_Identity; test(changed, false);
        changed = format; changed.nominalRange = MFNominalRange_0_255; test(changed, false);
        const auto packet = [&](bool audio, size_t bytes, exporting::VideoEncoding mode, bool expected) {
            Require(exporting::IsPacketSizeSupported(audio, bytes, mode) == expected, "packet_size_contract_failed"); ++count;
        };
        constexpr size_t standardLimit = 16u * 1024 * 1024, losslessLimit = 64u * 1024 * 1024;
        const auto standard = exporting::VideoEncoding::H264Baseline420;
        const auto lossless = exporting::VideoEncoding::H264LosslessGbr444;
        packet(false, standardLimit, standard, true);
        packet(false, standardLimit + 1, standard, false);
        packet(false, 39471729, standard, false);
        packet(false, 39471729, lossless, true); // Measured full-entropy 2160p packet.
        packet(false, losslessLimit, lossless, true);
        packet(false, losslessLimit + 1, lossless, false);
        for (auto mode : {standard, lossless})
        {
            packet(false, 0, mode, false);
            packet(false, (std::numeric_limits<size_t>::max)(), mode, false);
            packet(true, 65536, mode, true);
            packet(true, 65537, mode, false);
        }
        packet(false, 1, static_cast<exporting::VideoEncoding>(255), false);
        return count;
    }

    struct Readback
    {
        UINT packets = 0, metadataFieldsRead = 0;
        DWORD largestPacketBytes = 0;
        std::array<UINT32, 5> color{}; // profile, primaries, transfer, matrix, range.
        LONGLONG maxTimeError = 0, maxDurationError = 0;
        bool metadata = false;
    };
    void Verify(const std::wstring& path, const Source& source, const Fixture& fixture, Readback& result)
    {
        ComPtr<IMFAttributes> attributes;
        Check(MFCreateAttributes(&attributes, 3), "readback_attributes_failed");
        Check(attributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE), "readback_attributes_failed");
        Check(attributes->SetUINT32(MF_SOURCE_READER_DISABLE_DXVA, TRUE), "readback_attributes_failed");
        Check(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE), "readback_attributes_failed");
        ComPtr<IMFSourceReader> reader;
        Check(MFCreateSourceReaderFromURL(path.c_str(), attributes.Get(), &reader), "readback_source_failed");
        Check(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE), "readback_selection_failed");
        Check(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE), "readback_selection_failed");
        ComPtr<IMFMediaType> type;
        Check(reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &type), "readback_type_failed");
        GUID subtype{};
        Check(type->GetGUID(MF_MT_SUBTYPE, &subtype), "readback_subtype_failed");
        Require(subtype == MFVideoFormat_H264, "readback_not_compressed_h264");
        Check(reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, type.Get()), "readback_passthrough_failed");
        UINT32 width = 0, height = 0, numerator = 0, denominator = 0;
        Check(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height), "readback_size_failed");
        Check(MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &numerator, &denominator), "readback_rate_failed");
        Require(width == fixture.width && height == fixture.height && numerator == Rate && denominator == 1, "readback_geometry_mismatch");
        const auto format = LosslessFormat(fixture);
        const std::array<std::pair<GUID, UINT>, 5> metadata{{
            {MF_MT_MPEG2_PROFILE, format.profile}, {MF_MT_VIDEO_PRIMARIES, format.primaries},
            {MF_MT_TRANSFER_FUNCTION, format.transfer}, {MF_MT_YUV_MATRIX, format.matrix},
            {MF_MT_VIDEO_NOMINAL_RANGE, format.nominalRange}}};
        for (const auto& field : metadata)
        {
            auto& value = result.color[result.metadataFieldsRead];
            Check(type->GetUINT32(field.first, &value), "readback_color_attribute_missing");
            ++result.metadataFieldsRead;
            Require(value == field.second, "readback_color_attribute_mismatch");
        }
        result.metadata = true;
        for (UINT call = 0; call <= fixture.frames; ++call)
        {
            DWORD stream = 0, flags = 0;
            LONGLONG time = 0;
            ComPtr<IMFSample> sample;
            Check(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &stream, &flags, &time, &sample), "readback_sample_failed");
            Require((flags & ~MF_SOURCE_READERF_ENDOFSTREAM) == 0, "readback_unexpected_flags");
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
            {
                Require(!sample && result.packets == fixture.frames, "readback_end_mismatch");
                return;
            }
            Require(sample != nullptr && result.packets < fixture.frames, "readback_packet_count_mismatch");
            LONGLONG duration = 0;
            Check(sample->GetSampleDuration(&duration), "readback_duration_failed");
            const LONGLONG timeError = std::llabs(time - FrameTime(result.packets));
            const LONGLONG durationError = std::llabs(duration - (FrameTime(result.packets + 1) - FrameTime(result.packets)));
            result.maxTimeError = (std::max)(result.maxTimeError, timeError);
            result.maxDurationError = (std::max)(result.maxDurationError, durationError);
            Require(timeError <= 1 && durationError <= 1, "readback_timing_mismatch");
            ComPtr<IMFMediaBuffer> buffer;
            Check(sample->ConvertToContiguousBuffer(&buffer), "readback_buffer_failed");
            DWORD bytes = 0;
            Check(buffer->GetCurrentLength(&bytes), "readback_buffer_length_failed");
            Require(bytes && bytes <= fixture.maximumReadbackBytes, "readback_packet_size_invalid");
            result.largestPacketBytes = (std::max)(result.largestPacketBytes, bytes);
            std::vector<BYTE> payload(bytes);
            BYTE* data = nullptr;
            Check(buffer->Lock(&data, nullptr, nullptr), "readback_buffer_lock_failed");
            std::memcpy(payload.data(), data, bytes);
            Check(buffer->Unlock(), "readback_buffer_unlock_failed");
            UINT slices = 0;
            for (const auto& nal : Nals(payload))
            {
                if (nal.type != 1 && nal.type != 5)
                {
                    Require(nal.type == 6 || nal.type == 7 || nal.type == 8 || nal.type == 9, "readback_nal_unexpected");
                    continue;
                }
                const auto& expected = source.Expected(result.packets);
                Require(++slices == 1 && nal.type == expected.type && nal.end - nal.payload == expected.end - expected.payload &&
                    std::memcmp(payload.data() + nal.payload, source.Bytes().data() + expected.payload, nal.end - nal.payload) == 0,
                    "readback_compressed_bytes_mismatch");
            }
            Require(slices == 1, "readback_slice_missing");
            ++result.packets;
        }
        throw Failure{"readback_no_end", E_FAIL};
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "--self-test: CPU-only format contracts.\n"
            "--mux PINNED_SYNTHETIC_H264 NEW_LOCAL_DIRECTORY: actual Wisp MF writer and compressed readback.\n"
            "--mux-entropy2160 PINNED_TWO_FRAME_H264 NEW_LOCAL_DIRECTORY: same check with the 39,471,729-byte 4K packet.\n"
            "Forza and Wisp must be closed. No capture, decoder, encoder, audio or visible window.15-second watchdog.\n";
        return 0;
    }
    if (!((argc == 2 && wcscmp(argv[1], L"--self-test") == 0) ||
        (argc == 4 && (wcscmp(argv[1], L"--mux") == 0 || wcscmp(argv[1], L"--mux-entropy2160") == 0)))) return 2;
    const Fixture& fixture = argc == 4 && wcscmp(argv[1], L"--mux-entropy2160") == 0 ? EntropyFixture : StandardFixture;
    bool com = false, mf = false;
    exporting::ExportEvidence written;
    Readback readback;
    UINT contracts = 0;
    size_t largestInputPacket = 0;
    const char* reason = "not_started";
    HRESULT failure = S_OK;
    int result = 0;
    std::unique_ptr<Deadline> deadline;
    try
    {
        deadline = std::make_unique<Deadline>();
        contracts = Contracts();
        if (argc == 4)
        {
            RequireApplicationsClosed();
            const auto sourcePath = LocalPath(argv[2]), directory = LocalPath(argv[3]);
            const auto sourceParents = HoldParents(sourcePath);
            const auto destinationParents = HoldParents(directory);
            const auto bytes = ReadPinned(sourcePath, fixture);
            Source source(bytes, fixture);
            largestInputPacket = source.LargestPacket();
            Win(CreateDirectoryW(directory.c_str(), nullptr) != FALSE, "new_output_directory_required");
            const auto output = directory + L"\\fixture.mp4";
            const auto outputParents = HoldParents(output);
            Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "com_start_failed"); com = true;
            Check(MFStartup(MF_VERSION), "mf_start_failed"); mf = true;
            std::atomic<bool> cancelled{false};
            {
                ComPtr<IMFByteStream> destination;
                Check(MFCreateFile(MF_ACCESSMODE_WRITE, MF_OPENMODE_FAIL_IF_EXIST, MF_FILEFLAGS_NONE,
                    output.c_str(), &destination), "new_mp4_file_failed");
                written = exporting::WritePacketStream(destination.Get(), source, LosslessFormat(fixture), cancelled);
                if (!written.completed) throw Failure{written.reason, FAILED(written.hr) ? written.hr : written.cleanupHr};
            }
            Require(written.samplesWritten == fixture.frames && written.duration100ns == FrameTime(fixture.frames), "writer_evidence_mismatch");
            Verify(output, source, fixture, readback);
            RequireApplicationsClosed();
            reason = "actual_writer_compressed_roundtrip_passed";
        }
        else reason = "format_contracts_passed";
    }
    catch (const Failure& error) { reason = error.reason; failure = error.hr; result = 3; }
    catch (...) { reason = "unexpected_fixture_failure"; failure = E_FAIL; result = 3; }
    HRESULT shutdown = S_OK;
    if (mf) shutdown = MFShutdown();
    if (com) CoUninitialize();
    if (FAILED(shutdown)) { reason = "mf_shutdown_failed"; result = 4; }
    std::cout << std::boolalpha << "{\"completed\":" << (result == 0) << ",\"reason\":\"" << reason
        << "\",\"hresult\":" << static_cast<UINT>(failure) << ",\"mfShutdownHresult\":" << static_cast<UINT>(shutdown)
        << ",\"formatContracts\":" << contracts << ",\"writerCompleted\":" << written.completed
        << ",\"width\":" << fixture.width << ",\"height\":" << fixture.height << ",\"expectedFrames\":" << fixture.frames
        << ",\"largestInputPacketBytes\":" << largestInputPacket << ",\"largestReadbackPacketBytes\":" << readback.largestPacketBytes
        << ",\"writerCleanupHresult\":" << static_cast<UINT>(written.cleanupHr) << ",\"packetsWritten\":" << written.samplesWritten
        << ",\"payloadBytesWritten\":" << written.compressedBytes << ",\"duration100ns\":" << written.duration100ns
        << ",\"readbackPackets\":" << readback.packets << ",\"metadataMatches\":" << readback.metadata
        << ",\"metadataFieldsRead\":" << readback.metadataFieldsRead << ",\"observedProfile\":" << readback.color[0]
        << ",\"observedPrimaries\":" << readback.color[1] << ",\"observedTransfer\":" << readback.color[2]
        << ",\"observedMatrix\":" << readback.color[3] << ",\"observedRange\":" << readback.color[4]
        << ",\"maxTimestampError100ns\":" << readback.maxTimeError << ",\"maxDurationError100ns\":" << readback.maxDurationError
        << ",\"requiresIndependentPixelDecode\":true,\"captureUsed\":false,\"encoderUsed\":false}\n";
    return result;
}
