#include "ClipThumbnail.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <iostream>
#include <limits>
#include <mfapi.h>

namespace recorder::thumbnail
{
    namespace
    {
        template<class T> void Put(std::uint8_t* bytes,T value)
        { for (std::size_t i = 0; i < sizeof(T); ++i) { bytes[i] = static_cast<std::uint8_t>(value & 255); value >>= 8; } }
        std::vector<std::uint8_t> Wire(std::wstring path = L"C:\\Clips\\0123456789abcdef0123456789abcdef.mp4",
            std::uint32_t width = 1920,std::uint32_t height = 1080)
        {
            std::vector<std::uint8_t> bytes(RequestHeaderBytes+path.size()*2);
            std::memcpy(bytes.data(),"WTR1",4); Put(bytes.data()+4,static_cast<std::uint32_t>(path.size()*2));
            Put(bytes.data()+8,static_cast<std::uint64_t>(12345)); Put(bytes.data()+16,width); Put(bytes.data()+20,height);
            Put(bytes.data()+24,static_cast<std::uint64_t>(45678));
            for (std::size_t i = 0; i < path.size(); ++i) Put(bytes.data()+RequestHeaderBytes+i*2,static_cast<std::uint16_t>(path[i]));
            return bytes;
        }
    }
    unsigned RunContracts()
    {
        unsigned checks = 0;
        const auto check = [&](bool value) { if (!value) throw checks; ++checks; };
        check(IsProgressiveFrame(MFVideoInterlace_Progressive,-1,-1,-1));
        check(IsProgressiveFrame(MFVideoInterlace_Progressive,0,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_Progressive,1,-1,-1));
        check(IsProgressiveFrame(MFVideoInterlace_MixedInterlaceOrProgressive,0,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_MixedInterlaceOrProgressive,-1,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_MixedInterlaceOrProgressive,1,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_Unknown,0,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_FieldSingleUpper,0,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_FieldInterleavedUpperFirst,0,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_Progressive,0,1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_Progressive,0,-1,1));
        check(!IsProgressiveFrame(MFVideoInterlace_Progressive,2,-1,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_Progressive,0,-2,-1));
        check(!IsProgressiveFrame(MFVideoInterlace_Progressive,0,-1,2));
        std::int64_t cropOffset = 0;
        check(GetCropOffset(1920,1088,1920,1080,{0,0,1920,1080},7680,cropOffset) && cropOffset == 0);
        check(GetCropOffset(1920,1088,1920,1080,{0,0,1920,1080},-7680,cropOffset) && cropOffset == 7680ll*1087);
        check(GetCropOffset(1920,1088,1920,1080,{0,8,1920,1080},-7680,cropOffset) && cropOffset == 7680ll*1079);
        check(!GetCropOffset(1920,1088,1920,1080,{0,0,1920,1088},7680,cropOffset));
        check(!GetCropOffset(1920,1088,1920,1080,{0,9,1920,1080},7680,cropOffset));
        check(!GetCropOffset(1920,1088,1920,1080,{-1,0,1920,1080},7680,cropOffset));
        check(!GetCropOffset(1920,1088,1920,1080,{0,0,1920,1080,1,0},7680,cropOffset));
        check(!GetCropOffset(1920,1088,1920,1080,{0,0,1920,1080,0,1},7680,cropOffset));
        check(!GetCropOffset(1920,1120,1920,1080,{0,0,1920,1080},7680,cropOffset));
        check(!GetCropOffset(864,480,854,480,{0,0,854,480},854*4,cropOffset));
        check(GetCropOffset(864,480,854,480,{2,0,854,480},864*4,cropOffset) && cropOffset == 8);
        check(GetCropOffset(3840,2176,3840,2160,{0,0,3840,2160},3840*4,cropOffset));
        Request request;
        check(ParseRequest(Wire(),request) && request.fileBytes == 12345 && request.lastWriteFileTime == 45678 && request.width == 1920);
        check(ParseRequest(Wire(L"D:\\0123456789abcdef0123456789abcdef.mp4"),request));
        for (const unsigned height : {360u,480u,720u,1080u,1440u,2160u})
            check(ParseRequest(Wire(L"C:\\Clips\\0123456789abcdef0123456789abcdef.mp4",height == 480 ? 854u : height*16/9,height),request));
        check(ParseRequest(Wire(L"C:\\Clips\\\u00e9\U0001f697\\0123456789abcdef0123456789abcdef.mp4"),request));
        for (const auto* path : {L"Clips\\0123456789abcdef0123456789abcdef.mp4", L"C:Clips\\0123456789abcdef0123456789abcdef.mp4",
            L"\\\\server\\share\\0123456789abcdef0123456789abcdef.mp4", L"\\\\?\\C:\\Clips\\0123456789abcdef0123456789abcdef.mp4",
            L"C:\\Clips\\..\\0123456789abcdef0123456789abcdef.mp4", L"C:\\Clips.\\0123456789abcdef0123456789abcdef.mp4",
            L"C:\\NUL\\0123456789abcdef0123456789abcdef.mp4", L"C:\\LPT1.data\\0123456789abcdef0123456789abcdef.mp4",
            L"C:\\Clips\\00000000000000000000000000000000.mp4", L"C:\\Clips\\0123456789abcdef0123456789abcdef.mp4:stream",
            L"C:\\Clips\\0123456789abcdef0123456789abcdef.MP4", L"C:\\Clips\\clip.mp4",L"C:\\Clips\\\\0123456789abcdef0123456789abcdef.mp4"})
            check(!ParseRequest(Wire(path),request));
        auto malformed = Wire(); malformed[0] = 'x'; check(!ParseRequest(malformed,request));
        malformed = Wire(); malformed.push_back(0); check(!ParseRequest(malformed,request));
        malformed = Wire(); malformed.resize(RequestHeaderBytes-1); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+4,MaximumPathBytes+2); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+4,static_cast<std::uint32_t>(1)); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+8,MaximumFileBytes+1); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+8,static_cast<std::uint64_t>(0)); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+24,static_cast<std::uint64_t>(0)); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+16,static_cast<std::uint32_t>(3841)); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+RequestHeaderBytes+6,static_cast<std::uint16_t>(0)); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+RequestHeaderBytes+6,static_cast<std::uint16_t>(0xd800)); check(!ParseRequest(malformed,request));
        malformed = Wire(); Put(malformed.data()+RequestHeaderBytes+6,static_cast<std::uint16_t>(0xdc00)); check(!ParseRequest(malformed,request));
        const std::array<std::uint8_t,16> picture{0,0,255,0, 0,255,0,0, 255,0,0,0, 255,255,255,0};
        std::vector<std::uint8_t> poster, reversedPoster;
        check(MakePoster(picture.data(),picture.size(),0,8,2,2,poster) && poster.size() == PosterBytes);
        check(poster[0] == 0 && poster[1] == 0 && poster[2] == 0 && poster[3] == 255);
        const std::size_t red = 70*4, blue = (static_cast<std::size_t>(179)*Width+70)*4;
        check(poster[red] == 0 && poster[red+1] == 0 && poster[red+2] == 255);
        check(poster[blue] == 255 && poster[blue+1] == 0 && poster[blue+2] == 0);
        bool opaque = true; for (std::size_t i = 3; i < poster.size(); i += 4) opaque &= poster[i] == 255;
        check(opaque);
        const std::array<std::uint8_t,16> reverse{255,0,0,0, 255,255,255,0, 0,0,255,0, 0,255,0,0};
        check(MakePoster(reverse.data(),reverse.size(),8,-8,2,2,reversedPoster) && poster == reversedPoster);
        const auto center = (static_cast<std::size_t>(90)*Width+160)*4;
        check(poster[center] > 120 && poster[center] < 136 && poster[center+1] > 120 && poster[center+1] < 136 &&
            poster[center+2] > 120 && poster[center+2] < 136);
        check(!MakePoster(picture.data(),picture.size(),0,7,2,2,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),0,-8,2,2,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),0,8,2,3,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),-1,8,2,2,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),(std::numeric_limits<std::int64_t>::max)(),8,2,2,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),0,(std::numeric_limits<std::int32_t>::min)(),2,2,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),0,8,0,2,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),0,8,2,0,reversedPoster));
        check(!MakePoster(picture.data(),picture.size(),0,8,2,2,reversedPoster,0,1));
        check(!MakePoster(picture.data(),picture.size(),0,8,2,2,reversedPoster,1,0));
        std::vector<std::uint8_t> wide(static_cast<std::size_t>(854)*480*4,200);
        check(MakePoster(wide.data(),wide.size(),0,854*4,854,480,poster,1280,1281));
        check(poster[0] == 200 && poster[PosterBytes-4] == 200 && poster[3] == 255);
        check(!MakePoster(wide.data(),wide.size(),0,854*4,854,480,poster,1281,1280));
        return checks;
    }
}

#ifndef WISP_THUMBNAIL_NO_MAIN
int main()
{
    try
    {
        const auto checks = recorder::thumbnail::RunContracts();
        std::cout << "{\"mode\":\"thumbnail_cpu_contracts\",\"passed\":" << checks << ",\"mediaOpened\":false,\"graphicsInitialized\":false}\n";
        return 0;
    }
    catch (...) { std::cout << "{\"passed\":false,\"reason\":\"thumbnail_contract_failed\"}\n"; return 1; }
}
#endif
