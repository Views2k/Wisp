#include "SpoolMp4Writer.h"
#include "OwnedFileStream.h"
#include <mfidl.h>
#include <wrl/client.h>
#include <algorithm>
#include <new>

namespace recorder::exporting
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        class SpoolSource final : public PacketSource
        {
        public:
            explicit SpoolSource(const spool::Snapshot& snapshot)
            {
                const auto& format = snapshot.Format();
                const auto& bounds = snapshot.Range();
                description_.h264SequenceHeader = format.h264SequenceHeader;
                description_.start100ns = bounds.start100ns; description_.end100ns = bounds.end100ns;
                description_.audioStart100ns = bounds.audioStart100ns; description_.audioEnd100ns = bounds.audioEnd100ns;
                description_.videoPackets = bounds.videoPackets; description_.audioPackets = bounds.audioPackets;
                if (format.aacUserData.size() > aac::MaximumConfigBytes) { hr_ = E_INVALIDARG; return; }
                std::copy(format.aacUserData.begin(), format.aacUserData.end(), description_.audioConfiguration.userData.begin());
                description_.audioConfiguration.userDataBytes = static_cast<UINT>(format.aacUserData.size());
                if (bounds.audioPackets && !aac::ValidateCodecConfiguration(description_.audioConfiguration))
                { hr_ = E_INVALIDARG; return; }
                hr_ = snapshot.OpenCursor(spool::Track::Video, video_);
                if (SUCCEEDED(hr_) && bounds.audioPackets) hr_ = snapshot.OpenCursor(spool::Track::Audio, audio_);
            }
            const ClipDescription& Description() const noexcept override { return description_; }
            HRESULT Status() const noexcept { return hr_; }
            HRESULT Read(bool audio, PacketView& view) noexcept override
            {
                view = {};
                if (FAILED(hr_)) return hr_;
                auto& cursor = audio ? audio_ : video_;
                auto& packet = audio ? sound_ : picture_;
                if (!cursor) return audio && description_.audioPackets == 0 ? S_FALSE : E_UNEXPECTED;
                switch (cursor->Next(packet))
                {
                case spool::ReadResult::End: return S_FALSE;
                case spool::ReadResult::Failed: return FAILED(cursor->Error()) ? cursor->Error() : E_FAIL;
                case spool::ReadResult::Packet:
                    view = { packet.bytes.data(), packet.bytes.size(), packet.timestamp100ns, packet.duration100ns, packet.cleanPoint };
                    return S_OK;
                }
                return E_UNEXPECTED;
            }
            HRESULT Close() noexcept
            {
                const HRESULT video = video_ ? video_->Close() : S_OK;
                const HRESULT audio = audio_ ? audio_->Close() : S_OK;
                video_.reset(); audio_.reset();
                return FAILED(video) ? video : audio;
            }
        private:
            ClipDescription description_{};
            std::unique_ptr<spool::PacketCursor> video_, audio_;
            spool::Packet picture_{}, sound_{};
            HRESULT hr_ = S_OK;
        };
    }

    FileExportEvidence WriteSpoolMp4(HANDLE ownedFile, const spool::Snapshot& snapshot,
        const VideoFormat& format, const std::atomic<bool>& cancelled) noexcept
    {
        FileExportEvidence result;
        ComPtr<OwnedFileStream> stream;
        ComPtr<IMFByteStream> bytes;
        std::unique_ptr<SpoolSource> source;
        try
        {
            if (!ownedFile || ownedFile == INVALID_HANDLE_VALUE)
            { result.media.reason = "export_file_missing"; result.media.hr = E_INVALIDARG; return result; }
            stream = Microsoft::WRL::Make<OwnedFileStream>(ownedFile);
            if (!stream) throw std::bad_alloc();
            ownedFile = INVALID_HANDLE_VALUE;
            source = std::make_unique<SpoolSource>(snapshot);
            result.media.hr = source->Status();
            result.media.reason = "export_cursor_open_failed";
            if (SUCCEEDED(result.media.hr))
            {
                result.media.hr = MFCreateMFByteStreamOnStream(stream.Get(), &bytes);
                result.media.reason = "export_file_stream_failed";
                if (SUCCEEDED(result.media.hr)) result.media = WritePacketStream(bytes.Get(), *source, format, cancelled);
            }
        }
        catch (const std::bad_alloc&) { result.media.reason = "export_allocation_failed"; result.media.hr = E_OUTOFMEMORY; }
        catch (...) { result.media.reason = "export_unexpected_failure"; result.media.hr = E_FAIL; }
        // WritePacketStream already shut down its sink and closed its MF wrapper.
        bytes.Reset();
        if (source) result.cursorCloseHr = source->Close();
        source.reset();
        if (stream) result.fileCloseHr = stream->FlushAndClose(result.fileBytes);
        else if (ownedFile && ownedFile != INVALID_HANDLE_VALUE && !CloseHandle(ownedFile))
            result.fileCloseHr = HRESULT_FROM_WIN32(GetLastError());
        stream.Reset();
        if (FAILED(result.fileCloseHr) || FAILED(result.cursorCloseHr))
        {
            result.media.completed = false;
            result.media.reason = "export_cleanup_failed";
            result.media.cleanupHr = FAILED(result.fileCloseHr) ? result.fileCloseHr : result.cursorCloseHr;
        }
        return result;
    }
}
