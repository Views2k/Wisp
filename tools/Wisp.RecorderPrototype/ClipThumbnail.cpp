#include "ClipThumbnail.h"

#include <windows.h>
#include <winternl.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/implements.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <utility>
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
#include <cstdio>
#endif

namespace recorder::thumbnail
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        constexpr DWORD AllStreams = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
        constexpr DWORD FirstVideoStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        const char* diagnosticStage = "request";
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
        const char* diagnosticMediaPhase = "none";
        UINT32 diagnosticInterlace = 0;
        DWORD diagnosticSubtype = 0;
        UINT32 diagnosticWidth = 0, diagnosticHeight = 0, diagnosticExpectedWidth = 0, diagnosticExpectedHeight = 0;
        bool diagnosticVideo = false;
        UINT32 diagnosticAspectNumerator = 0, diagnosticAspectDenominator = 0;
        struct DiagnosticAperture { MFVideoArea area{}; UINT32 bytes = 0; HRESULT result = E_FAIL; };
        DiagnosticAperture diagnosticMinimum, diagnosticGeometric;
        void ReadDiagnosticAperture(IMFMediaType* type, const GUID& key, DiagnosticAperture& result)
        {
            result = {};
            result.result = type->GetBlob(key,reinterpret_cast<UINT8*>(&result.area),sizeof(result.area),&result.bytes);
            if (FAILED(result.result) || result.bytes != sizeof(result.area)) result.area = {};
        }
        void PrintDiagnosticAperture(const char* name, const DiagnosticAperture& value)
        {
            std::fprintf(stderr,",\"%s\":{\"hresult\":%lu,\"bytes\":%u,\"x\":%d,\"xFraction\":%u,\"y\":%d,\"yFraction\":%u,\"width\":%ld,\"height\":%ld}",
                name,static_cast<unsigned long>(value.result),value.bytes,static_cast<int>(value.area.OffsetX.value),
                static_cast<unsigned>(value.area.OffsetX.fract),static_cast<int>(value.area.OffsetY.value),
                static_cast<unsigned>(value.area.OffsetY.fract),value.area.Area.cx,value.area.Area.cy);
        }
#endif
        struct Failure { HRESULT hr = E_FAIL; };
        void Require(bool condition) { if (!condition) throw Failure{}; }
        void Check(HRESULT result) { if (FAILED(result)) throw Failure{result}; }
        template<class T> T Get(const std::uint8_t* bytes) noexcept
        {
            T result = 0;
            for (std::size_t i = 0; i < sizeof(T); ++i) result |= static_cast<T>(bytes[i]) << (i * 8);
            return result;
        }
        template<class T> void Put(std::uint8_t* bytes, T value) noexcept
        { for (std::size_t i = 0; i < sizeof(T); ++i) { bytes[i] = static_cast<std::uint8_t>(value & 255); value >>= 8; } }
        bool Geometry(std::uint32_t width, std::uint32_t height) noexcept
        {
            return (height == 360 || height == 480 || height == 720 || height == 1080 || height == 1440 || height == 2160) &&
                width == (height == 480 ? 854u : height * 16 / 9);
        }
        bool SafePath(const std::wstring& path) noexcept
        {
            if (path.size() < 39 || path.size() > MaximumPathBytes / 2 ||
                !((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) ||
                path[1] != L':' || path[2] != L'\\') return false;
            for (std::size_t i = 0; i < path.size(); ++i)
            {
                const auto c = static_cast<unsigned>(path[i]);
                if (c < 32 || c == 0x7f || (c >= 0xdc00 && c <= 0xdfff)) return false;
                if (c >= 0xd800 && c <= 0xdbff)
                {
                    if (++i == path.size() || path[i] < 0xdc00 || path[i] > 0xdfff) return false;
                }
            }
            std::size_t first = 3;
            while (first < path.size())
            {
                const auto slash = path.find(L'\\', first);
                const auto end = slash == std::wstring::npos ? path.size() : slash;
                const auto length = end - first;
                if (!length || length > 255 || path[end-1] == L'.' || path[end-1] == L' ') return false;
                for (std::size_t i = first; i < end; ++i)
                    if (path[i] == L'/' || path[i] == L':' || path[i] == L'"' || path[i] == L'<' || path[i] == L'>' ||
                        path[i] == L'|' || path[i] == L'?' || path[i] == L'*') return false;
                std::size_t stem = 0; while (stem < length && path[first+stem] != L'.') ++stem;
                const auto reserved = [&](const wchar_t* value)
                { return stem == std::wcslen(value) && _wcsnicmp(path.c_str()+first,value,stem) == 0; };
                if (reserved(L"CON") || reserved(L"PRN") || reserved(L"AUX") || reserved(L"NUL") || reserved(L"CONIN$") || reserved(L"CONOUT$")) return false;
                if (stem == 4 && (_wcsnicmp(path.c_str()+first,L"COM",3) == 0 || _wcsnicmp(path.c_str()+first,L"LPT",3) == 0) &&
                    path[first+3] >= L'1' && path[first+3] <= L'9') return false;
                if (slash == std::wstring::npos)
                {
                    if (length != 36 || path.compare(first+32,4,L".mp4") != 0) return false;
                    bool nonzero = false;
                    for (std::size_t i = first; i < first+32; ++i)
                    {
                        const auto c = path[i];
                        if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) return false;
                        nonzero |= c != L'0';
                    }
                    return nonzero;
                }
                first = end + 1;
            }
            return false;
        }
        struct Handle
        {
            HANDLE value = nullptr;
            ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
            HANDLE Release() noexcept { const auto result = value; value = nullptr; return result; }
        };
        void OpenReadOnly(const Request& request, Handle& file)
        {
            diagnosticStage = "file_open";
            const std::wstring ntPath = L"\\??\\" + request.path;
            UNICODE_STRING name{};
            name.Buffer = const_cast<PWSTR>(ntPath.data());
            name.Length = name.MaximumLength = static_cast<USHORT>(ntPath.size()*sizeof(wchar_t));
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes,&name,OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE,nullptr,nullptr);
            IO_STATUS_BLOCK status{};
            const auto opened = NtCreateFile(&file.value,GENERIC_READ | SYNCHRONIZE,&attributes,&status,nullptr,
                FILE_ATTRIBUTE_NORMAL,FILE_SHARE_READ,FILE_OPEN,FILE_NON_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT,nullptr,0);
            if (opened < 0) { file.value = nullptr; throw Failure{HRESULT_FROM_NT(opened)}; }
            diagnosticStage = "file_attributes";
            FILE_ATTRIBUTE_TAG_INFO tag{};
            Require(GetFileInformationByHandleEx(file.value,FileAttributeTagInfo,&tag,sizeof(tag)) &&
                !(tag.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)));
            LARGE_INTEGER size{}; FILETIME modified{};
            diagnosticStage = "file_version";
            Require(GetFileSizeEx(file.value,&size) && size.QuadPart > 0 && static_cast<std::uint64_t>(size.QuadPart) == request.fileBytes &&
                GetFileTime(file.value,nullptr,nullptr,&modified));
            Require((static_cast<std::uint64_t>(modified.dwHighDateTime) << 32 | modified.dwLowDateTime) == request.lastWriteFileTime);
            std::array<wchar_t,32768> finalPath{};
            diagnosticStage = "local_volume";
            const DWORD length = GetFinalPathNameByHandleW(file.value,finalPath.data(),static_cast<DWORD>(finalPath.size()),FILE_NAME_OPENED | VOLUME_NAME_GUID);
            Require(length >= 49 && length < finalPath.size() && std::wcsncmp(finalPath.data(),L"\\\\?\\Volume{",11) == 0 &&
                finalPath[47] == L'}' && finalPath[48] == L'\\');
        }
        class ReadStream final : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IStream>
        {
        public:
            ReadStream(HANDLE file, std::uint64_t size) noexcept : file_(file), size_(size) {}
            ~ReadStream() { if (file_) CloseHandle(file_); }
            HRESULT Close() noexcept
            {
                std::lock_guard lock(mutex_);
                if (!file_) return S_OK;
                if (!CloseHandle(file_)) return HRESULT_FROM_WIN32(GetLastError());
                file_ = nullptr; return S_OK;
            }
            IFACEMETHOD(Read)(void* out,ULONG count,ULONG* read) override
            {
                if (read) *read = 0;
                if (count && !out) return STG_E_INVALIDPOINTER;
                std::lock_guard lock(mutex_); DWORD actual = 0;
                if (!ReadFile(file_,out,count,&actual,nullptr)) return HRESULT_FROM_WIN32(GetLastError());
                if (read) *read = actual;
                return actual == count ? S_OK : S_FALSE;
            }
            IFACEMETHOD(Seek)(LARGE_INTEGER move,DWORD origin,ULARGE_INTEGER* result) override
            {
                if (origin > STREAM_SEEK_END) return STG_E_INVALIDFUNCTION;
                std::lock_guard lock(mutex_); LARGE_INTEGER base{}, zero{};
                if (origin == STREAM_SEEK_CUR && !SetFilePointerEx(file_,zero,&base,FILE_CURRENT)) return HRESULT_FROM_WIN32(GetLastError());
                if (origin == STREAM_SEEK_END) base.QuadPart = static_cast<LONGLONG>(size_);
                if (base.QuadPart < 0 || static_cast<std::uint64_t>(base.QuadPart) > size_ || move.QuadPart < -base.QuadPart ||
                    move.QuadPart > static_cast<LONGLONG>(size_)-base.QuadPart) return STG_E_INVALIDFUNCTION;
                LARGE_INTEGER next{}; next.QuadPart = base.QuadPart+move.QuadPart;
                if (!SetFilePointerEx(file_,next,nullptr,FILE_BEGIN)) return HRESULT_FROM_WIN32(GetLastError());
                if (result) result->QuadPart = static_cast<ULONGLONG>(next.QuadPart);
                return S_OK;
            }
            IFACEMETHOD(Stat)(STATSTG* status,DWORD flags) override
            {
                if (!status) return STG_E_INVALIDPOINTER;
                if (flags != STATFLAG_DEFAULT && flags != STATFLAG_NONAME) return STG_E_INVALIDFLAG;
                std::memset(status,0,sizeof(*status)); status->type = STGTY_STREAM;
                status->cbSize.QuadPart = size_; status->grfMode = STGM_READ | STGM_SHARE_DENY_WRITE;
                return S_OK;
            }
            IFACEMETHOD(Write)(const void*,ULONG,ULONG* done) override { if (done) *done = 0; return STG_E_ACCESSDENIED; }
            IFACEMETHOD(SetSize)(ULARGE_INTEGER) override { return STG_E_ACCESSDENIED; }
            IFACEMETHOD(CopyTo)(IStream*,ULARGE_INTEGER,ULARGE_INTEGER* read,ULARGE_INTEGER* written) override
            { if (read) read->QuadPart = 0; if (written) written->QuadPart = 0; return E_NOTIMPL; }
            IFACEMETHOD(Commit)(DWORD) override { return STG_E_ACCESSDENIED; }
            IFACEMETHOD(Revert)() override { return STG_E_ACCESSDENIED; }
            IFACEMETHOD(LockRegion)(ULARGE_INTEGER,ULARGE_INTEGER,DWORD) override { return STG_E_INVALIDFUNCTION; }
            IFACEMETHOD(UnlockRegion)(ULARGE_INTEGER,ULARGE_INTEGER,DWORD) override { return STG_E_INVALIDFUNCTION; }
            IFACEMETHOD(Clone)(IStream** result) override { if (!result) return E_POINTER; *result = nullptr; return E_NOTIMPL; }
        private:
            HANDLE file_; const std::uint64_t size_; std::mutex mutex_;
        };
        bool ReadArea(IMFMediaType* type, const GUID& key, DisplayArea& result)
        {
            MFVideoArea area{}; UINT32 length = 0;
            const auto status = type->GetBlob(key,reinterpret_cast<UINT8*>(&area),sizeof(area),&length);
            if (status == MF_E_ATTRIBUTENOTFOUND) return false;
            Check(status); Require(length == sizeof(area));
            result = {area.OffsetX.value,area.OffsetY.value,area.Area.cx,area.Area.cy,area.OffsetX.fract,area.OffsetY.fract};
            return true;
        }
        struct ValidatedType { UINT32 interlace = 0, width = 0, height = 0; DisplayArea area; };
        ValidatedType ValidateType(IMFMediaType* type, const Request& request, const GUID& expectedSubtype)
        {
            diagnosticStage = "media_major_subtype";
            GUID major{}, subtype{}; UINT32 width = 0, height = 0;
            Check(type->GetGUID(MF_MT_MAJOR_TYPE,&major)); Check(type->GetGUID(MF_MT_SUBTYPE,&subtype));
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
            diagnosticSubtype = subtype.Data1;
#endif
            diagnosticStage = "media_dimensions"; Check(MFGetAttributeSize(type,MF_MT_FRAME_SIZE,&width,&height));
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
            diagnosticWidth = width; diagnosticHeight = height;
            diagnosticExpectedWidth = request.width; diagnosticExpectedHeight = request.height;
            diagnosticVideo = major == MFMediaType_Video;
            diagnosticAspectNumerator = diagnosticAspectDenominator = 0;
            (void)MFGetAttributeRatio(type,MF_MT_PIXEL_ASPECT_RATIO,&diagnosticAspectNumerator,&diagnosticAspectDenominator);
            ReadDiagnosticAperture(type,MF_MT_MINIMUM_DISPLAY_APERTURE,diagnosticMinimum);
            ReadDiagnosticAperture(type,MF_MT_GEOMETRIC_APERTURE,diagnosticGeometric);
#endif
            Require(major == MFMediaType_Video && subtype == expectedSubtype);
            DisplayArea area{0,0,static_cast<std::int32_t>(width),static_cast<std::int32_t>(height)};
            if (expectedSubtype == MFVideoFormat_H264)
                Require(width == request.width && height == request.height && Geometry(width,height));
            else
            {
                diagnosticStage = "display_aperture";
                Require(width > 0 && width <= 3840 && height > 0 && height <= 2176);
                DisplayArea minimum{}, geometric{};
                const bool hasMinimum = ReadArea(type,MF_MT_MINIMUM_DISPLAY_APERTURE,minimum);
                const bool hasGeometric = ReadArea(type,MF_MT_GEOMETRIC_APERTURE,geometric);
                if (hasMinimum) area = minimum;
                else if (hasGeometric) area = geometric;
                if (hasMinimum && hasGeometric)
                    Require(minimum.x == geometric.x && minimum.y == geometric.y && minimum.width == geometric.width &&
                        minimum.height == geometric.height && minimum.fractionX == geometric.fractionX && minimum.fractionY == geometric.fractionY);
                Require(MFGetAttributeUINT32(type,MF_MT_PAN_SCAN_ENABLED,FALSE) == FALSE);
                std::int64_t offset = 0;
                Require(GetCropOffset(width,height,request.width,request.height,area,static_cast<std::int32_t>(width*4),offset));
            }
            diagnosticStage = "media_interlace";
            UINT32 interlace = 0; Check(type->GetUINT32(MF_MT_INTERLACE_MODE,&interlace));
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
            diagnosticInterlace = interlace;
#endif
            Require(interlace == MFVideoInterlace_Progressive || interlace == MFVideoInterlace_MixedInterlaceOrProgressive);
            diagnosticStage = "media_pixel_aspect";
            UINT32 numerator = 0, denominator = 0;
            Check(MFGetAttributeRatio(type,MF_MT_PIXEL_ASPECT_RATIO,&numerator,&denominator));
            Require(numerator == (request.height == 480 ? 1280u : 1u) && denominator == (request.height == 480 ? 1281u : 1u));
            diagnosticStage = "media_rotation"; Require(MFGetAttributeUINT32(type,MF_MT_VIDEO_ROTATION,0) == 0);
            return {interlace,width,height,area};
        }
        int SampleFlag(IMFSample* sample, const GUID& key)
        {
            UINT32 value = 0;
            const auto result = sample->GetUINT32(key,&value);
            if (result == MF_E_ATTRIBUTENOTFOUND) return -1;
            Check(result); Require(value <= 1);
            return static_cast<int>(value);
        }
        std::vector<std::uint8_t> Decode(const Request& request)
        {
            Handle file; OpenReadOnly(request,file);
            diagnosticStage = "com_initialize";
            Check(CoInitializeEx(nullptr,COINIT_MULTITHREADED));
            struct ComEnd { ~ComEnd() { CoUninitialize(); } } com;
            diagnosticStage = "mf_startup"; Check(MFStartup(MF_VERSION,MFSTARTUP_LITE));
            struct MfEnd { bool active = true; ~MfEnd() { if (active) MFShutdown(); }
                HRESULT Close() { active = false; return MFShutdown(); } } mf;
            auto stream = Microsoft::WRL::Make<ReadStream>(file.value,request.fileBytes);
            Require(stream != nullptr); (void)file.Release();
            diagnosticStage = "byte_stream";
            ComPtr<IMFByteStream> bytes; Check(MFCreateMFByteStreamOnStream(stream.Get(),&bytes));
            diagnosticStage = "reader_attributes";
            ComPtr<IMFAttributes> attributes; Check(MFCreateAttributes(&attributes,4));
            Check(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,TRUE));
            Check(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,FALSE));
            Check(attributes->SetUINT32(MF_SOURCE_READER_DISABLE_DXVA,TRUE));
            diagnosticStage = "source_reader_create";
            ComPtr<IMFSourceReader> reader; Check(MFCreateSourceReaderFromByteStream(bytes.Get(),attributes.Get(),&reader));
            diagnosticStage = "video_selection";
            Check(reader->SetStreamSelection(AllStreams,FALSE));
            Check(reader->SetStreamSelection(FirstVideoStream,TRUE));
            diagnosticStage = "native_media_type";
            ComPtr<IMFMediaType> nativeType; Check(reader->GetNativeMediaType(FirstVideoStream,0,&nativeType));
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
            diagnosticMediaPhase = "native";
#endif
            ValidateType(nativeType.Get(),request,MFVideoFormat_H264);
            diagnosticStage = "rgb32_media_type";
            ComPtr<IMFMediaType> desired; Check(MFCreateMediaType(&desired));
            Check(desired->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video)); Check(desired->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_RGB32));
            Check(reader->SetCurrentMediaType(FirstVideoStream,nullptr,desired.Get()));
            std::vector<std::uint8_t> poster;
            for (unsigned attempt = 0; attempt < 16; ++attempt)
            {
                DWORD flags = 0; LONGLONG timestamp = 0; ComPtr<IMFSample> sample;
                diagnosticStage = "read_video_sample";
                Check(reader->ReadSample(FirstVideoStream,0,nullptr,&flags,&timestamp,&sample));
                Require(!(flags & MF_SOURCE_READERF_ERROR) && timestamp >= 0);
                diagnosticStage = "decoded_media_type";
                ComPtr<IMFMediaType> current; Check(reader->GetCurrentMediaType(FirstVideoStream,&current));
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
                diagnosticMediaPhase = "decoded";
#endif
                const auto layout = ValidateType(current.Get(),request,MFVideoFormat_RGB32);
                if (!sample) { Require(!(flags & MF_SOURCE_READERF_ENDOFSTREAM)); continue; }
                diagnosticStage = "sample_interlace";
                Require(IsProgressiveFrame(layout.interlace,SampleFlag(sample.Get(),MFSampleExtension_Interlaced),
                    SampleFlag(sample.Get(),MFSampleExtension_SingleField),SampleFlag(sample.Get(),MFSampleExtension_RepeatFirstField)));
                diagnosticStage = "contiguous_rgb32_buffer";
                ComPtr<IMFMediaBuffer> buffer; Check(sample->ConvertToContiguousBuffer(&buffer));
                diagnosticStage = "rgb32_buffer_lock";
                BYTE* data = nullptr; DWORD maximum = 0, length = 0; Check(buffer->Lock(&data,&maximum,&length));
                struct Unlock { IMFMediaBuffer* buffer; ~Unlock() { if (buffer) buffer->Unlock(); }
                    HRESULT Close() { auto* held = buffer; buffer = nullptr; return held->Unlock(); } } unlock{buffer.Get()};
                Require(data && length > 0 && length <= 128u*1024*1024 && maximum >= length);
                diagnosticStage = "rgb32_stride";
                UINT32 rawStride = 0;
                const auto strideResult = current->GetUINT32(MF_MT_DEFAULT_STRIDE,&rawStride);
                LONG stride = 0;
                if (strideResult == MF_E_ATTRIBUTENOTFOUND) Check(MFGetStrideForBitmapInfoHeader(MFVideoFormat_RGB32.Data1,layout.width,&stride));
                else { Check(strideResult); std::memcpy(&stride,&rawStride,sizeof(stride)); }
                std::int64_t offset = 0;
                Require(GetCropOffset(layout.width,layout.height,request.width,request.height,layout.area,stride,offset));
                diagnosticStage = "poster_resize";
                Require(MakePoster(data,length,offset,stride,request.width,request.height,poster,
                    request.height == 480 ? 1280u : 1u,request.height == 480 ? 1281u : 1u));
                diagnosticStage = "buffer_unlock"; Check(unlock.Close()); break;
            }
            Require(poster.size() == PosterBytes);
            diagnosticStage = "reader_flush"; Check(reader->Flush(AllStreams)); reader.Reset();
            nativeType.Reset(); desired.Reset(); attributes.Reset();
            diagnosticStage = "byte_stream_close"; Check(bytes->Close()); bytes.Reset();
            diagnosticStage = "file_close"; Check(stream->Close()); stream.Reset();
            diagnosticStage = "mf_shutdown"; Check(mf.Close());
            return poster;
        }
        void ReadExact(HANDLE input, std::uint8_t* bytes, DWORD length)
        {
            while (length)
            {
                DWORD count = 0; Require(ReadFile(input,bytes,length,&count,nullptr) && count > 0);
                bytes += count; length -= count;
            }
        }
        void WriteExact(HANDLE output, const std::uint8_t* bytes, DWORD length)
        {
            while (length)
            {
                DWORD count = 0; Require(WriteFile(output,bytes,length,&count,nullptr) && count > 0);
                bytes += count; length -= count;
            }
        }
    }
    bool IsProgressiveFrame(std::uint32_t mode, int interlaced, int singleField, int repeatFirstField) noexcept
    {
        if (interlaced < -1 || interlaced > 1 || singleField < -1 || singleField > 0 ||
            repeatFirstField < -1 || repeatFirstField > 0) return false;
        if (mode == MFVideoInterlace_Progressive) return interlaced != 1;
        if (mode == MFVideoInterlace_MixedInterlaceOrProgressive) return interlaced == 0;
        return false;
    }
    bool GetCropOffset(std::uint32_t codedWidth, std::uint32_t codedHeight, std::uint32_t visibleWidth,
        std::uint32_t visibleHeight, const DisplayArea& area, std::int32_t stride, std::int64_t& offset) noexcept
    {
        offset = 0;
        if (!Geometry(visibleWidth,visibleHeight) || codedWidth < visibleWidth || codedHeight < visibleHeight ||
            codedWidth > ((visibleWidth+31)/32)*32 || codedHeight > ((visibleHeight+31)/32)*32 ||
            area.x < 0 || area.y < 0 || area.fractionX || area.fractionY ||
            area.width != static_cast<std::int32_t>(visibleWidth) || area.height != static_cast<std::int32_t>(visibleHeight) ||
            static_cast<std::uint32_t>(area.x) > codedWidth-visibleWidth || static_cast<std::uint32_t>(area.y) > codedHeight-visibleHeight ||
            std::abs(static_cast<std::int64_t>(stride)) < static_cast<std::int64_t>(codedWidth)*4) return false;
        const auto origin = stride < 0 ? -static_cast<std::int64_t>(stride)*(codedHeight-1) : 0;
        offset = origin+static_cast<std::int64_t>(area.y)*stride+static_cast<std::int64_t>(area.x)*4;
        return true;
    }
    bool ParseRequest(const std::vector<std::uint8_t>& bytes, Request& result) noexcept
    {
        result = {};
        try
        {
            if (bytes.size() < RequestHeaderBytes || std::memcmp(bytes.data(),"WTR1",4) != 0) return false;
            const auto pathBytes = Get<std::uint32_t>(bytes.data()+4);
            if (!pathBytes || pathBytes > MaximumPathBytes || pathBytes % 2 || bytes.size() != RequestHeaderBytes+pathBytes) return false;
            Request parsed;
            parsed.fileBytes = Get<std::uint64_t>(bytes.data()+8);
            parsed.width = Get<std::uint32_t>(bytes.data()+16); parsed.height = Get<std::uint32_t>(bytes.data()+20);
            parsed.lastWriteFileTime = Get<std::uint64_t>(bytes.data()+24);
            if (!parsed.fileBytes || parsed.fileBytes > MaximumFileBytes || !parsed.lastWriteFileTime || !Geometry(parsed.width,parsed.height)) return false;
            parsed.path.reserve(pathBytes/2);
            for (std::size_t i = RequestHeaderBytes; i < bytes.size(); i += 2)
                parsed.path.push_back(static_cast<wchar_t>(Get<std::uint16_t>(bytes.data()+i)));
            if (!SafePath(parsed.path)) return false;
            result = std::move(parsed); return true;
        }
        catch (...) { return false; }
    }
    bool MakePoster(const std::uint8_t* source, std::size_t sourceBytes, std::int64_t offset, std::int32_t stride,
        std::uint32_t width, std::uint32_t height, std::vector<std::uint8_t>& result,
        std::uint32_t pixelAspectNumerator, std::uint32_t pixelAspectDenominator) noexcept
    {
        result.clear();
        try
        {
            if (!source || !width || width > 3840 || !height || height > 2160 || sourceBytes > 128u*1024*1024 ||
                std::abs(static_cast<std::int64_t>(stride)) < static_cast<std::int64_t>(width)*4 || offset < 0 ||
                static_cast<std::uint64_t>(offset) > sourceBytes) return false;
            if (!((pixelAspectNumerator == 1 && pixelAspectDenominator == 1) ||
                (width == 854 && height == 480 && pixelAspectNumerator == 1280 && pixelAspectDenominator == 1281))) return false;
            const auto final = offset + static_cast<std::int64_t>(stride)*(height-1);
            const auto low = (std::min)(offset,final), high = (std::max)(offset,final);
            if (low < 0 || high > static_cast<std::int64_t>(sourceBytes) || static_cast<std::uint64_t>(width)*4 > sourceBytes-static_cast<std::size_t>(high)) return false;
            result.assign(PosterBytes,0);
            for (std::size_t i = 3; i < result.size(); i += 4) result[i] = 255;
            const double displayWidth = static_cast<double>(width)*pixelAspectNumerator/pixelAspectDenominator;
            const double scale = (std::min)(static_cast<double>(Width)/displayWidth,static_cast<double>(Height)/height);
            const auto drawWidth = (std::max)(1u,static_cast<unsigned>(std::lround(displayWidth*scale)));
            const auto drawHeight = (std::max)(1u,static_cast<unsigned>(std::lround(height*scale)));
            const auto left = (Width-drawWidth)/2, top = (Height-drawHeight)/2;
            for (unsigned y = 0; y < drawHeight; ++y)
                for (unsigned x = 0; x < drawWidth; ++x)
                {
                    const double sx = std::clamp((x+.5)*width/drawWidth-.5,0.0,static_cast<double>(width-1));
                    const double sy = std::clamp((y+.5)*height/drawHeight-.5,0.0,static_cast<double>(height-1));
                    const auto x0 = static_cast<unsigned>(sx), y0 = static_cast<unsigned>(sy);
                    const auto x1 = (std::min)(x0+1,width-1), y1 = (std::min)(y0+1,height-1);
                    const double fx = sx-x0, fy = sy-y0;
                    const auto at = [&](unsigned px,unsigned py,unsigned channel)
                    { return source[static_cast<std::size_t>(offset+static_cast<std::int64_t>(py)*stride)+px*4+channel]; };
                    for (unsigned channel = 0; channel < 3; ++channel)
                    {
                        const double a = at(x0,y0,channel)*(1-fx)+at(x1,y0,channel)*fx;
                        const double b = at(x0,y1,channel)*(1-fx)+at(x1,y1,channel)*fx;
                        result[(static_cast<std::size_t>(top+y)*Width+left+x)*4+channel] = static_cast<std::uint8_t>(std::lround(a*(1-fy)+b*fy));
                    }
                }
            return true;
        }
        catch (...) { result.clear(); return false; }
    }
    int RunStdio() noexcept
    {
        std::vector<std::uint8_t> poster;
        bool completed = false;
        try
        {
            const auto input = GetStdHandle(STD_INPUT_HANDLE);
            std::array<std::uint8_t,RequestHeaderBytes> header{}; ReadExact(input,header.data(),RequestHeaderBytes);
            const auto pathBytes = Get<std::uint32_t>(header.data()+4);
            Require(pathBytes > 0 && pathBytes <= MaximumPathBytes && !(pathBytes%2));
            std::vector<std::uint8_t> request(header.begin(),header.end()); request.resize(RequestHeaderBytes+pathBytes);
            ReadExact(input,request.data()+RequestHeaderBytes,pathBytes);
            std::uint8_t extra{}; DWORD count = 0;
            const BOOL end = ReadFile(input,&extra,1,&count,nullptr);
            Require((end && count == 0) || (!end && GetLastError() == ERROR_BROKEN_PIPE));
            Request parsed; Require(ParseRequest(request,parsed));
            poster = Decode(parsed); Require(poster.size() == PosterBytes); completed = true;
        }
        catch (const Failure& failure)
        {
#ifdef WISP_THUMBNAIL_DIAGNOSTICS
            std::fprintf(stderr,"{\"thumbnailStage\":\"%s\",\"hresult\":%lu,\"mediaPhase\":\"%s\",\"interlace\":%u,\"subtypeData1\":%lu,\"width\":%u,\"height\":%u,\"expectedWidth\":%u,\"expectedHeight\":%u,\"videoMajorType\":%s,\"aspectNumerator\":%u,\"aspectDenominator\":%u",
                diagnosticStage,static_cast<unsigned long>(failure.hr),diagnosticMediaPhase,
                diagnosticInterlace,static_cast<unsigned long>(diagnosticSubtype),diagnosticWidth,diagnosticHeight,
                diagnosticExpectedWidth,diagnosticExpectedHeight,diagnosticVideo ? "true" : "false",diagnosticAspectNumerator,diagnosticAspectDenominator);
            PrintDiagnosticAperture("minimumAperture",diagnosticMinimum); PrintDiagnosticAperture("geometricAperture",diagnosticGeometric);
            std::fprintf(stderr,"}\n");
#else
            (void)failure;
#endif
            poster.clear();
        }
        catch (...) { poster.clear(); }
        try
        {
            std::array<std::uint8_t,ResponseHeaderBytes> response{};
            std::memcpy(response.data(),"WTP1",4); Put(response.data()+4,completed ? 0u : 1u);
            if (completed)
            {
                Put(response.data()+8,Width); Put(response.data()+12,Height);
                Put(response.data()+16,Stride); Put(response.data()+20,PosterBytes);
            }
            const auto output = GetStdHandle(STD_OUTPUT_HANDLE);
            WriteExact(output,response.data(),ResponseHeaderBytes); if (completed) WriteExact(output,poster.data(),PosterBytes);
        }
        catch (...) { return 3; }
        return completed ? 0 : 3;
    }
}
