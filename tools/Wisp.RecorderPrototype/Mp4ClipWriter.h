#pragma once

#include "EncodedClipBuffer.h"
#include "AacEncoder.h"
#include <windows.h>
#include <atomic>
#include <mfobjects.h>

namespace recorder::exporting
{
    enum class VideoEncoding { H264Baseline420, H264LosslessGbr444, HevcMain10Pq420, HevcLosslessPqGbr444 };
    bool IsPacketSizeSupported(bool audio, size_t bytes, VideoEncoding) noexcept;

    struct VideoFormat
    {
        UINT width = 0, height = 0, frameRate = 0, bitrate = 0;
        UINT primaries = 0, transfer = 0, matrix = 0, nominalRange = 0, profile = 0;
        UINT pixelAspectNumerator = 1, pixelAspectDenominator = 1, chromaSiting = 0;
        VideoEncoding encoding = VideoEncoding::H264Baseline420;
    };
    struct ExportEvidence
    {
        bool completed = false;
        const char* reason = "not_started";
        HRESULT hr = S_OK, cleanupHr = S_OK;
        HRESULT sinkShutdownHr = S_OK, byteStreamCloseHr = S_OK;
        UINT samplesWritten = 0;
        UINT audioSamplesWritten = 0;
        std::uint64_t compressedBytes = 0;
        std::uint64_t audioCompressedBytes = 0;
        buffer::MediaTime duration100ns = 0;
    };

    struct AudioPacket
    {
        LONGLONG timestamp100ns = 0, duration100ns = 0;
        std::vector<BYTE> payload;
    };
    struct AudioTrack
    {
        aac::CodecConfiguration configuration{};
        UINT bitrate = 192000;
        std::vector<AudioPacket> packets;
    };
    // Timestamps share the video epoch. Packet times remain the encoder's
    // times; this contract does not guess encoder priming compensation.
    const char* ValidateAudio(const AudioTrack&, const buffer::Clip&) noexcept;

    // CPU-only structure validation. Compressed syntax remains the encoder's
    // responsibility. Both explicit H264 modes require ordered packets without B frames.
    const char* ValidateClip(const buffer::Clip& clip, const VideoFormat& format) noexcept;

    struct ClipDescription
    {
        std::vector<BYTE> h264SequenceHeader;
        aac::CodecConfiguration audioConfiguration{};
        UINT audioBitrate = 192000;
        LONGLONG start100ns = 0, end100ns = 0, audioStart100ns = 0, audioEnd100ns = 0;
        UINT videoPackets = 0, audioPackets = 0;
    };
    struct PacketView
    {
        const BYTE* data = nullptr;
        size_t bytes = 0;
        LONGLONG timestamp100ns = 0, duration100ns = 0;
        bool cleanPoint = false;
    };
    struct PacketSource
    {
        virtual ~PacketSource() = default;
        virtual const ClipDescription& Description() const noexcept = 0;
        // S_OK: one borrowed packet, valid until next read on the same track.
        // S_FALSE: EOF; a failed HRESULT is never treated as EOF.
        virtual HRESULT Read(bool audio, PacketView&) noexcept = 0;
    };
    const char* ValidateDescription(const ClipDescription&, const VideoFormat&) noexcept;
    // Takes a reference to the caller's new stream and closes that MF stream on
    // every path. Caller must release it then check its underlying owned handle.
    // Validates bounded packets as they stream; failed output remains a partial.
    ExportEvidence WritePacketStream(IMFByteStream*, PacketSource&, const VideoFormat&,
        const std::atomic<bool>& cancelled) noexcept;

    // Caller owns COM/MF lifetime and supplies a new local staging filename.
    // Fails if it exists. Muxes compressed packets without video re-encoding.
    // Failed partial output is left to the caller; no rename/delete is implicit.
    ExportEvidence WriteMp4(const wchar_t* newStagingFile, const buffer::Clip& clip,
        const VideoFormat& format, const std::atomic<bool>& cancelled,
        const AudioTrack* audio = nullptr) noexcept;
}
