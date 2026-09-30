#include "HardwareEncoder.h"
#include "HardwareVideoSession.h"
#include "ConversionOutput.h"
#include "EncodedClipBuffer.h"
#include "Mp4ClipWriter.h"
#include "SpoolMp4Writer.h"
#include "SyntheticHdrFrameProvider.h"

#include <mfapi.h>
#include <mferror.h>
#include <codecapi.h>
#include <d3d10.h>
#include <dxgi1_2.h>
#include <tlhelp32.h>
#include <array>
#include <cmath>
#include <cstring>
#include <cwchar>
#include <iostream>
#include <vector>

namespace
{
    using Microsoft::WRL::ComPtr;
    using namespace recorder;
    std::atomic<bool> cancelled{ false };
    BOOL WINAPI Cancel(DWORD signal) noexcept
    {
        if (signal != CTRL_C_EVENT && signal != CTRL_BREAK_EVENT) return FALSE;
        cancelled.store(true);
        return TRUE;
    }
    exporting::VideoFormat FormatFor(const encoder::EncodeConfig& configuration = {})
    {
        return { configuration.width, configuration.height, configuration.frameRate, configuration.bitrate,
            MFVideoPrimaries_BT709, MFVideoTransFunc_709, MFVideoTransferMatrix_BT709,
            MFNominalRange_16_235, eAVEncH264VProfile_Base, configuration.pixelAspectNumerator,
            configuration.pixelAspectDenominator, configuration.chromaSiting };
    }
    class AudioCollector final : public aac::PacketObserver
    {
    public:
        exporting::AudioTrack track;
        HRESULT OnConfiguration(const aac::CodecConfiguration& configuration, IMFMediaType*) noexcept override
        { track.configuration = configuration; return S_OK; }
        HRESULT OnPacket(const aac::PacketView& packet) noexcept override
        {
            if (!packet.data || !packet.bytes || packet.bytes > aac::MaximumPacketBytes ||
                packet.index != track.packets.size() || track.packets.size() >= 96) return E_INVALIDARG;
            try
            {
                track.packets.push_back({ packet.time100ns, packet.duration100ns,
                    std::vector<BYTE>(packet.data, packet.data + packet.bytes) });
                return S_OK;
            }
            catch (...) { return E_OUTOFMEMORY; }
        }
    };
    aac::Evidence EncodeSyntheticAudio(AudioCollector& collected)
    {
        aac::Encoder encoder;
        aac::Configuration configuration;
        configuration.maximumSourceFrames = 96000;
        bool okay = encoder.Initialize(configuration, cancelled, &collected);
        std::array<std::int16_t, 480 * 2> pcm{};
        for (UINT first = 0; okay && first < 96000; first += 480)
        {
            for (UINT offset = 0; offset < 480; ++offset)
            {
                const UINT frame = first + offset;
                // Four generated 100ms tone bursts at250/750/1250/1750ms.
                // Decode verifies both channels and measures actual onset delay.
                const bool active = frame % 24000 >= 12000 && frame % 24000 < 16800;
                const double seconds = static_cast<double>(frame) / 48000.0;
                constexpr double twoPi = 6.28318530717958647692;
                pcm[offset * 2] = active ? static_cast<std::int16_t>(std::lround(9000.0 * std::sin(twoPi * 440.0 * seconds))) : std::int16_t{0};
                pcm[offset * 2 + 1] = active ? static_cast<std::int16_t>(std::lround(6000.0 * std::sin(twoPi * 660.0 * seconds))) : std::int16_t{0};
            }
            okay = encoder.Feed(pcm.data(), 480, first);
        }
        if (okay) okay = encoder.Drain();
        const auto closed = encoder.Close();
        auto evidence = encoder.Result();
        if (!okay || FAILED(closed)) evidence.completed = false;
        return evidence;
    }
    class Collector final : public encoder::FixtureObserver
    {
    public:
        explicit Collector(exporting::VideoFormat format = FormatFor(),
            synthetic::SyntheticHdrFrameProvider* provider = nullptr) : format_(format), provider_(provider) {}
        buffer::EncodedClipBuffer rolling{ { 5LL * 10000000, 16 * 1024 * 1024 } };
        std::vector<UINT> expectedLuma;
        std::vector<synthetic::ExpectedPatches> expectedPatches;
        std::vector<BYTE> header;
        const char* reason = "not_started";
        HRESULT OnInput(UINT frame, UINT surfaceSlot) noexcept override
        {
            try
            {
                if (surfaceSlot >= encoder::PoolSize) return E_UNEXPECTED;
                if (provider_)
                {
                    synthetic::ExpectedPatches expected;
                    if (frame != expectedPatches.size() || !provider_->ExpectedFrame(frame, expected)) return E_UNEXPECTED;
                    expectedPatches.push_back(expected);
                }
                else
                {
                    if (frame != expectedLuma.size()) return E_UNEXPECTED;
                    expectedLuma.push_back(32 + surfaceSlot * 80);
                }
                return S_OK;
            }
            catch (...) { return E_OUTOFMEMORY; }
        }
        HRESULT OnOutput(IMFMediaType* type, IMFSample* sample) noexcept override
        {
            try
            {
                reason = "output_format_invalid";
                if (!type || !sample) return E_POINTER;
                const auto& format = format_;
                UINT32 width = 0, height = 0, rate = 0, denominator = 0;
                HRESULT hr = MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height);
                if (FAILED(hr)) return hr;
                hr = MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &rate, &denominator);
                if (FAILED(hr)) return hr;
                if (width != format.width || height != format.height || rate != format.frameRate || denominator != 1)
                    return MF_E_INVALIDMEDIATYPE;
                UINT32 aspectNumerator = 0, aspectDenominator = 0;
                if (FAILED(hr = MFGetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, &aspectNumerator, &aspectDenominator))) return hr;
                if (aspectNumerator != format.pixelAspectNumerator || aspectDenominator != format.pixelAspectDenominator)
                    return MF_E_INVALIDMEDIATYPE;
                if (format.chromaSiting)
                {
                    UINT32 siting = 0;
                    if (FAILED(hr = type->GetUINT32(MF_MT_VIDEO_CHROMA_SITING, &siting))) return hr;
                    if (siting != format.chromaSiting) return MF_E_INVALIDMEDIATYPE;
                }
                const std::array<std::pair<GUID, UINT32>, 6> required{{
                    {MF_MT_AVG_BITRATE, format.bitrate}, {MF_MT_VIDEO_PRIMARIES, format.primaries},
                    {MF_MT_TRANSFER_FUNCTION, format.transfer}, {MF_MT_YUV_MATRIX, format.matrix},
                    {MF_MT_VIDEO_NOMINAL_RANGE, format.nominalRange}, {MF_MT_MPEG2_PROFILE, format.profile} }};
                for (const auto& attribute : required)
                {
                    UINT32 value = 0;
                    hr = type->GetUINT32(attribute.first, &value);
                    if (FAILED(hr)) return hr;
                    if (value != attribute.second) return MF_E_INVALIDMEDIATYPE;
                }
                reason = "output_header_invalid";
                UINT32 headerBytes = 0;
                hr = type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &headerBytes);
                if (FAILED(hr)) return hr;
                if (!headerBytes || headerBytes > 65536) return E_INVALIDARG;
                std::vector<BYTE> observedHeader(headerBytes);
                hr = type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, observedHeader.data(), headerBytes, nullptr);
                if (FAILED(hr)) return hr;
                if (header.empty())
                {
                    header = std::move(observedHeader);
                    if (rolling.BeginEpoch(1, header.data(), header.size()) != buffer::Result::Accepted) return E_FAIL;
                }
                else if (header != observedHeader) return MF_E_INVALIDMEDIATYPE;
                LONGLONG time = 0, duration = 0;
                reason = "output_time_missing";
                if (FAILED(hr = sample->GetSampleTime(&time)) || FAILED(hr = sample->GetSampleDuration(&duration))) return hr;
                UINT32 clean = 0;
                hr = sample->GetUINT32(MFSampleExtension_CleanPoint, &clean);
                if (FAILED(hr) && hr != MF_E_ATTRIBUTENOTFOUND) return hr;
                ComPtr<IMFMediaBuffer> compressed;
                if (FAILED(hr = sample->ConvertToContiguousBuffer(&compressed))) return hr;
                DWORD size = 0;
                if (FAILED(hr = compressed->GetCurrentLength(&size))) return hr;
                if (!size || size > 16 * 1024 * 1024) return E_INVALIDARG;
                BYTE* data = nullptr;
                if (FAILED(hr = compressed->Lock(&data, nullptr, nullptr))) return hr;
                const auto result = rolling.Append(1, time, duration, clean != 0, data, size);
                const HRESULT unlocked = compressed->Unlock();
                if (FAILED(unlocked)) return unlocked;
                reason = result == buffer::Result::Accepted ? "compressed_sample_retained" : "compressed_buffer_rejected_sample";
                return result == buffer::Result::Accepted ? S_OK : E_FAIL;
            }
            catch (...) { reason = "collector_allocation_failed"; return E_OUTOFMEMORY; }
        }
    private:
        exporting::VideoFormat format_;
        synthetic::SyntheticHdrFrameProvider* provider_;
    };
    bool FixtureGameClosed() noexcept
    {
        const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return false;
        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        bool closed = Process32FirstW(snapshot, &entry) != FALSE;
        if (closed) do
        {
            if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") == 0) { closed = false; break; }
        } while (Process32NextW(snapshot, &entry));
        if (closed) closed = GetLastError() == ERROR_NO_MORE_FILES;
        CloseHandle(snapshot);
        return closed;
    }
    encoder::Evidence RunLiveApiFixture(const encoder::Options& options, Collector& collector,
        synthetic::SyntheticHdrFrameProvider& provider) noexcept
    {
        encoder::Evidence result;
        if (!FixtureGameClosed()) { result.reason = "game_closed_guard_failed"; result.hr = E_ABORT; return result; }
        const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(com)) { result.reason = "com_initialization_failed"; result.hr = com; return result; }
        const HRESULT mf = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (SUCCEEDED(mf))
        {
            const char* initializationStage = "live_fixture_initialization_failed";
            try
            {
                struct Observer final : encoder::PacketObserver
                {
                    Collector& collector;
                    explicit Observer(Collector& value) : collector(value) {}
                    HRESULT OnConfiguration(IMFMediaType* type, const encoder::EncodeConfig&) noexcept override
                    { return type ? S_OK : E_POINTER; }
                    HRESULT OnPacket(IMFMediaType* type, IMFSample* sample) noexcept override
                    { return collector.OnOutput(type, sample); }
                } observer(collector);
                struct Writer final : encoder::FrameWriter
                {
                    Collector& collector;
                    synthetic::SyntheticHdrFrameProvider& provider;
                    Writer(Collector& sink, synthetic::SyntheticHdrFrameProvider& source) : collector(sink), provider(source) {}
                    HRESULT Fill(UINT frame, ID3D11Texture2D* texture) noexcept override
                    {
                        const auto hr = provider.Fill(frame, 0, texture);
                        return FAILED(hr) ? hr : collector.OnInput(frame, 0);
                    }
                } writer(collector, provider);
                ComPtr<IDXGIFactory1> factory;
                ComPtr<IDXGIAdapter1> adapter;
                ComPtr<ID3D11Device> device;
                ComPtr<ID3D10Multithread> multithread;
                auto check = [](HRESULT hr) { if (FAILED(hr)) throw hr; };
                initializationStage = "live_fixture_factory_failed";
                check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
                initializationStage = "live_fixture_adapter_failed";
                check(factory->EnumAdapters1(0, &adapter));
                DXGI_ADAPTER_DESC1 description{};
                initializationStage = "live_fixture_adapter_description_failed";
                check(adapter->GetDesc1(&description));
                if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) throw E_NOINTERFACE;
                const D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
                initializationStage = "live_fixture_d3d_device_failed";
                check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    &level, 1, D3D11_SDK_VERSION, &device, nullptr, nullptr));
                initializationStage = "live_fixture_multithread_failed";
                check(device.As(&multithread));
                multithread->SetMultithreadProtected(TRUE);
                initializationStage = "live_fixture_provider_initialization_failed";
                check(provider.Initialize(device.Get(), options.configuration));
                encoder::HardwareVideoSession session;
                encoder::LiveOptions live;
                const auto deadline = GetTickCount64() + 10000;
                bool okay = session.Initialize(device.Get(), options.configuration, live, cancelled, observer);
                UINT frame = 0;
                bool guardFailed = false;
                while (okay && frame < options.frames)
                {
                    if (cancelled.load() || GetTickCount64() >= deadline || !FixtureGameClosed())
                    { guardFailed = true; okay = false; break; }
                    okay = session.Pump(50);
                    if (!okay) break;
                    const auto submitted = session.TrySubmit(frame, static_cast<LONGLONG>(frame) * 10000000 / options.configuration.frameRate, writer);
                    if (submitted == encoder::SubmitResult::Failed) okay = false;
                    else if (submitted == encoder::SubmitResult::Submitted) ++frame;
                }
                if (okay && (cancelled.load() || GetTickCount64() >= deadline || !FixtureGameClosed()))
                { guardFailed = true; okay = false; }
                if (okay) okay = session.Drain();
                if (cancelled.load() || GetTickCount64() >= deadline || !FixtureGameClosed())
                { guardFailed = true; okay = false; }
                const auto close = session.Close();
                result = session.Result();
                if (!okay || FAILED(close)) result.completed = false;
                if (guardFailed) { result.reason = "fixture_guard_failed"; result.hr = E_ABORT; }
            }
            catch (HRESULT hr) { result.reason = initializationStage; result.hr = hr; result.completed = false; }
            catch (...) { result.reason = "live_fixture_unexpected_failure"; result.hr = E_FAIL; result.completed = false; }
            const auto stopped = MFShutdown();
            if (FAILED(stopped)) { result.completed = false; result.cleanupHr = stopped; }
        }
        else { result.reason = "mf_initialization_failed"; result.hr = mf; }
        CoUninitialize();
        return result;
    }
    bool CpuContracts()
    {
        auto format = FormatFor();
        buffer::EncodedClipBuffer rolling({ 20000000, 1024 });
        const BYTE data[]{ 0,0,0,1,9 };
        if (rolling.BeginEpoch(1, data, sizeof(data)) != buffer::Result::Accepted ||
            rolling.Append(1, 100, 333333, true, data, sizeof(data)) != buffer::Result::Accepted ||
            rolling.Retain() != buffer::Result::Accepted) return false;
        const auto clip = rolling.Retained();
        if (exporting::ValidateClip(*clip, format)) return false;
        auto bad = *clip;
        bad.packets.front().cleanPoint = false;
        if (!exporting::ValidateClip(bad, format)) return false;
        bad = *clip; bad.end100ns++;
        if (!exporting::ValidateClip(bad, format)) return false;
        bad = *clip; bad.start100ns = -1;
        if (!exporting::ValidateClip(bad, format)) return false;
        bad = *clip; bad.packets.front().duration100ns = 0;
        if (!exporting::ValidateClip(bad, format)) return false;
        bad = *clip; bad.configuration.h264SequenceHeader.reset();
        if (!exporting::ValidateClip(bad, format)) return false;
        format.transfer = MFVideoTransFunc_sRGB;
        if (!exporting::ValidateClip(*clip, format)) return false;
        format = FormatFor(); format.frameRate = 0;
        if (!exporting::ValidateClip(*clip, format)) return false;
        format = FormatFor(); format.pixelAspectDenominator = 0;
        if (!exporting::ValidateClip(*clip, format)) return false;
        format = FormatFor(); format.chromaSiting = 0xffffffff;
        return exporting::ValidateClip(*clip, format) != nullptr;
    }
    UINT AudioCpuContracts()
    {
        UINT checks = 0;
        bool passed = true;
        const auto check = [&](bool condition) { ++checks; passed = passed && condition; };
        buffer::Clip clip;
        clip.start100ns = 100;
        clip.end100ns = 426766;
        exporting::AudioTrack audio;
        audio.configuration.userDataBytes = 14;
        audio.configuration.userData[2] = 0x29;
        audio.configuration.userData[12] = 0x11;
        audio.configuration.userData[13] = 0x90;
        audio.packets = { { 100, 213333, { 1 } }, { 213433, 213333, { 2 } } };
        check(exporting::ValidateAudio(audio, clip) == nullptr);
        auto bad = audio;
        bad.configuration.userDataBytes = 1025;
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.bitrate = 100000;
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.packets.clear();
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.packets.front().timestamp100ns = 99;
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.packets.front().timestamp100ns = 213435;
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; ++bad.packets.back().timestamp100ns;
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.packets.back().duration100ns = 0;
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.packets.back().duration100ns = 213335;
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.packets.back().payload.clear();
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        bad = audio; bad.packets.back().payload.resize(65537);
        check(exporting::ValidateAudio(bad, clip) != nullptr);
        clip.end100ns += 213335;
        check(exporting::ValidateAudio(audio, clip) != nullptr);
        return passed ? checks : 0;
    }

    UINT StreamCpuContracts()
    {
        UINT count = 0;
        const auto good = [&](const exporting::ClipDescription& clip)
        { return exporting::ValidateDescription(clip, FormatFor()) == nullptr; };
        exporting::ClipDescription clip;
        clip.h264SequenceHeader = { 0, 0, 0, 1 };
        clip.end100ns = 10000000; clip.videoPackets = 30;
        if (!good(clip)) return 0; ++count;
        auto invalid = clip; invalid.h264SequenceHeader.clear(); if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.videoPackets = 0; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.videoPackets = 18001; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.start100ns = -1; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.end100ns = 3000000001ll; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.audioStart100ns = 1; if (good(invalid)) return 0; ++count;
        clip.audioPackets = 47; clip.audioEnd100ns = 10026666;
        clip.audioConfiguration.userDataBytes = 14;
        clip.audioConfiguration.userData[2] = 0x29;
        clip.audioConfiguration.userData[12] = 0x11;
        clip.audioConfiguration.userData[13] = 0x90;
        if (!good(clip)) return 0; ++count;
        invalid = clip; invalid.audioPackets = 14065; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.audioStart100ns = -1; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.audioStart100ns = 213335; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.audioEnd100ns = 10213335; if (good(invalid)) return 0; ++count;
        invalid = clip; invalid.audioBitrate = 100000; if (good(invalid)) return 0; ++count;
        return count;
    }

    exporting::FileExportEvidence ExportViaSpool(const wchar_t* output, const buffer::Clip& clip,
        const exporting::AudioTrack& audio, const exporting::VideoFormat& format, HRESULT& spoolClose)
    {
        exporting::FileExportEvidence result;
        spool::EncodedSpool retained;
        std::shared_ptr<const spool::Snapshot> snapshot;
        const auto fail = [&]() { result.media.reason = retained.Result().reason; result.media.hr = retained.Result().hr; };
        try
        {
            const std::wstring path(output);
            const auto split = path.find_last_of(L'\\');
            if (split == std::wstring::npos || !spool::ValidateExportName(path.substr(split + 1)))
            { result.media.reason = "fixture_guid_output_required"; result.media.hr = E_INVALIDARG; return result; }
            GUID id{};
            if (FAILED(CoCreateGuid(&id))) throw E_FAIL;
            wchar_t guid[40]{};
            if (StringFromGUID2(id, guid, 40) != 39) throw E_FAIL;
            std::wstring compact;
            for (const wchar_t c : guid)
            {
                if ((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f')) compact += c;
                else if (c >= L'A' && c <= L'F') compact += static_cast<wchar_t>(c + (L'a' - L'A'));
            }
            spool::Limits limits; limits.maximumDuration100ns = 5ll * 10000000; limits.maximumFileBytes = 16 * 1024 * 1024;
            spool::Configuration configuration;
            configuration.epoch = 1; configuration.h264SequenceHeader = clip.configuration.h264SequenceHeader->Bytes();
            configuration.aacUserData.assign(audio.configuration.userData.begin(),
                audio.configuration.userData.begin() + audio.configuration.userDataBytes);
            bool okay = retained.Initialize(path.substr(0, split + 1) + L".wisp-recorder-" + compact, limits, configuration);
            size_t picture = 0, sound = 0;
            while (okay && (picture < clip.packets.size() || sound < audio.packets.size()))
            {
                if (sound < audio.packets.size() && (picture == clip.packets.size() ||
                    audio.packets[sound].timestamp100ns <= clip.packets[picture].timestamp100ns))
                {
                    const auto& packet = audio.packets[sound++];
                    okay = retained.AppendAudio(packet.timestamp100ns, packet.duration100ns, packet.payload.data(), packet.payload.size());
                }
                else
                {
                    const auto& packet = clip.packets[picture++];
                    const auto& bytes = packet.payload->Bytes();
                    okay = retained.AppendVideo(packet.timestamp100ns, packet.duration100ns, packet.cleanPoint, bytes.data(), bytes.size());
                }
            }
            if (okay) okay = retained.Retain(5ll * 10000000, snapshot);
            if (okay)
            {
                HANDLE file = nullptr;
                const auto created = retained.CreateNewExport(path.substr(split + 1), file);
                if (SUCCEEDED(created)) result = exporting::WriteSpoolMp4(file, *snapshot, format, cancelled);
                else { result.media.reason = "fixture_export_create_failed"; result.media.hr = created; }
            }
            else fail();
        }
        catch (...) { result.media.reason = "fixture_spool_failed"; result.media.hr = E_FAIL; }
        snapshot.reset();
        spoolClose = retained.Close();
        if (FAILED(spoolClose)) { result.media.completed = false; result.media.reason = "fixture_spool_cleanup_failed"; }
        return result;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "Synthetic hardware H264 -> bounded buffer -> new MP4 file. No capture or audio devices.\n"
            "--self-test: CPU-only export contracts.\n"
            "--export-fixture <new-local-output.mp4>: 60 generated1080p30 frames.\n"
            "--hdr-export-fixture <new-local-output.mp4>: 120 alternating syntheticHDR frames,1080p60.\n"
            "--av-export-fixture <new-local-output.mp4>: same HDR video plus generated stereoAAC tone bursts.\n"
            "--live-av-export-fixture <new-local-output.mp4>: same generated inputs through the reusable live video API.\n"
            "--live-gop-export-fixture <new-local-output.mp4>: 241 generated video-only frames to verify two live GOP intervals.\n"
            "--spool-av-export-fixture <new-GUID-N.mp4>: same inputs through disk packets and owned-handle streaming export.\n"
            "Requires Forza closed. External watchdog required; partial output is preserved on failure.\n";
        return 0;
    }
    if (argc == 2 && wcscmp(argv[1], L"--self-test") == 0)
    {
        const UINT encoderChecks = encoder::RunContractTests();
        const UINT providerChecks = synthetic::SyntheticHdrFrameProvider::RunContractTests();
        const UINT audioChecks = AudioCpuContracts();
        const UINT liveChecks = encoder::RunLiveContractTests();
        const UINT outputChecks = conversion::RunOutputContractTests();
        const UINT streamChecks = StreamCpuContracts();
        const bool passed = CpuContracts() && encoderChecks > 0 && providerChecks > 0 && audioChecks > 0 && liveChecks > 0 && outputChecks > 0 && streamChecks > 0;
        std::cout << "{\"mode\":\"mp4_export_cpu_contracts\",\"passed\":" << (passed ? 10 : 0)
            << ",\"encoderContracts\":" << encoderChecks << ",\"providerContracts\":" << providerChecks
            << ",\"audioMuxContracts\":" << audioChecks
            << ",\"liveSessionContracts\":" << liveChecks << ",\"conversionOutputContracts\":" << outputChecks
            << ",\"streamContracts\":" << streamChecks
            << ",\"graphicsInitialized\":false}\n";
        return passed ? 0 : 1;
    }
    const bool spoolMode = argc == 3 && wcscmp(argv[1], L"--spool-av-export-fixture") == 0;
    const bool gopMode = argc == 3 && wcscmp(argv[1], L"--live-gop-export-fixture") == 0;
    const bool liveMode = gopMode || spoolMode || (argc == 3 && wcscmp(argv[1], L"--live-av-export-fixture") == 0);
    const bool audioMode = (liveMode && !gopMode) || (argc == 3 && wcscmp(argv[1], L"--av-export-fixture") == 0);
    const bool hdrMode = gopMode || audioMode || (argc == 3 && wcscmp(argv[1], L"--hdr-export-fixture") == 0);
    if (argc != 3 || (!hdrMode && wcscmp(argv[1], L"--export-fixture") != 0) || wcslen(argv[2]) < 8 ||
        argv[2][1] != L':' || argv[2][2] != L'\\' || wcsstr(argv[2], L"://") ||
        wcscmp(argv[2] + wcslen(argv[2]) - 4, L".mp4") != 0)
    {
        std::cout << "{\"completed\":false,\"reason\":\"invalid_fixture_arguments\"}\n";
        return 2;
    }
    if (!SetConsoleCtrlHandler(Cancel, TRUE)) return 3;
    recorder::encoder::Options options;
    options.mode = recorder::encoder::Mode::Encode;
    synthetic::SyntheticHdrFrameProvider provider(80.0f);
    if (hdrMode)
    {
        options.frames = gopMode ? 241 : 120;
        options.configuration.frameRate = 60;
        options.configuration.bitrate = 16000000;
        // Explicit left/vertical-center sampling. The tested MFT rejects the
        // additional ProgressiveChroma reconstruction flag; frames themselves
        // remain explicitly progressive through MF_MT_INTERLACE_MODE.
        options.configuration.chromaSiting = MFVideoChromaSubsampling_MPEG2;
    }
    const auto format = FormatFor(options.configuration);
    Collector collector(format, hdrMode ? &provider : nullptr);
    auto encoded = liveMode ? RunLiveApiFixture(options, collector, provider) :
        recorder::encoder::RunSyntheticFixture(options, cancelled, &collector, hdrMode ? &provider : nullptr);
    if (gopMode && encoded.completed && (!encoded.gopSizeReadback || encoded.requestedGopFrames != 120 ||
        encoded.negotiatedGopFrames != 120 || encoded.observedGopIntervals < 2 ||
        encoded.maximumObservedGopFrames > 120 || encoded.cleanPoints < 3))
    {
        encoded.completed = false; encoded.reason = "fixture_gop_evidence_incomplete"; encoded.hr = E_FAIL;
    }
    recorder::exporting::ExportEvidence exported;
    exporting::FileExportEvidence fileExport;
    HRESULT spoolClose = S_OK;
    HRESULT exportMfShutdown = S_OK;
    aac::Evidence audioEvidence;
    AudioCollector audioCollector;
    if (encoded.completed && (hdrMode ? collector.expectedPatches.size() : collector.expectedLuma.size()) == options.frames &&
        collector.rolling.Retain() == recorder::buffer::Result::Accepted)
    {
        const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (SUCCEEDED(initialized))
        {
            const HRESULT started = MFStartup(MF_VERSION, MFSTARTUP_LITE);
            if (SUCCEEDED(started))
            {
                if (audioMode) audioEvidence = EncodeSyntheticAudio(audioCollector);
                if (spoolMode && audioEvidence.completed)
                {
                    fileExport = ExportViaSpool(argv[2], *collector.rolling.Retained(), audioCollector.track, format, spoolClose);
                    exported = fileExport.media;
                }
                else if (!audioMode || audioEvidence.completed)
                    exported = recorder::exporting::WriteMp4(argv[2], *collector.rolling.Retained(), format, cancelled,
                        audioMode ? &audioCollector.track : nullptr);
                else { exported.reason = "synthetic_audio_encoding_failed"; exported.hr = audioEvidence.hr; }
                exportMfShutdown = MFShutdown();
                if (FAILED(exportMfShutdown))
                {
                    exported.completed = false;
                    exported.reason = "export_mf_shutdown_failed";
                    if (SUCCEEDED(exported.cleanupHr)) exported.cleanupHr = exportMfShutdown;
                }
            }
            else { exported.reason = "mf_startup_failed"; exported.hr = started; }
            CoUninitialize();
        }
        else { exported.reason = "com_startup_failed"; exported.hr = initialized; }
    }
    (void)SetConsoleCtrlHandler(Cancel, FALSE);
    std::cout << std::boolalpha << "{\"mode\":\"synthetic_h264_buffer_mp4\",\"completed\":" << exported.completed
        << ",\"hdrInput\":" << hdrMode << ",\"frameRate\":" << format.frameRate
        << ",\"liveVideoApi\":" << liveMode
        << ",\"spoolUsed\":" << spoolMode << ",\"fileCloseHresult\":" << static_cast<UINT>(fileExport.fileCloseHr)
        << ",\"cursorCloseHresult\":" << static_cast<UINT>(fileExport.cursorCloseHr)
        << ",\"spoolCloseHresult\":" << static_cast<UINT>(spoolClose) << ",\"fileBytes\":" << fileExport.fileBytes
        << ",\"exportMfShutdownHresult\":" << static_cast<UINT>(exportMfShutdown)
        << ",\"framesRequested\":" << options.frames << ",\"configurationNegotiated\":" << encoded.configurationNegotiated
        << ",\"requestedGopFrames\":" << encoded.requestedGopFrames << ",\"negotiatedGopFrames\":" << encoded.negotiatedGopFrames
        << ",\"observedGopIntervals\":" << encoded.observedGopIntervals << ",\"maximumObservedGopFrames\":" << encoded.maximumObservedGopFrames
        << ",\"cleanPoints\":" << encoded.cleanPoints << ",\"gopSizeReadback\":" << encoded.gopSizeReadback
        << ",\"gopModifiableQueryHresult\":" << static_cast<UINT>(encoded.gopModifiableQueryHr)
        << ",\"providerFramesFilled\":" << encoded.providerFramesFilled
        << ",\"encoderCleanupHresult\":" << static_cast<UINT>(encoded.cleanupHr)
        << ",\"providerReason\":\"" << provider.Evidence().reason << "\""
        << ",\"encoderCompleted\":" << encoded.completed << ",\"encoderReason\":\"" << encoded.reason
        << "\",\"encoderHresult\":" << static_cast<UINT>(encoded.hr)
        << ",\"collectorReason\":\"" << collector.reason << "\",\"exportReason\":\"" << exported.reason
        << "\",\"exportHresult\":" << static_cast<UINT>(exported.hr)
        << ",\"cleanupHresult\":" << static_cast<UINT>(exported.cleanupHr)
        << ",\"sinkShutdownHresult\":" << static_cast<UINT>(exported.sinkShutdownHr)
        << ",\"byteStreamCloseHresult\":" << static_cast<UINT>(exported.byteStreamCloseHr)
        << ",\"samplesWritten\":" << exported.samplesWritten << ",\"compressedBytes\":" << exported.compressedBytes
        << ",\"audioSamplesWritten\":" << exported.audioSamplesWritten << ",\"audioCompressedBytes\":" << exported.audioCompressedBytes
        << ",\"audioEncoderCompleted\":" << audioEvidence.completed << ",\"audioEncoderReason\":\"" << audioEvidence.reason << "\""
        << ",\"audioEncoderHresult\":" << static_cast<UINT>(audioEvidence.hr)
        << ",\"audioEncoderCleanupHresult\":" << static_cast<UINT>(audioEvidence.cleanupHr)
        << ",\"audioApplicationPaddingFrames\":" << audioEvidence.applicationZeroPaddingFrames
        << ",\"audioPrimingMeasured\":false"
        << ",\"duration100ns\":" << exported.duration100ns << ",\"accountedBufferBytes\":" << collector.rolling.AccountedBytes()
        << ",\"captureUsed\":false,\"audioUsed\":" << audioMode
        << ",\"audioDeviceActivated\":false,\"decodedOutputVerified\":false,\"expectedLuma\":[";
    for (size_t index = 0; index < collector.expectedLuma.size(); ++index)
    {
        if (index) std::cout << ',';
        std::cout << collector.expectedLuma[index];
    }
    std::cout << "],\"expectedPatches\":[";
    for (size_t frame = 0; frame < collector.expectedPatches.size(); ++frame)
    {
        if (frame) std::cout << ',';
        std::cout << '[';
        for (size_t index = 0; index < synthetic::HdrPatchCount; ++index)
        {
            if (index) std::cout << ',';
            const auto& patch = collector.expectedPatches[frame][index];
            std::cout << '[' << patch.centerX << ',' << patch.centerY << ',' << patch.luma << ',' << patch.chromaU << ',' << patch.chromaV << ']';
        }
        std::cout << ']';
    }
    std::cout << "]}\n";
    return exported.completed ? 0 : 3;
}
