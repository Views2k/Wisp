#include "NvencLosslessVideoSession.h"
#include "HdrFrameConverter.h"
#include "Mp4ClipWriter.h"
#include <mfapi.h>
#include <mfreadwrite.h>
#include <codecapi.h>
#include <d3d10.h>
#include <dxgi1_2.h>
#include <DirectXPackedVector.h>
#include <tlhelp32.h>
#include <array>
#include <algorithm>
#include <cstring>
#include <cwchar>
#include <iostream>
#include <memory>
#include <thread>
#include <string>
#include <vector>
namespace {
using namespace recorder;
using Microsoft::WRL::ComPtr;
using namespace DirectX::PackedVector;
#ifdef WISP_HDR_FIXTURE_4K
constexpr UINT Width=3840, Height=2160;
#else
constexpr UINT Width=1920, Height=1080;
#endif
constexpr UINT Rate=60, FrameCount=16;
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
                if (WaitForSingleObject(done_.Get(), 30000) != WAIT_OBJECT_0)
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
        PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
        Win(Process32FirstW(processes.Get(), &entry) != FALSE, "process_enumeration_failed");
        do
        {
            Require(_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") != 0 && _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") != 0 &&
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") != 0 && _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") != 0 &&
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
// RGB values are absolute scRGB (1=80nits), quantized to FP16 by the source.
// First row: black,80,203,280nits; second:600,1000nits,18% of80nits,20nits.
constexpr std::array<std::array<float,3>,16> Colors{{
    {0,0,0},{1,1,1},{2.5375f,2.5375f,2.5375f},{3.5f,3.5f,3.5f},
    {7.5f,7.5f,7.5f},{12.5f,12.5f,12.5f},{.18f,.18f,.18f},{.25f,.25f,.25f},
    {1,0,0},{0,1,0},{0,0,1},{0,1,1},{1,0,1},{1,1,0},{4,.25f,.125f},{.125f,.5f,2}}};
void Write(HANDLE file, const void* data, DWORD bytes)
{
    DWORD actual=0; Win(WriteFile(file,data,bytes,&actual,nullptr)!=FALSE,"fixture_write_failed");
    Require(actual==bytes,"fixture_write_incomplete");
}
class Frames final : public encoder::FrameWriter {
    ComPtr<ID3D11Texture2D> source_, staging_;
    ComPtr<ID3D11DeviceContext> context_;
    hdr::HdrFrameConverter converter_;
    std::vector<HALF> pixels_;
    bool lossless_ = false, motion_ = false;
    void SetPixels(UINT frame) noexcept {
        std::array<std::array<HALF,4>,16> colors{};
        for(UINT tile=0;tile<16;++tile) {
            const auto& rgb=Colors[(tile+frame)%16];
            for(UINT channel=0;channel<3;++channel) colors[tile][channel]=XMConvertFloatToHalf(rgb[channel]);
            colors[tile][3]=XMConvertFloatToHalf(1);
        }
        for(UINT y=0;y<Height;++y) for(UINT x=0;x<Width;++x) {
            const auto& pixel=colors[(y/(Height/4))*4+x/(Width/4)];
            const size_t at=(static_cast<size_t>(y)*Width+x)*4;
            for(UINT channel=0;channel<4;++channel) pixels_[at+channel]=pixel[channel];
        }
    }
public:
    hdr::Evidence evidence;
    void Initialize(ID3D11Device* device, bool lossless, bool motion) {
        lossless_ = lossless; motion_ = motion;
        device->GetImmediateContext(&context_);
        pixels_.resize(static_cast<size_t>(Width)*Height*4);
        SetPixels(0);
        D3D11_TEXTURE2D_DESC desc{}; desc.Width=Width;desc.Height=Height;
        desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
        desc.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{pixels_.data(),Width*8,0};
        Check(device->CreateTexture2D(&desc,&data,&source_),"source_create_failed");
        desc.Format=lossless_?DXGI_FORMAT_R16_UINT:DXGI_FORMAT_P010;
        if(lossless_)desc.Height*=3;
        desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        Check(device->CreateTexture2D(&desc,nullptr,&staging_),"oracle_create_failed");
        if(!converter_.Initialize(device,Width,Height,hdr::SourceEncoding::LinearScRgbFp16,0,
            {Width,Height,Rate,1,1},lossless_?hdr::OutputEncoding::PreparedPqGbrPlanar16:hdr::OutputEncoding::Bt2020PqP010,evidence)) throw Failure{evidence.reason,evidence.hr};
    }
    HRESULT Fill(UINT index,ID3D11Texture2D* target) noexcept override {
        if(index>=FrameCount) return E_INVALIDARG;
        if(motion_ && index!=0) {
            SetPixels(index);
            // The same immediate-context queue orders this upload after the
            // previous conversion and before this frame's conversion.
            context_->UpdateSubresource(source_.Get(),0,nullptr,pixels_.data(),Width*8,0);
        }
        if(!converter_.Submit(source_.Get(),target,evidence)) return evidence.hr;
        if(index==0) context_->CopyResource(staging_.Get(),target);
        return S_OK;
    }
    void WriteOracle(const std::wstring& path) {
        Handle file(CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
        Win(file.Get()!=INVALID_HANDLE_VALUE,"oracle_file_failed");
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(context_->Map(staging_.Get(),0,D3D11_MAP_READ,0,&mapped),"oracle_map_failed");
        try {
            for(UINT row=0;row<(lossless_?Height*3:Height*3/2);++row)
                Write(file.Get(),static_cast<const BYTE*>(mapped.pData)+static_cast<size_t>(row)*mapped.RowPitch,Width*2);
        } catch(...) {context_->Unmap(staging_.Get(),0);throw;}
        context_->Unmap(staging_.Get(),0);
    }
};
class Packets final : public encoder::PacketObserver, public exporting::PacketSource {
    struct Packet {std::vector<BYTE> bytes;LONGLONG time,duration;bool key;};
    std::vector<Packet> packets_;
    exporting::ClipDescription description_{};
    size_t read_=0, bytes_=0;
public:
    HRESULT OnConfiguration(IMFMediaType* type,const encoder::EncodeConfig&) noexcept override {
        try {
            GUID subtype{};Check(type->GetGUID(MF_MT_SUBTYPE,&subtype),"subtype_missing");
            Require(subtype==MFVideoFormat_HEVC,"hevc_required");
            UINT32 size=0;Check(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER,&size),"header_missing");
            Require(size>0&&size<=65536,"header_bound"); description_.h264SequenceHeader.resize(size);
            Check(type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER,description_.h264SequenceHeader.data(),size,nullptr),"header_read_failed");
            return S_OK;
        } catch(const Failure& f) {return f.hr;} catch(...) {return E_FAIL;}
    }
    HRESULT OnPacket(IMFMediaType*,IMFSample* sample) noexcept override {
        try {
            Require(packets_.size()<FrameCount,"too_many_frames");Packet packet{};
            Check(sample->GetSampleTime(&packet.time),"time_missing");Check(sample->GetSampleDuration(&packet.duration),"duration_missing");
            Require(packet.time==FrameTime(static_cast<UINT>(packets_.size())),"time_mismatch");
            UINT32 clean=0;Check(sample->GetUINT32(MFSampleExtension_CleanPoint,&clean),"key_missing");packet.key=clean!=0;
            ComPtr<IMFMediaBuffer> buffer;Check(sample->ConvertToContiguousBuffer(&buffer),"packet_buffer_failed");
            DWORD size=0;Check(buffer->GetCurrentLength(&size),"packet_size_failed");
            Require(size>0&&size<=16*1024*1024&&bytes_+size<=64*1024*1024,"packet_bound");
            packet.bytes.resize(size);BYTE* data=nullptr;Check(buffer->Lock(&data,nullptr,nullptr),"packet_lock_failed");
            std::memcpy(packet.bytes.data(),data,size);Check(buffer->Unlock(),"packet_unlock_failed");bytes_+=size;
            packets_.push_back(std::move(packet));description_.videoPackets=static_cast<UINT>(packets_.size());
            description_.end100ns=packets_.back().time+packets_.back().duration;return S_OK;
        } catch(const Failure& f) {return f.hr;} catch(...) {return E_FAIL;}
    }
    const exporting::ClipDescription& Description() const noexcept override {return description_;}
    HRESULT Read(bool audio,exporting::PacketView& view) noexcept override {
        view={};if(audio||read_==packets_.size())return S_FALSE;
        const auto& p=packets_[read_++];view={p.bytes.data(),p.bytes.size(),p.time,p.duration,p.key};return S_OK;
    }
};
}
int wmain(int argc,wchar_t** argv) {
    bool lossless=false,motion=false,valid=argc>=2&&argc<=4;
    for(int index=1;valid&&index<argc-1;++index) {
        if(wcscmp(argv[index],L"--lossless")==0&&!lossless) lossless=true;
        else if(wcscmp(argv[index],L"--motion")==0&&!motion) motion=true;
        else valid=false;
    }
    if(!valid) {std::cout<<"[--lossless] [--motion] NEW_LOCAL_DIRECTORY:16 synthetic"<<Height<<"p60 HDR frames; no capture/audio/window.30s watchdog.\n";return 2;}
    std::unique_ptr<Deadline> deadline;bool com=false,mf=false,complete=false;
    const char* reason="not_started";HRESULT hr=S_OK;lossless::Evidence encoding{};exporting::ExportEvidence mux{};
    try {
        deadline=std::make_unique<Deadline>();RequireApplicationsClosed();
        const auto dir=LocalPath(argv[argc-1]);const auto parents=HoldParents(dir);
        Win(CreateDirectoryW(dir.c_str(),nullptr)!=FALSE,"new_directory_required");
        const auto owned=HoldParents(dir+L"\\fixture.mp4");
        Check(CoInitializeEx(nullptr,COINIT_MULTITHREADED),"com_failed");com=true;
        Check(MFStartup(MF_VERSION),"mf_failed");mf=true;
        {
            ComPtr<IDXGIFactory1> factory;ComPtr<IDXGIAdapter1> adapter;
            Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)),"factory_failed");
            for(UINT index=0;;++index) {
                Check(factory->EnumAdapters1(index,&adapter),"nvidia_adapter_missing");
                DXGI_ADAPTER_DESC1 desc{};Check(adapter->GetDesc1(&desc),"adapter_failed");
                if(desc.VendorId==0x10de&&!(desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE))break;adapter.Reset();
            }
            ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;ComPtr<ID3D10Multithread> multithread;
            const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0};D3D_FEATURE_LEVEL selected{};
            Check(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_VIDEO_SUPPORT|D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels,2,D3D11_SDK_VERSION,&device,&selected,&context),"device_failed");
            Check(device.As(&multithread),"multithread_missing");multithread->SetMultithreadProtected(TRUE);
            Frames frames;frames.Initialize(device.Get(),lossless,motion);Packets packets;std::atomic<bool> cancelled{false};
            lossless::NvencLosslessVideoSession session;
            const UINT chroma=lossless?0:MFVideoChromaSubsampling_MPEG2|MFVideoChromaSubsampling_ProgressiveChroma;
            const encoder::EncodeConfig config{Width,Height,Rate,lossless?0u:40000000u,1,1,chroma};
            try {
                if(!session.Initialize(device.Get(),config,{3000,0,lossless?lossless::VideoMode::HdrLosslessGbr444:lossless::VideoMode::HdrMain10},cancelled,packets))throw Failure{session.Result().reason,session.Result().hr};
                UINT frame=0;const ULONGLONG began=GetTickCount64();
                while(frame<FrameCount) {
                    Require(GetTickCount64()-began<15000,"submit_timeout");
                    if(!session.Pump())throw Failure{session.Result().reason,session.Result().hr};
                    if(session.CanAcceptInput()) {
                        const auto result=session.TrySubmit(frame,FrameTime(frame),frames);
                        Require(result!=encoder::SubmitResult::Failed,session.Result().reason);
                        if(result==encoder::SubmitResult::Submitted)++frame;
                    }
                    Sleep(1);
                }
                if(!session.Drain())throw Failure{session.Result().reason,session.Result().hr};
                Check(session.Close(),"session_close_failed");encoding=session.Result();
            } catch(...) {encoding=session.Result();(void)session.Close();throw;}
            Require(packets.Description().videoPackets==FrameCount,"frame_count_mismatch");
            frames.WriteOracle(dir+(lossless?L"\\prepared.gbrp16":L"\\prepared.p010"));
            ComPtr<IMFByteStream> output;
            Check(MFCreateFile(MF_ACCESSMODE_READWRITE,MF_OPENMODE_FAIL_IF_EXIST,MF_FILEFLAGS_NONE,(dir+L"\\fixture.mp4").c_str(),&output),"mp4_create_failed");
            const exporting::VideoFormat format{Width,Height,Rate,lossless?0u:40000000u,MFVideoPrimaries_BT2020,MFVideoTransFunc_2084,
                static_cast<UINT>(lossless?MFVideoTransferMatrix_Identity:MFVideoTransferMatrix_BT2020_10),static_cast<UINT>(lossless?MFNominalRange_0_255:MFNominalRange_16_235),
                static_cast<UINT>(lossless?eAVEncH265VProfile_Main_444_10:eAVEncH265VProfile_Main_420_10),1,1,chroma,
                lossless?exporting::VideoEncoding::HevcLosslessPqGbr444:exporting::VideoEncoding::HevcMain10Pq420};
            mux=exporting::WritePacketStream(output.Get(),packets,format,cancelled);
            if(!mux.completed)throw Failure{mux.reason,mux.hr};complete=true;reason="hdr_fixture_complete";
        }
    }catch(const Failure& f){reason=f.reason;hr=f.hr;}catch(...){reason="fixture_unexpected_failure";hr=E_FAIL;}
    if(mf){const auto stopped=MFShutdown();if(FAILED(stopped)){complete=false;hr=stopped;reason="mf_shutdown_failed";}}
    if(com)CoUninitialize();
    std::cout<<"{\"completed\":"<<(complete?"true":"false")<<",\"reason\":\""<<reason<<"\",\"hresult\":"<<static_cast<UINT>(hr)
        <<",\"nvencStatus\":"<<encoding.nvencStatus<<",\"encodedFrames\":"<<encoding.outputSamples<<",\"muxedFrames\":"<<mux.samplesWritten
        <<",\"width\":"<<Width<<",\"height\":"<<Height<<",\"frameRate\":"<<Rate
        <<",\"codec\":\"hevc\",\"pixelFormat\":\""<<(lossless?"gbrp10le":"yuv420p10le")<<"\",\"range\":\""<<(lossless?"full":"limited")
        <<"\",\"primaries\":\"bt2020\",\"transfer\":\"smpte2084\",\"motion\":"<<(motion?"true":"false")<<",\"captureUsed\":false}\n";
    return complete?0:3;
}
