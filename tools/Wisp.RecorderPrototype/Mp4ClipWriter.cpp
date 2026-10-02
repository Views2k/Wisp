#include "Mp4ClipWriter.h"

#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <codecapi.h>
#include <wrl/client.h>
#include <cstring>
#include <limits>
#include <new>

namespace recorder::exporting
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        struct Failure { const char* reason; HRESULT hr; };
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_INVALIDARG }; }
    }

    bool IsPacketSizeSupported(bool audio, size_t bytes, VideoEncoding encoding) noexcept
    {
        size_t maximumVideoBytes = 0;
        switch (encoding)
        {
        case VideoEncoding::H264Baseline420: maximumVideoBytes = 16u * 1024 * 1024; break;
        case VideoEncoding::H264LosslessGbr444: maximumVideoBytes = 64u * 1024 * 1024; break;
        default: return false;
        }
        return bytes > 0 && bytes <= (audio ? aac::MaximumPacketBytes : maximumVideoBytes);
    }

    static const char* ValidateFormat(const VideoFormat& format) noexcept
    {
        if (format.width < 2 || format.height < 2 || format.width > 3840 || format.height > 2160 ||
            (format.width & 1) || (format.height & 1) || format.frameRate == 0 || format.frameRate > 60 ||
            format.primaries != MFVideoPrimaries_BT709 || format.transfer != MFVideoTransFunc_709 ||
            format.pixelAspectNumerator == 0 || format.pixelAspectDenominator == 0 ||
            format.pixelAspectNumerator > 65535 || format.pixelAspectDenominator > 65535)
            return "export_format_unsupported";
        switch (format.encoding)
        {
        case VideoEncoding::H264Baseline420:
            if (format.bitrate == 0 || format.profile != eAVEncH264VProfile_Base ||
                format.matrix != MFVideoTransferMatrix_BT709 || format.nominalRange != MFNominalRange_16_235 ||
                (format.chromaSiting != 0 && format.chromaSiting != MFVideoChromaSubsampling_MPEG2 &&
                 format.chromaSiting != (MFVideoChromaSubsampling_MPEG2 | MFVideoChromaSubsampling_ProgressiveChroma)))
                return "export_format_unsupported";
            return nullptr;
        case VideoEncoding::H264LosslessGbr444:
            // Matrix enum6 is MF's identity mapping; the H264 VUI uses matrix0.
            // No chroma subsampling or nominal rate target belongs to this mode.
            if (format.bitrate != 0 || format.profile != eAVEncH264VProfile_444 ||
                format.matrix != MFVideoTransferMatrix_Identity || format.nominalRange != MFNominalRange_0_255 ||
                format.chromaSiting != 0)
                return "export_format_unsupported";
            return nullptr;
        default:
            return "export_format_unsupported";
        }
    }

    const char* ValidateAudio(const AudioTrack& audio, const buffer::Clip& clip) noexcept
    {
        auto configuration = audio.configuration;
        if (!aac::ValidateCodecConfiguration(configuration) ||
            (audio.bitrate != 96000 && audio.bitrate != 128000 && audio.bitrate != 160000 && audio.bitrate != 192000))
            return "export_audio_format_invalid";
        if (audio.packets.empty() || audio.packets.size() > 14064 || clip.start100ns < 0 || clip.end100ns <= clip.start100ns)
            return "export_audio_bounds_invalid";
        constexpr LONGLONG maximumPacketDuration = 213334; // 1024 frames at48kHz, rounded up.
        if (audio.packets.front().timestamp100ns < clip.start100ns ||
            audio.packets.front().timestamp100ns - clip.start100ns > maximumPacketDuration)
            return "export_audio_start_mismatch";
        auto next = audio.packets.front().timestamp100ns;
        std::uint64_t bytes = 0;
        for (const auto& packet : audio.packets)
        {
            if (packet.timestamp100ns != next || packet.duration100ns < 213333 || packet.duration100ns > maximumPacketDuration ||
                packet.timestamp100ns > (std::numeric_limits<LONGLONG>::max)() - packet.duration100ns ||
                packet.payload.empty() || packet.payload.size() > aac::MaximumPacketBytes)
                return "export_audio_packet_invalid";
            bytes += packet.payload.size();
            if (bytes > 16 * 1024 * 1024) return "export_audio_bytes_exceeded";
            next += packet.duration100ns;
        }
        const auto distance = next > clip.end100ns ? next - clip.end100ns : clip.end100ns - next;
        return distance <= maximumPacketDuration ? nullptr : "export_audio_end_mismatch";
    }

    const char* ValidateClip(const buffer::Clip& clip, const VideoFormat& format) noexcept
    {
        if (const char* reason = ValidateFormat(format)) return reason;
        if (!clip.configuration.epoch || !clip.configuration.h264SequenceHeader ||
            clip.configuration.h264SequenceHeader->Bytes().empty() ||
            clip.configuration.h264SequenceHeader->Bytes().size() > 65536)
            return "export_configuration_invalid";
        if (clip.packets.empty() || clip.packets.size() > 18000 || !clip.packets.front().cleanPoint ||
            clip.start100ns < 0 || clip.end100ns <= clip.start100ns ||
            clip.end100ns - clip.start100ns > 300LL * 10000000)
            return "export_clip_bounds_invalid";
        auto next = clip.start100ns;
        for (const auto& packet : clip.packets)
        {
            if (packet.timestamp100ns != next || packet.duration100ns <= 0 ||
                packet.timestamp100ns > (std::numeric_limits<buffer::MediaTime>::max)() - packet.duration100ns ||
                !packet.payload || !IsPacketSizeSupported(false, packet.payload->Bytes().size(), format.encoding))
                return "export_packet_invalid";
            next += packet.duration100ns;
        }
        return next == clip.end100ns ? nullptr : "export_end_time_mismatch";
    }

    const char* ValidateDescription(const ClipDescription& clip, const VideoFormat& format) noexcept
    {
        if (const char* reason = ValidateFormat(format)) return reason;
        if (clip.h264SequenceHeader.empty() || clip.h264SequenceHeader.size() > 65536 ||
            clip.videoPackets == 0 || clip.videoPackets > 18000 || clip.start100ns < 0 ||
            clip.end100ns <= clip.start100ns || clip.end100ns - clip.start100ns > 300ll * 10000000)
            return "export_clip_bounds_invalid";
        if (clip.audioPackets)
        {
            auto config = clip.audioConfiguration;
            if (clip.audioPackets > 14064 || !aac::ValidateCodecConfiguration(config) ||
                (clip.audioBitrate != 96000 && clip.audioBitrate != 128000 && clip.audioBitrate != 160000 && clip.audioBitrate != 192000) ||
                clip.audioStart100ns < clip.start100ns || clip.audioStart100ns - clip.start100ns > 213334 ||
                clip.audioEnd100ns <= clip.audioStart100ns)
                return "export_audio_bounds_invalid";
            const auto distance = clip.audioEnd100ns > clip.end100ns ? clip.audioEnd100ns - clip.end100ns : clip.end100ns - clip.audioEnd100ns;
            if (distance > 213334) return "export_audio_end_mismatch";
        }
        else if (clip.audioStart100ns != 0 || clip.audioEnd100ns != 0) return "export_audio_bounds_invalid";
        return nullptr;
    }

    namespace
    {
        class MemorySource final : public PacketSource
        {
        public:
            MemorySource(const buffer::Clip& clip, const AudioTrack* audio) : clip_(clip), audio_(audio)
            {
                description_.h264SequenceHeader = clip.configuration.h264SequenceHeader->Bytes();
                description_.start100ns = clip.start100ns; description_.end100ns = clip.end100ns;
                description_.videoPackets = static_cast<UINT>(clip.packets.size());
                if (audio)
                {
                    description_.audioConfiguration = audio->configuration; description_.audioBitrate = audio->bitrate;
                    description_.audioPackets = static_cast<UINT>(audio->packets.size());
                    description_.audioStart100ns = audio->packets.front().timestamp100ns;
                    const auto& last = audio->packets.back();
                    description_.audioEnd100ns = last.timestamp100ns + last.duration100ns;
                }
            }
            const ClipDescription& Description() const noexcept override { return description_; }
            HRESULT Read(bool audio, PacketView& view) noexcept override
            {
                view = {};
                if (audio)
                {
                    if (!audio_ || audioIndex_ == audio_->packets.size()) return S_FALSE;
                    const auto& packet = audio_->packets[audioIndex_++];
                    view = { packet.payload.data(), packet.payload.size(), packet.timestamp100ns, packet.duration100ns, false };
                }
                else
                {
                    if (videoIndex_ == clip_.packets.size()) return S_FALSE;
                    const auto& packet = clip_.packets[videoIndex_++];
                    const auto& bytes = packet.payload->Bytes();
                    view = { bytes.data(), bytes.size(), packet.timestamp100ns, packet.duration100ns, packet.cleanPoint };
                }
                return S_OK;
            }
        private:
            const buffer::Clip& clip_;
            const AudioTrack* audio_;
            ClipDescription description_{};
            size_t videoIndex_ = 0, audioIndex_ = 0;
        };
    }

    ExportEvidence WriteMp4(const wchar_t* newStagingFile, const buffer::Clip& clip,
        const VideoFormat& format, const std::atomic<bool>& cancelled, const AudioTrack* audio) noexcept
    {
        ExportEvidence result;
        try
        {
            if (const char* reason = ValidateClip(clip, format)) throw Failure{ reason, E_INVALIDARG };
            if (audio) if (const char* reason = ValidateAudio(*audio, clip)) throw Failure{ reason, E_INVALIDARG };
            Require(newStagingFile && *newStagingFile, "export_path_missing");
            Require(!cancelled.load(), "export_cancelled");
            MemorySource source(clip, audio);
            ComPtr<IMFByteStream> bytes;
            Check(MFCreateFile(MF_ACCESSMODE_WRITE, MF_OPENMODE_FAIL_IF_EXIST, MF_FILEFLAGS_NONE,
                newStagingFile, &bytes), "export_new_file_failed");
            return WritePacketStream(bytes.Get(), source, format, cancelled);
        }
        catch (const Failure& failure) { result.reason = failure.reason; result.hr = failure.hr; }
        catch (const std::bad_alloc&) { result.reason = "export_allocation_failed"; result.hr = E_OUTOFMEMORY; }
        catch (...) { result.reason = "export_unexpected_failure"; result.hr = E_FAIL; }
        return result;
    }

    ExportEvidence WritePacketStream(IMFByteStream* destinationStream, PacketSource& source,
        const VideoFormat& format, const std::atomic<bool>& cancelled) noexcept
    {
        ExportEvidence evidence;
        ComPtr<IMFByteStream> bytes = destinationStream;
        const auto& clip = source.Description();
        const bool audio = clip.audioPackets != 0;
        ComPtr<IMFByteStream> sinkBytes;
        ComPtr<IMFMediaSink> sink;
        ComPtr<IMFSinkWriter> writer;
        try
        {
            if (const char* reason = ValidateDescription(clip, format)) throw Failure{ reason, E_INVALIDARG };
            Require(bytes != nullptr, "export_stream_missing");
            Require(!cancelled.load(), "export_cancelled");
            ComPtr<IMFMediaType> type;
            Check(MFCreateMediaType(&type), "export_type_creation_failed");
            Check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "export_type_attribute_failed");
            Check(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264), "export_type_attribute_failed");
            Check(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, format.width, format.height), "export_type_attribute_failed");
            Check(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, format.frameRate, 1), "export_type_attribute_failed");
            Check(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO,
                format.pixelAspectNumerator, format.pixelAspectDenominator), "export_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "export_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_MPEG2_PROFILE, format.profile), "export_type_attribute_failed");
            if (format.bitrate)
                Check(type->SetUINT32(MF_MT_AVG_BITRATE, format.bitrate), "export_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, format.primaries), "export_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, format.transfer), "export_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_YUV_MATRIX, format.matrix), "export_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, format.nominalRange), "export_type_attribute_failed");
            if (format.chromaSiting)
                Check(type->SetUINT32(MF_MT_VIDEO_CHROMA_SITING, format.chromaSiting), "export_type_attribute_failed");
            const auto& header = clip.h264SequenceHeader;
            Check(type->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, header.data(), static_cast<UINT32>(header.size())), "export_header_failed");
            ComPtr<IMFMediaType> audioType;
            if (audio)
            {
                Check(MFCreateMediaType(&audioType), "export_audio_type_creation_failed");
                Check(audioType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), "export_audio_attribute_failed");
                Check(audioType->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC), "export_audio_attribute_failed");
                Check(audioType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, aac::SampleRate), "export_audio_attribute_failed");
                Check(audioType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, aac::Channels), "export_audio_attribute_failed");
                Check(audioType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, aac::BitsPerSample), "export_audio_attribute_failed");
                Check(audioType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, clip.audioBitrate / 8), "export_audio_attribute_failed");
                Check(audioType->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0), "export_audio_attribute_failed");
                Check(audioType->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29), "export_audio_attribute_failed");
                Check(audioType->SetBlob(MF_MT_USER_DATA, clip.audioConfiguration.userData.data(),
                    clip.audioConfiguration.userDataBytes), "export_audio_configuration_failed");
            }
            // The sink can close its stream. A wrapper keeps final file-close
            // ownership here without closing the same file stream twice.
            Check(MFCreateMFByteStreamWrapper(bytes.Get(), &sinkBytes), "export_stream_wrapper_failed");
            Check(MFCreateMPEG4MediaSink(sinkBytes.Get(), type.Get(), audioType.Get(), &sink), "mp4_sink_creation_failed");
            DWORD streams = 0;
            Check(sink->GetStreamSinkCount(&streams), "export_stream_count_failed");
            Require(streams == (audio ? 2u : 1u), "export_stream_count_mismatch");
            for (DWORD index = 0; index < streams; ++index)
            {
                ComPtr<IMFStreamSink> stream;
                ComPtr<IMFMediaTypeHandler> handler;
                GUID major{};
                Check(sink->GetStreamSinkByIndex(index, &stream), "export_stream_lookup_failed");
                Check(stream->GetMediaTypeHandler(&handler), "export_stream_handler_failed");
                Check(handler->GetMajorType(&major), "export_stream_type_failed");
                Require(major == (index == 0 ? MFMediaType_Video : MFMediaType_Audio), "export_stream_order_mismatch");
            }
            ComPtr<IMFAttributes> attributes;
            Check(MFCreateAttributes(&attributes, 1), "export_attributes_failed");
            Check(attributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE), "export_attributes_failed");
            // Preserve WriteSample backpressure: a five-minute disk source must
            // not be queued wholesale inside the sink writer.
            Check(MFCreateSinkWriterFromMediaSink(sink.Get(), attributes.Get(), &writer), "export_writer_creation_failed");
            Check(writer->SetInputMediaType(0, type.Get(), nullptr), "compressed_passthrough_rejected");
            if (audio) Check(writer->SetInputMediaType(1, audioType.Get(), nullptr), "compressed_audio_passthrough_rejected");
            Check(writer->BeginWriting(), "export_begin_failed");
            PacketView video{}, sound{};
            UINT videoCount = 0, audioCount = 0;
            LONGLONG videoNext = clip.start100ns, audioNext = clip.audioStart100ns;
            const auto read = [&](bool isAudio, PacketView& packet)
            {
                packet = {};
                const HRESULT hr = source.Read(isAudio, packet);
                Check(hr, "export_packet_read_failed");
                Require(hr == S_OK || hr == S_FALSE, "export_packet_read_invalid");
                auto& count = isAudio ? audioCount : videoCount;
                auto& next = isAudio ? audioNext : videoNext;
                const UINT expected = isAudio ? clip.audioPackets : clip.videoPackets;
                if (hr == S_FALSE)
                {
                    Require(count == expected && next == (isAudio ? clip.audioEnd100ns : clip.end100ns),
                        "export_packet_count_or_end_mismatch");
                    return false;
                }
                Require(count < expected && packet.data && IsPacketSizeSupported(isAudio, packet.bytes, format.encoding) &&
                    packet.timestamp100ns == next && packet.duration100ns > 0 &&
                    packet.timestamp100ns <= (std::numeric_limits<LONGLONG>::max)() - packet.duration100ns,
                    "export_packet_invalid");
                if (isAudio) Require(packet.duration100ns >= 213333 && packet.duration100ns <= 213334,
                    "export_audio_duration_invalid");
                else if (count == 0) Require(packet.cleanPoint, "export_keyframe_missing");
                next += packet.duration100ns;
                Require(next <= (isAudio ? clip.audioEnd100ns : clip.end100ns), "export_packet_end_exceeded");
                ++count;
                return true;
            };
            const auto write = [&](bool isAudio, const PacketView& packet)
            {
                Require(!cancelled.load(), "export_cancelled");
                auto& accumulated = isAudio ? evidence.audioCompressedBytes : evidence.compressedBytes;
                const std::uint64_t maximumBytes = isAudio ? 16ull * 1024 * 1024 : 15ull * 1024 * 1024 * 1024;
                Require(packet.bytes <= maximumBytes - accumulated, "export_bytes_exceeded");
                ComPtr<IMFMediaBuffer> encoded;
                Check(MFCreateMemoryBuffer(static_cast<DWORD>(packet.bytes), &encoded), "export_sample_allocation_failed");
                BYTE* destination = nullptr;
                Check(encoded->Lock(&destination, nullptr, nullptr), "export_buffer_lock_failed");
                std::memcpy(destination, packet.data, packet.bytes);
                Check(encoded->Unlock(), "export_buffer_unlock_failed");
                Check(encoded->SetCurrentLength(static_cast<DWORD>(packet.bytes)), "export_buffer_length_failed");
                ComPtr<IMFSample> sample;
                Check(MFCreateSample(&sample), "export_sample_creation_failed");
                Check(sample->AddBuffer(encoded.Get()), "export_buffer_attach_failed");
                Check(sample->SetSampleTime(packet.timestamp100ns - clip.start100ns), "export_timestamp_failed");
                Check(sample->SetSampleDuration(packet.duration100ns), "export_duration_failed");
                auto& written = isAudio ? evidence.audioSamplesWritten : evidence.samplesWritten;
                if (!isAudio) Check(sample->SetUINT32(MFSampleExtension_CleanPoint, packet.cleanPoint ? TRUE : FALSE), "export_keyframe_failed");
                if (written == 0) Check(sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE), "export_discontinuity_failed");
                Check(writer->WriteSample(isAudio ? 1 : 0, sample.Get()), "export_write_failed");
                ++written;
                accumulated += packet.bytes;
            };
            bool haveVideo = read(false, video), haveAudio = audio && read(true, sound);
            while (haveVideo || haveAudio)
            {
                const bool nextAudio = haveAudio && (!haveVideo || sound.timestamp100ns <= video.timestamp100ns);
                write(nextAudio, nextAudio ? sound : video);
                if (nextAudio) haveAudio = read(true, sound);
                else haveVideo = read(false, video);
            }
            Require(!cancelled.load(), "export_cancelled");
            Check(writer->Finalize(), "export_finalize_failed");
            evidence.duration100ns = clip.end100ns - clip.start100ns;
            evidence.completed = true;
            evidence.reason = "compressed_mp4_written";
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "export_allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "export_unexpected_failure"; evidence.hr = E_FAIL; }
        writer.Reset();
        if (sink)
        {
            evidence.sinkShutdownHr = sink->Shutdown();
            evidence.cleanupHr = evidence.sinkShutdownHr;
        }
        sink.Reset();
        sinkBytes.Reset();
        if (bytes)
        {
            const HRESULT closed = bytes->Close();
            evidence.byteStreamCloseHr = closed;
            if (SUCCEEDED(evidence.cleanupHr)) evidence.cleanupHr = closed;
        }
        if (FAILED(evidence.cleanupHr))
        {
            evidence.completed = false;
            evidence.reason = "export_cleanup_failed";
        }
        return evidence;
    }
}
