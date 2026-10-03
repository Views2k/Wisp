#include "HdrFrameConverter.h"

#include <DirectXPackedVector.h>
#include <d3d10_1.h>
#include <dxgi1_2.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cwchar>
#include <iomanip>
#include <iostream>
#include <limits>
#include <thread>
#include <utility>
#include <vector>

namespace
{
    using namespace recorder::hdr;
    using namespace DirectX::PackedVector;
    using Microsoft::WRL::ComPtr;
    using Clock = std::chrono::steady_clock;
    constexpr UINT SourceWidth = 3840, SourceHeight = 2160, Tolerance = 2;
    std::atomic<bool> cancelled{ false };
    struct Failure { const char* reason; HRESULT hr; };
    void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
    void Require(bool value, const char* reason) { if (!value) throw Failure{ reason, E_FAIL }; }
    BOOL WINAPI Cancel(DWORD signal) noexcept
    {
        if (signal != CTRL_C_EVENT && signal != CTRL_BREAK_EVENT) return FALSE;
        cancelled.store(true); return TRUE;
    }
    bool ForzaRunning()
    {
        const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        Check(snapshot == INVALID_HANDLE_VALUE ? HRESULT_FROM_WIN32(GetLastError()) : S_OK, "process_enumeration_failed");
        struct Close { HANDLE value; ~Close() { CloseHandle(value); } } close{ snapshot };
        PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
        Check(Process32FirstW(snapshot, &entry) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "process_enumeration_failed");
        do
        {
            if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0 || _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") == 0 ||
                _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") == 0 || _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") == 0) return true;
        } while (Process32NextW(snapshot, &entry));
        Check(GetLastError() == ERROR_NO_MORE_FILES ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "process_enumeration_failed");
        return false;
    }
    enum class Mode { Help, SelfTest, Fixture, SdrFixture };
    struct Options { Mode mode = Mode::Help; UINT adapter = 0, timeoutMs = 10000, whiteNits = 0; };
    bool Number(const wchar_t* text, UINT minimum, UINT maximum, UINT& result) noexcept
    {
        if (!text || !*text) return false;
        UINT value = 0;
        for (; *text; ++text)
        {
            if (*text < L'0' || *text > L'9') return false;
            const UINT digit = static_cast<UINT>(*text - L'0');
            if (value > (maximum - digit) / 10) return false;
            value = value * 10 + digit;
        }
        if (value < minimum || value > maximum) return false;
        result = value; return true;
    }
    bool Parse(int argc, const wchar_t* const* argv, Options& options) noexcept
    {
        options = {};
        if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--help") == 0)) return true;
        if (argc == 2 && wcscmp(argv[1], L"--self-test") == 0) { options.mode = Mode::SelfTest; return true; }
        if (argc < 2) return false;
        if (wcscmp(argv[1], L"--hdr-conversion-fixture") == 0) options.mode = Mode::Fixture;
        else if (wcscmp(argv[1], L"--sdr-conversion-fixture") == 0) options.mode = Mode::SdrFixture;
        else return false;
        UINT seen = 0;
        for (int i = 2; i < argc; i += 2)
        {
            if (i + 1 >= argc) return false;
            if (wcscmp(argv[i], L"--adapter-index") == 0)
            {
                if ((seen & 1) || !Number(argv[i + 1], 0, 15, options.adapter)) return false;
                seen |= 1;
            }
            else if (wcscmp(argv[i], L"--timeout-ms") == 0)
            {
                if ((seen & 2) || !Number(argv[i + 1], 1000, 30000, options.timeoutMs)) return false;
                seen |= 2;
            }
            else if (wcscmp(argv[i], L"--reference-white-nits") == 0)
            {
                if (options.mode != Mode::Fixture || (seen & 4) || !Number(argv[i + 1], 10, 1000, options.whiteNits)) return false;
                seen |= 4;
            }
            else return false;
        }
        return options.mode == Mode::SdrFixture || (seen & 4) != 0;
    }

    struct Rgb { double r, g, b; };
    const double NotFinite = (std::numeric_limits<double>::quiet_NaN)();
    const double Infinity = (std::numeric_limits<double>::infinity)();
    const std::array<Rgb, 16> Colors{{ {0,0,0}, {.001,.001,.001}, {.018,.018,.018}, {.18,.18,.18},
        {1,1,1}, {2,2,2}, {4,4,4}, {12.5,12.5,12.5}, {4,0,0}, {0,4,0}, {0,0,4},
        {-.5,1,.2}, {-1,-1,-1}, {NotFinite,1,1}, {Infinity,1,1}, {.0184,.0184,.0184} }};
    using Pixel = std::array<HALF, 4>;
    Pixel Pack(Rgb value)
    {
        return { XMConvertFloatToHalf(static_cast<float>(value.r)), XMConvertFloatToHalf(static_cast<float>(value.g)),
            XMConvertFloatToHalf(static_cast<float>(value.b)), XMConvertFloatToHalf(1.0f) };
    }
    Rgb Quantized(Rgb value)
    {
        const auto pixel = Pack(value);
        return { XMConvertHalfToFloat(pixel[0]), XMConvertHalfToFloat(pixel[1]), XMConvertHalfToFloat(pixel[2]) };
    }
    UINT ColorIndex(UINT patch, UINT frame) { return (patch + frame * 7) % static_cast<UINT>(Colors.size()); }
    Rgb Generated(UINT x, UINT y, UINT frame, UINT /*sourceSdrWhiteNits*/ = 80)
    {
        Rgb result{};
        if (frame < 2)
        {
            const UINT patch = (y / (OutputHeight / 4)) * 4 + x / (OutputWidth / 4);
            result = Colors[ColorIndex(patch, frame)];
        }
        else if (y < OutputHeight / 2)
        {
            const double value = static_cast<double>(x) / 120;
            result = { value, value, value };
        }
        else result = ((x / 7 + y / 5) & 1) ? Rgb{4,0,0} : Rgb{0,0,4};
        // Absolute scRGB pixels must not change with the Windows SDR-content
        // brightness metadata. Runs at 80 and 280 nits use identical inputs.
        return result;
    }
    double SrgbCode(double value)
    {
        return value <= .0031308 ? 12.92 * value : 1.055 * std::pow(value,1.0/2.4) - .055;
    }
    using Bgra = std::array<BYTE, 4>;
    constexpr std::array<Bgra, 16> SdrColors{{ {0,0,0,255}, {255,255,255,255},
        {0,0,255,255}, {0,255,0,255}, {255,0,0,255}, {255,255,0,255}, {255,0,255,255}, {0,255,255,255},
        {10,10,10,255}, {11,11,11,255}, {32,32,32,255}, {64,64,64,255}, {128,128,128,255},
        {192,192,192,255}, {23,109,217,255}, {201,71,13,255} }};
    Bgra SdrGenerated(UINT x, UINT y, UINT frame)
    {
        if (frame < 2)
        {
            const UINT patch = (y / (SourceHeight / 4)) * 4 + x / (SourceWidth / 4);
            // Frame 1 varies within each source 2x2 footprint. Replicating
            // output pixels would miss a decode-before-filter regression.
            const UINT variation = frame == 0 ? 0 : (x & 1) * 3 + (y & 1) * 7;
            return SdrColors[(patch + variation) % static_cast<UINT>(SdrColors.size())];
        }
        if (y < SourceHeight / 2)
        {
            const BYTE value = static_cast<BYTE>(x * 255 / (SourceWidth - 1));
            return { value, value, value, 255 };
        }
        return ((x / 7 + y / 5) & 1) ? Bgra{0,0,255,255} : Bgra{255,0,0,255};
    }
    double SrgbLinear(double value)
    {
        return value <= .04045 ? value / 12.92 : std::pow((value + .055) / 1.055, 2.4);
    }
    Rgb SdrReferenceCode(UINT x, UINT y, UINT frame)
    {
        // A normalized output pixel center maps to (2*x+.5,2*y+.5) in
        // source texel-index coordinates: four BGRA8_UNORM samples, each 1/4.
        // Filter quantized code values FIRST; _UNORM performs no sRGB decode.
        static_assert(SourceWidth == 2 * OutputWidth && SourceHeight == 2 * OutputHeight);
        Rgb filtered{};
        for (UINT row = 0; row < 2; ++row)
            for (UINT column = 0; column < 2; ++column)
            {
                const auto pixel = SdrGenerated(x * 2 + column, y * 2 + row, frame);
                filtered.r += pixel[2] / 1020.0;
                filtered.g += pixel[1] / 1020.0;
                filtered.b += pixel[0] / 1020.0;
            }
        return filtered;
    }
    Rgb ReferenceCode(Rgb input, double /*sourceSdrWhiteNits*/, bool quantizeSource = true)
    {
        // Independent double-precision reference. FP16 quantization is modeled
        // before the appearance transform because that is the GPU source.
        if (quantizeSource) input = Quantized(input);
        if (!std::isfinite(input.r) || !std::isfinite(input.g) || !std::isfinite(input.b)) return {};
        const double scale = 80.0 / 300.0;
        const std::array<double,3> linear2020{{
            scale*(.62740389593469903*input.r+.32928303837788370*input.g+.043313065687417225*input.b),
            scale*(.069097289358232075*input.r+.91954039507545871*input.g+.011362315566309178*input.b),
            scale*(.016391438875150280*input.r+.088013307877225749*input.g+.89559525324762401*input.b) }};
        std::array<double,3> mapped{};
        for (UINT i = 0; i < mapped.size(); ++i)
        {
            const double light = (std::max)(0.0,linear2020[i]);
            mapped[i] = SrgbLinear(std::pow(light/(1+light),1.0/2.4));
        }
        const std::array<double,3> linear709{{
            1.6604910021084345*mapped[0]-.58764113878854951*mapped[1]-.072849863319884883*mapped[2],
            -.12455047452159074*mapped[0]+1.1328998971259603*mapped[1]-.0083494226043694768*mapped[2],
            -.018150763354905303*mapped[0]-.10057889800800739*mapped[1]+1.1187296613629127*mapped[2] }};
        return { SrgbCode(std::clamp(linear709[0],0.0,1.0)), SrgbCode(std::clamp(linear709[1],0.0,1.0)),
            SrgbCode(std::clamp(linear709[2],0.0,1.0)) };
    }
    struct Yuv { UINT y, u, v; };
    Yuv Matrix(Rgb code)
    {
        const double y = .2126 * code.r + .7152 * code.g + .0722 * code.b;
        return { static_cast<UINT>(std::floor(16 + 219 * y + .5)),
            static_cast<UINT>(std::floor(128 + 112 * (code.b - y) / .9278 + .5)),
            static_cast<UINT>(std::floor(128 + 112 * (code.r - y) / .7874 + .5)) };
    }
    Yuv ReferenceAt(UINT x, UINT y, UINT frame, UINT whiteNits, bool sdr = false)
    {
        return Matrix(sdr ? SdrReferenceCode(x, y, frame) : ReferenceCode(Generated(x, y, frame,whiteNits), whiteNits));
    }
    Yuv ReferenceChroma(UINT x, UINT y, UINT frame, UINT whiteNits, bool sdr = false)
    {
        Rgb filtered{};
        for (int row = 0; row < 2; ++row)
            for (int column = -1; column <= 1; ++column)
            {
                const int sampleX = std::clamp(static_cast<int>(x) + column, 0, static_cast<int>(OutputWidth) - 1);
                const UINT sampleY = (std::min)(y + static_cast<UINT>(row), OutputHeight - 1);
                const auto code = sdr ? SdrReferenceCode(static_cast<UINT>(sampleX), sampleY, frame) :
                    ReferenceCode(Generated(static_cast<UINT>(sampleX), sampleY, frame,whiteNits), whiteNits);
                const double weight = column == 0 ? .25 : .125;
                filtered.r += code.r * weight; filtered.g += code.g * weight; filtered.b += code.b * weight;
            }
        return Matrix(filtered);
    }
    struct Measurement { UINT checked = 0, maximumError = 0; };
    void Record(Measurement& result, UINT observed, UINT expected)
    {
        ++result.checked;
        const UINT error = observed > expected ? observed - expected : expected - observed;
        result.maximumError = (std::max)(result.maximumError, error);
    }
    UINT ReadY(const D3D11_MAPPED_SUBRESOURCE& mapped, UINT x, UINT y)
    {
        return static_cast<const BYTE*>(mapped.pData)[static_cast<size_t>(y) * mapped.RowPitch + x];
    }
    void CheckPoint(const D3D11_MAPPED_SUBRESOURCE& mapped, UINT x, UINT y, UINT frame, UINT whiteNits, Measurement& result, bool sdr = false)
    {
        Record(result, ReadY(mapped, x, y), ReferenceAt(x, y, frame, whiteNits, sdr).y);
        const UINT chromaX = x & ~1u, chromaY = y & ~1u;
        const auto expected = ReferenceChroma(chromaX, chromaY, frame, whiteNits, sdr);
        const size_t offset = static_cast<size_t>(mapped.RowPitch) * OutputHeight +
            static_cast<size_t>(chromaY / 2) * mapped.RowPitch + chromaX;
        const auto* bytes = static_cast<const BYTE*>(mapped.pData);
        Record(result, bytes[offset], expected.u); Record(result, bytes[offset + 1], expected.v);
    }
    void CheckNeutralAnchor(const D3D11_MAPPED_SUBRESOURCE& mapped, UINT x, UINT y, UINT luma, Measurement& result)
    {
        // Fixed policy/BT.709 checkpoints, independent of ReferenceCode and
        // the shader. Uniform patch interiors avoid chroma-filter boundaries.
        Record(result,ReadY(mapped,x,y),luma);
        const size_t offset = static_cast<size_t>(mapped.RowPitch) * OutputHeight +
            static_cast<size_t>(y / 2) * mapped.RowPitch + (x & ~1u);
        const auto* bytes = static_cast<const BYTE*>(mapped.pData);
        Record(result,bytes[offset],128); Record(result,bytes[offset+1],128);
    }

    UINT SelfTest()
    {
        UINT passed = 0;
        Options options;
        const wchar_t* defaults[]{ L"fixture" };
        if (!Parse(1, defaults, options) || options.mode != Mode::Help) return 0;
        ++passed;
        const wchar_t* valid[]{ L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"80",L"--adapter-index",L"15",L"--timeout-ms",L"30000" };
        if (!Parse(8, valid, options) || options.whiteNits != 80 || options.adapter != 15 || options.timeoutMs != 30000) return 0;
        ++passed;
        const wchar_t* invalid[][6]{ {L"fixture",L"--hdr-conversion-fixture"}, {L"fixture",L"--capture"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"9"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"1001"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"NaN"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"-80"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"80.0"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"80",L"--reference-white-nits",L"80"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"80",L"--adapter-index",L"16"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"80",L"--timeout-ms",L"999"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits",L"80",L"--timeout-ms",L"30001"},
            {L"fixture",L"--self-test",L"--hdr-conversion-fixture"},
            {L"fixture",L"--hdr-conversion-fixture",L"--reference-white-nits"} };
        for (const auto& args : invalid)
        {
            int argc = 0; while (argc < 6 && args[argc]) ++argc;
            if (Parse(argc, args, options)) return 0;
            ++passed;
        }
        if (ValidateConfiguration(3840,2160,SourceEncoding::LinearScRgbFp16,80) ||
            ValidateConfiguration(1920,1080,SourceEncoding::LinearScRgbFp16,1000) ||
            ValidateConfiguration(1920,1200,SourceEncoding::LinearScRgbFp16,80) ||
            ValidateConfiguration(1920,1080,SourceEncoding::LinearScRgbFp16,0)) return 0;
        ++passed;
        if (!ValidateConfiguration(3840,2160,SourceEncoding::Unknown,80) ||
            !ValidateConfiguration(3840,2160,SourceEncoding::LinearScRgbFp16,static_cast<float>(NotFinite)) ||
            !ValidateConfiguration(7680,2160,SourceEncoding::LinearScRgbFp16,80)) return 0;
        ++passed;
        if (std::abs(SrgbCode(.001) - .01292) > 1e-12 || std::abs(SrgbCode(1) - 1) > 1e-12 ||
            std::abs(SrgbCode(.018) - .1428256813030392) > 1e-12) return 0;
        ++passed;
        const auto black = Matrix(ReferenceCode({0,0,0},80));
        const auto white = Matrix(ReferenceCode({1,1,1},80));
        if (black.y != 16 || black.u != 128 || black.v != 128 || white.y != 130 || white.u != 128 || white.v != 128) return 0;
        ++passed;
        // Neutral primaries cancel. This scalar identity is an independent
        // oracle that does not repeat the shader's matrices or sRGB stages.
        const std::array<double,8> toneAnchors{{0,.018,.18,.5,.75,1,2,4}};
        for (const auto& point : toneAnchors)
        {
            const auto code = ReferenceCode({point,point,point},80,false);
            const double reference = std::pow(point/(point+3.75),1.0/2.4);
            if (std::abs(code.r-reference) > 1e-12 || std::abs(code.g-reference) > 1e-12 ||
                std::abs(code.b-reference) > 1e-12) return 0;
            ++passed;
        }
        const std::array<std::pair<Rgb,Rgb>,4> colorAnchors{{
            {{4,0,0},{.8274693285783914,.23784788357932074,.12915317602353804}},
            {{0,4,0},{.43937780978744045,.7665988373951503,.27553180960006773}},
            {{0,0,4},{.24886375367637553,.11569505626621734,.7781174963398488}},
            {{.5,.25,.125},{.41276179534756063,.3154543410331486,.24021348633611728}} }};
        for (const auto& point : colorAnchors)
        {
            const auto code = ReferenceCode(point.first,80,false);
            if (std::abs(code.r-point.second.r) > 1e-12 || std::abs(code.g-point.second.g) > 1e-12 ||
                std::abs(code.b-point.second.b) > 1e-12) return 0;
            ++passed;
        }
        const std::array<std::pair<double,UINT>,10> lumaAnchors{{ {0,16}, {.001,23}, {.018,40}, {.18,77},
            {.5,106}, {.75,120}, {1,130}, {2,157}, {4,182}, {12.5,212} }};
        for (const auto& point : lumaAnchors)
        {
            const auto yuv = Matrix(ReferenceCode({point.first,point.first,point.first},80));
            if (yuv.y != point.second || yuv.u != 128 || yuv.v != 128) return 0;
            ++passed;
        }
        const auto diffuse = ReferenceCode({1,1,1},80), brighter = ReferenceCode({2,2,2},80), brightest = ReferenceCode({12.5,12.5,12.5},80);
        if (!(diffuse.r < brighter.r && brighter.r < brightest.r && brightest.r < 1)) return 0;
        ++passed;
        for (UINT i = 11; i < Colors.size(); ++i)
        {
            const auto code = ReferenceCode(Colors[i],80);
            if (!std::isfinite(code.r) || !std::isfinite(code.g) || !std::isfinite(code.b) ||
                code.r < 0 || code.r > 1 || code.g < 0 || code.g > 1 || code.b < 0 || code.b > 1) return 0;
            if (i >= 12 && i <= 14 && (code.r != 0 || code.g != 0 || code.b != 0)) return 0;
        }
        ++passed;
        for (const double sourceWhite : {0.0,80.0,160.0,280.0,480.0,1000.0})
            for (const auto& point : colorAnchors)
            {
                const auto a = ReferenceCode(point.first,80), b = ReferenceCode(point.first,sourceWhite);
                if (a.r != b.r || a.g != b.g || a.b != b.b) return 0;
                ++passed;
            }
        // A left-sited edge must differ from a centered 2x2 chroma box.
        const auto left = ReferenceChroma(6,600,2,80);
        const auto a = ReferenceCode(Generated(6,600,2),80), b = ReferenceCode(Generated(7,600,2),80);
        const auto centered = Matrix({(a.r+b.r)/2,(a.g+b.g)/2,(a.b+b.b)/2});
        if (left.u == centered.u && left.v == centered.v) return 0;
        ++passed;
        D3D11_TEXTURE2D_DESC input{}, output{};
        input.Width = SourceWidth; input.Height = SourceHeight; input.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        input.ArraySize = input.MipLevels = input.SampleDesc.Count = 1; input.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        output = input; output.Width = OutputWidth; output.Height = OutputHeight;
        output.Format = DXGI_FORMAT_NV12; output.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (ValidateSurfaces(input,output,SourceWidth,SourceHeight)) return 0;
        ++passed;
        input.BindFlags = 0;
        if (!ValidateSurfaces(input,output,SourceWidth,SourceHeight)) return 0;
        ++passed;
        input.BindFlags = D3D11_BIND_SHADER_RESOURCE; input.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        if (!ValidateSurfaces(input,output,SourceWidth,SourceHeight)) return 0;
        ++passed;
        const wchar_t* sdrDefault[]{ L"fixture",L"--sdr-conversion-fixture" };
        if (!Parse(2,sdrDefault,options) || options.mode != Mode::SdrFixture || options.whiteNits != 0) return 0;
        ++passed;
        const wchar_t* sdrValid[]{ L"fixture",L"--sdr-conversion-fixture",L"--adapter-index",L"15",L"--timeout-ms",L"30000" };
        if (!Parse(6,sdrValid,options) || options.mode != Mode::SdrFixture || options.adapter != 15 || options.timeoutMs != 30000) return 0;
        ++passed;
        const wchar_t* sdrInvalid[][6]{ {L"fixture",L"--sdr-conversion-fixture",L"--reference-white-nits",L"80"},
            {L"fixture",L"--sdr-conversion-fixture",L"--reference-white-nits",L"0"},
            {L"fixture",L"--sdr-conversion-fixture",L"--hdr-conversion-fixture"},
            {L"fixture",L"--hdr-conversion-fixture",L"--sdr-conversion-fixture"},
            {L"fixture",L"--sdr-conversion-fixture",L"--adapter-index",L"16"},
            {L"fixture",L"--sdr-conversion-fixture",L"--timeout-ms",L"999"},
            {L"fixture",L"--sdr-conversion-fixture",L"--timeout-ms",L"30001"},
            {L"fixture",L"--sdr-conversion-fixture",L"--adapter-index",L"0",L"--adapter-index",L"1"},
            {L"fixture",L"--sdr-conversion-fixture",L"--timeout-ms",L"1000",L"--timeout-ms",L"2000"},
            {L"fixture",L"--sdr-conversion-fixture",L"--adapter-index"} };
        for (const auto& args : sdrInvalid)
        {
            int argc = 0; while (argc < 6 && args[argc]) ++argc;
            if (Parse(argc,args,options)) return 0;
            ++passed;
        }
        if (ValidateConfiguration(SourceWidth,SourceHeight,SourceEncoding::SrgbBgra8,0) ||
            !ValidateConfiguration(SourceWidth,SourceHeight,SourceEncoding::SrgbBgra8,80) ||
            !ValidateConfiguration(SourceWidth,SourceHeight,SourceEncoding::SrgbBgra8,static_cast<float>(NotFinite))) return 0;
        ++passed;
        if (ValidateSurfaces(input,output,SourceWidth,SourceHeight,{},SourceEncoding::SrgbBgra8)) return 0;
        ++passed;
        input.Format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        if (!ValidateSurfaces(input,output,SourceWidth,SourceHeight,{},SourceEncoding::SrgbBgra8)) return 0;
        ++passed;
        input.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        if (!ValidateSurfaces(input,output,SourceWidth,SourceHeight,{},SourceEncoding::SrgbBgra8)) return 0;
        ++passed;
        if (std::abs(SrgbLinear(.04) - .04/12.92) > 1e-12 ||
            std::abs(SrgbLinear(.5) - .21404114048223255) > 1e-12 || SrgbLinear(1) != 1) return 0;
        ++passed;
        const auto sdrBlack = Matrix({0,0,0}), sdrWhite = Matrix({1,1,1});
        const auto middle = Matrix(SdrReferenceCode(OutputWidth/8,OutputHeight*7/8,0));
        if (sdrBlack.y != 16 || sdrWhite.y != 235 || middle.y != 126 ||
            sdrBlack.u != 128 || sdrBlack.v != 128 || sdrWhite.u != 128 || sdrWhite.v != 128 ||
            middle.u != 128 || middle.v != 128) return 0;
        ++passed;
        const auto red = Matrix({1,0,0}), blue = Matrix({0,0,1});
        if (red.y != 63 || red.u != 102 || red.v != 240 || blue.y != 32 || blue.u != 240 || blue.v != 118) return 0;
        ++passed;
        // Ensure the subpixel pattern distinguishes the intended code-value
        // filtering from decoding each source pixel before the resize.
        Rgb incorrectLinearAverage{};
        for (UINT y = 0; y < 2; ++y)
            for (UINT x = 0; x < 2; ++x)
            {
                const auto pixel = SdrGenerated(x,y,1);
                incorrectLinearAverage.r += SrgbLinear(pixel[2]/255.0)/4;
                incorrectLinearAverage.g += SrgbLinear(pixel[1]/255.0)/4;
                incorrectLinearAverage.b += SrgbLinear(pixel[0]/255.0)/4;
            }
        const auto correctResize = Matrix(SdrReferenceCode(0,0,1));
        const auto incorrectResize = Matrix({SrgbCode(incorrectLinearAverage.r),SrgbCode(incorrectLinearAverage.g),SrgbCode(incorrectLinearAverage.b)});
        if (std::abs(static_cast<int>(correctResize.y)-static_cast<int>(incorrectResize.y)) <= 10) return 0;
        ++passed;
        return passed;
    }

    template<class Guard>
    void CheckAspectFits(ID3D11Device* device, ID3D11DeviceContext* context, bool sdr,
        Guard& guard, Measurement& colors, UINT& frames, UINT& blackChecks)
    {
        // Reduced-size sources retain the exact reported display ratios. This
        // exercises the real shaders, not capture or full-resolution throughput.
        constexpr std::array<std::array<UINT, 2>, 4> sources{{ {344,144}, {512,144}, {192,120}, {192,108} }};
        constexpr std::array<recorder::conversion::OutputConfiguration, 2> outputs{{
            {640,360,30,1,1}, {854,480,30,1280,1281}
        }};
        const auto sourceColor = [sdr](UINT x, UINT y, UINT width, UINT height) -> Rgb
        {
            if (y < height / 2) return x < width / 2 ? Rgb{1,0,0} : Rgb{0,1,0};
            return x < width / 2 ? Rgb{0,0,1} : sdr ? Rgb{128/255.0,128/255.0,128/255.0} : Rgb{1,1,1};
        };
        for (const auto sourceSize : sources)
        {
            const UINT width = sourceSize[0], height = sourceSize[1];
            D3D11_TEXTURE2D_DESC inputDescription{};
            inputDescription.Width = width; inputDescription.Height = height;
            inputDescription.MipLevels = inputDescription.ArraySize = inputDescription.SampleDesc.Count = 1;
            inputDescription.Format = sdr ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R16G16B16A16_FLOAT;
            inputDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            std::vector<Bgra> sdrPixels(sdr ? static_cast<size_t>(width) * height : 0);
            std::vector<Pixel> hdrPixels(sdr ? 0 : static_cast<size_t>(width) * height);
            for (UINT y = 0; y < height; ++y)
                for (UINT x = 0; x < width; ++x)
                {
                    const auto color = sourceColor(x,y,width,height);
                    if (sdr) sdrPixels[static_cast<size_t>(y)*width+x] = {
                        static_cast<BYTE>(color.b*255), static_cast<BYTE>(color.g*255), static_cast<BYTE>(color.r*255), 255 };
                    else hdrPixels[static_cast<size_t>(y)*width+x] = Pack(color);
                }
            const D3D11_SUBRESOURCE_DATA initial{ sdr ? static_cast<const void*>(sdrPixels.data()) : hdrPixels.data(),
                width * static_cast<UINT>(sdr ? sizeof(Bgra) : sizeof(Pixel)), 0 };
            ComPtr<ID3D11Texture2D> input;
            Check(device->CreateTexture2D(&inputDescription,&initial,&input), "aspect_input_creation_failed");
            for (const auto output : outputs)
            {
                // Independent normalized-display oracle. All declared output
                // presets display as 16:9, including the non-square 480p pixels.
                const double sourceAspect = static_cast<double>(width) / height;
                const double fittedWidth = (std::min)(1.0, sourceAspect / (16.0/9));
                const double fittedHeight = (std::min)(1.0, (16.0/9) / sourceAspect);
                const auto reference = [&](int px, int py) -> Rgb
                {
                    px = std::clamp(px,0,static_cast<int>(output.width)-1);
                    py = std::clamp(py,0,static_cast<int>(output.height)-1);
                    const double u = ((px+.5)/output.width-.5)/fittedWidth+.5;
                    const double v = ((py+.5)/output.height-.5)/fittedHeight+.5;
                    if (u < 0 || u >= 1 || v < 0 || v >= 1) return {};
                    const double sx = u*width-.5, sy = v*height-.5;
                    const int ix = static_cast<int>(std::floor(sx)), iy = static_cast<int>(std::floor(sy));
                    const double fx = sx-ix, fy = sy-iy;
                    Rgb filtered{};
                    for (int row = 0; row < 2; ++row)
                        for (int column = 0; column < 2; ++column)
                        {
                            const auto color = sourceColor(static_cast<UINT>(std::clamp(ix+column,0,static_cast<int>(width)-1)),
                                static_cast<UINT>(std::clamp(iy+row,0,static_cast<int>(height)-1)),width,height);
                            const double weight = (column ? fx : 1-fx)*(row ? fy : 1-fy);
                            filtered.r += color.r*weight; filtered.g += color.g*weight; filtered.b += color.b*weight;
                        }
                    // Source endpoints 0/1 are exact in FP16; filtering occurs
                    // after source quantization, so do not requantize the blend.
                    return sdr ? filtered : ReferenceCode(filtered,80,false);
                };
                for (const auto encoding : {OutputEncoding::Bt709Nv12,OutputEncoding::PreparedRgbAyuv})
                {
                    guard();
                    const bool fullColor = encoding == OutputEncoding::PreparedRgbAyuv;
                    HdrFrameConverter converter;
                    Evidence evidence;
                    if (!converter.Initialize(device,width,height,sdr ? SourceEncoding::SrgbBgra8 : SourceEncoding::LinearScRgbFp16,
                        sdr ? 0.0f : 80.0f,output,encoding,evidence)) throw Failure{evidence.reason,evidence.hr};
                    auto description = inputDescription;
                    description.Width = output.width; description.Height = output.height;
                    description.Format = fullColor ? DXGI_FORMAT_AYUV : DXGI_FORMAT_NV12;
                    description.BindFlags = D3D11_BIND_RENDER_TARGET;
                    const UINT rowPitch = output.width*(fullColor ? 4u : 1u);
                    std::vector<BYTE> poison(static_cast<size_t>(rowPitch)*output.height*(fullColor ? 2u : 3u)/2,255);
                    const D3D11_SUBRESOURCE_DATA poisoned{poison.data(),rowPitch,0};
                    ComPtr<ID3D11Texture2D> destination;
                    Check(device->CreateTexture2D(&description,&poisoned,&destination), "aspect_output_creation_failed");
                    description.BindFlags = 0; description.Usage = D3D11_USAGE_STAGING; description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    ComPtr<ID3D11Texture2D> staging;
                    Check(device->CreateTexture2D(&description,nullptr,&staging), "aspect_readback_creation_failed");
                    const D3D11_QUERY_DESC queryDescription{D3D11_QUERY_EVENT,0};
                    ComPtr<ID3D11Query> completion;
                    Check(device->CreateQuery(&queryDescription,&completion), "aspect_completion_query_failed");
                    if (!converter.Submit(input.Get(),destination.Get(),evidence)) throw Failure{evidence.reason,evidence.hr};
                    context->CopyResource(staging.Get(),destination.Get()); context->End(completion.Get()); context->Flush();
                    for (;;)
                    {
                        guard();
                        BOOL ready = FALSE;
                        const HRESULT hr = context->GetData(completion.Get(),&ready,sizeof(ready),D3D11_ASYNC_GETDATA_DONOTFLUSH);
                        Check(hr, "aspect_completion_query_failed");
                        if (hr == S_OK && ready) break;
                        std::this_thread::sleep_for(std::chrono::milliseconds(1));
                    }
                    D3D11_MAPPED_SUBRESOURCE mapped{};
                    Check(context->Map(staging.Get(),0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&mapped), "aspect_readback_failed");
                    struct Unmap { ID3D11DeviceContext* context; ID3D11Texture2D* texture; ~Unmap() { context->Unmap(texture,0); } } unmap{context,staging.Get()};
                    Require(mapped.pData && mapped.RowPitch >= rowPitch, "aspect_readback_layout_invalid");
                    const auto* bytes = static_cast<const BYTE*>(mapped.pData);
                    if (sdr)
                    {
                        // Source gray 128 must remain RGB 128, or limited Y 126.
                        // These fixed anchors deliberately bypass the transfer oracle.
                        const UINT x = static_cast<UINT>(output.width*(.5+.25*fittedWidth)) & ~1u;
                        const UINT y = static_cast<UINT>(output.height*(.5+.25*fittedHeight)) & ~1u;
                        const size_t offset = static_cast<size_t>(mapped.RowPitch)*y+x*(fullColor ? 4u : 1u);
                        if (fullColor)
                            Require(bytes[offset] == 128 && bytes[offset+1] == 128 && bytes[offset+2] == 128 && bytes[offset+3] == 255,
                                "sdr_source_rgb_code_not_preserved");
                        else
                        {
                            const size_t uv = static_cast<size_t>(mapped.RowPitch)*(output.height+y/2)+x;
                            Require(bytes[offset] == 126 && bytes[uv] == 128 && bytes[uv+1] == 128,
                                "sdr_source_luma_code_not_preserved");
                        }
                    }
                    const auto checkPoint = [&](UINT x, UINT y)
                    {
                        const auto expected = reference(static_cast<int>(x),static_cast<int>(y));
                        if (fullColor)
                        {
                            const size_t offset = static_cast<size_t>(mapped.RowPitch)*y+x*4;
                            for (UINT channel = 0; channel < 4; ++channel)
                            {
                                const double value = channel == 0 ? expected.r : channel == 1 ? expected.b : channel == 2 ? expected.g : 1;
                                Record(colors,bytes[offset+channel],static_cast<UINT>(std::floor(value*255+.5)));
                            }
                            if (expected.r == 0 && expected.g == 0 && expected.b == 0)
                            {
                                Require(bytes[offset] == 0 && bytes[offset+1] == 0 && bytes[offset+2] == 0 && bytes[offset+3] == 255,
                                    "aspect_full_color_black_not_exact");
                                ++blackChecks;
                            }
                        }
                        else
                        {
                            const auto observedY = ReadY(mapped,x,y);
                            Record(colors,observedY,Matrix(expected).y);
                            if (expected.r == 0 && expected.g == 0 && expected.b == 0)
                            {
                                Require(observedY == 16, "aspect_luma_black_not_exact");
                                ++blackChecks;
                            }
                            Rgb filtered{};
                            for (int row = 0; row < 2; ++row)
                                for (int column = -1; column <= 1; ++column)
                                {
                                    const auto sample = reference(static_cast<int>(x&~1u)+column,static_cast<int>(y&~1u)+row);
                                    const double weight = column == 0 ? .25 : .125;
                                    filtered.r += sample.r*weight; filtered.g += sample.g*weight; filtered.b += sample.b*weight;
                                }
                            const auto chroma = Matrix(filtered);
                            const size_t offset = static_cast<size_t>(mapped.RowPitch)*(output.height+y/2)+(x&~1u);
                            Record(colors,bytes[offset],chroma.u); Record(colors,bytes[offset+1],chroma.v);
                            if (filtered.r == 0 && filtered.g == 0 && filtered.b == 0)
                            {
                                Require(bytes[offset] == 128 && bytes[offset+1] == 128, "aspect_chroma_black_not_exact");
                                ++blackChecks;
                            }
                        }
                    };
                    // Scan across every bar boundary and source edge in both
                    // axes, as well as all four asymmetric color quadrants.
                    for (UINT y = 0; y < output.height; ++y)
                    {
                        if (y % 64 == 0) guard();
                        checkPoint(output.width/4,y); checkPoint(output.width*3/4,y);
                    }
                    for (UINT x = 0; x < output.width; ++x)
                    {
                        if (x % 64 == 0) guard();
                        checkPoint(x,output.height/4); checkPoint(x,output.height*3/4);
                    }
                    ++frames;
                }
            }
        }
        Require(frames == 16 && blackChecks > 0 && colors.maximumError <= Tolerance, "aspect_geometry_or_color_check_failed");
    }

    int Run(const Options& options)
    {
        const bool sdr = options.mode == Mode::SdrFixture;
        Evidence evidence;
        Measurement patches, ramp, edges, boundaries, toneAnchors, aspectColors;
        UINT completedFrames = 0, aspectFrames = 0, aspectBlackChecks = 0;
        bool completed = false, rampMonotonic = false, highlightsDistinct = false, sdrRangeCorrect = false, sdrMidtoneCorrect = false;
        const auto started = Clock::now(), deadline = started + std::chrono::milliseconds(options.timeoutMs);
        auto lastGameCheck = started - std::chrono::seconds(1);
        const auto guard = [&]()
        {
            Require(!cancelled.load(), "cancelled"); Require(Clock::now() < deadline, "conversion_deadline_reached");
            if (Clock::now() - lastGameCheck >= std::chrono::milliseconds(100))
            {
                Require(!ForzaRunning(), "forza_running"); lastGameCheck = Clock::now();
            }
        };
        try
        {
            guard();
            ComPtr<IDXGIFactory1> factory;
            Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "adapter_factory_failed");
            ComPtr<IDXGIAdapter1> adapter;
            Check(factory->EnumAdapters1(options.adapter,&adapter), "selected_adapter_unavailable");
            DXGI_ADAPTER_DESC1 description{};
            Check(adapter->GetDesc1(&description), "adapter_description_failed");
            Require(!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE), "software_adapter_refused");
            ComPtr<ID3D11Device> device;
            ComPtr<ID3D11DeviceContext> context;
            const D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
            D3D_FEATURE_LEVEL selected{};
            Check(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels,static_cast<UINT>(std::size(levels)),D3D11_SDK_VERSION,&device,&selected,&context), "hardware_device_creation_failed");
            ComPtr<ID3D10Multithread> multithread;
            Check(device.As(&multithread), "multithread_interface_missing");
            (void)multithread->SetMultithreadProtected(TRUE);
            HdrFrameConverter converter;
            if (!converter.Initialize(device.Get(),SourceWidth,SourceHeight,sdr ? SourceEncoding::SrgbBgra8 : SourceEncoding::LinearScRgbFp16,
                static_cast<float>(options.whiteNits),evidence)) throw Failure{evidence.reason,evidence.hr};
            guard();
            D3D11_TEXTURE2D_DESC inputDescription{};
            inputDescription.Width = SourceWidth; inputDescription.Height = SourceHeight;
            inputDescription.MipLevels = inputDescription.ArraySize = inputDescription.SampleDesc.Count = 1;
            inputDescription.Format = sdr ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R16G16B16A16_FLOAT;
            inputDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            ComPtr<ID3D11Texture2D> input;
            Check(device->CreateTexture2D(&inputDescription,nullptr,&input), "synthetic_input_creation_failed");
            auto outputDescription = inputDescription;
            outputDescription.Width = OutputWidth; outputDescription.Height = OutputHeight;
            outputDescription.Format = DXGI_FORMAT_NV12; outputDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
            ComPtr<ID3D11Texture2D> output;
            Check(device->CreateTexture2D(&outputDescription,nullptr,&output), "nv12_output_creation_failed");
            auto stagingDescription = outputDescription;
            stagingDescription.BindFlags = 0; stagingDescription.Usage = D3D11_USAGE_STAGING;
            stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> staging;
            Check(device->CreateTexture2D(&stagingDescription,nullptr,&staging), "synthetic_readback_creation_failed");
            const D3D11_QUERY_DESC queryDescription{D3D11_QUERY_EVENT,0};
            ComPtr<ID3D11Query> completion;
            Check(device->CreateQuery(&queryDescription,&completion), "gpu_completion_query_failed");
            std::vector<Pixel> pixels(sdr ? 0 : static_cast<size_t>(SourceWidth) * SourceHeight);
            std::vector<Bgra> sdrPixels(sdr ? static_cast<size_t>(SourceWidth) * SourceHeight : 0);
            for (UINT frame = 0; frame < 3; ++frame)
            {
                if (sdr)
                {
                    for (UINT y = 0; y < SourceHeight; ++y)
                    {
                        if (y % 64 == 0) guard();
                        for (UINT x = 0; x < SourceWidth; ++x)
                            sdrPixels[static_cast<size_t>(y)*SourceWidth+x] = SdrGenerated(x,y,frame);
                    }
                    context->UpdateSubresource(input.Get(),0,nullptr,sdrPixels.data(),SourceWidth * static_cast<UINT>(sizeof(Bgra)),0);
                }
                else
                {
                    for (UINT y = 0; y < OutputHeight; ++y)
                    {
                        if (y % 64 == 0) guard();
                        for (UINT x = 0; x < OutputWidth; ++x)
                        {
                            const auto pixel = Pack(Generated(x,y,frame,options.whiteNits));
                            const size_t offset = static_cast<size_t>(y * 2) * SourceWidth + x * 2;
                            pixels[offset] = pixels[offset+1] = pixels[offset+SourceWidth] = pixels[offset+SourceWidth+1] = pixel;
                        }
                    }
                    context->UpdateSubresource(input.Get(),0,nullptr,pixels.data(),SourceWidth * static_cast<UINT>(sizeof(Pixel)),0);
                }
                if (!converter.Submit(input.Get(),output.Get(),evidence)) throw Failure{evidence.reason,evidence.hr};
                // Readback is restricted to generated fixture pixels.
                context->CopyResource(staging.Get(),output.Get()); context->End(completion.Get()); context->Flush();
                for (;;)
                {
                    guard(); Check(device->GetDeviceRemovedReason(), "d3d11_device_removed");
                    BOOL ready = FALSE;
                    const HRESULT hr = context->GetData(completion.Get(),&ready,sizeof(ready),D3D11_ASYNC_GETDATA_DONOTFLUSH);
                    Check(hr, "gpu_completion_query_failed");
                    if (hr == S_OK && ready) break;
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                }
                ++completedFrames;
                D3D11_MAPPED_SUBRESOURCE mapped{};
                Check(context->Map(staging.Get(),0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&mapped), "synthetic_readback_map_failed");
                struct Unmap { ID3D11DeviceContext* context; ID3D11Texture2D* texture; ~Unmap() { context->Unmap(texture,0); } } unmap{context.Get(),staging.Get()};
                Require(mapped.pData && mapped.RowPitch >= OutputWidth && mapped.RowPitch <= 65536, "nv12_readback_layout_invalid");
                if (sdr)
                {
                    for (const auto point : std::array<std::array<UINT,2>,8>{{ {0,0}, {OutputWidth-1,0},
                        {0,OutputHeight-1}, {OutputWidth-1,OutputHeight-1}, {OutputWidth/2,0},
                        {OutputWidth/2,OutputHeight-1}, {0,OutputHeight/2}, {OutputWidth-1,OutputHeight/2} }})
                        CheckPoint(mapped,point[0],point[1],frame,0,boundaries,true);
                }
                if (frame < 2)
                {
                    for (UINT patch = 0; patch < 16; ++patch)
                    {
                        const UINT left = (patch % 4) * (OutputWidth / 4) + OutputWidth / 8;
                        const UINT top = (patch / 4) * (OutputHeight / 4) + OutputHeight / 8;
                        for (UINT y = top; y < top + 8; ++y)
                            for (UINT x = left; x < left + 8; ++x)
                                CheckPoint(mapped,x,y,frame,options.whiteNits,patches,sdr);
                    }
                    if (sdr && frame == 0)
                    {
                        sdrRangeCorrect = ReadY(mapped,OutputWidth/8,OutputHeight/8) == 16 &&
                            ReadY(mapped,OutputWidth/4+OutputWidth/8,OutputHeight/8) == 235;
                        sdrMidtoneCorrect = ReadY(mapped,OutputWidth/8,OutputHeight*7/8) == 126;
                    }
                    if (!sdr && frame == 0)
                    {
                        constexpr std::array<UINT,8> expected{{16,23,40,77,130,157,182,212}};
                        for (UINT patch = 0; patch < expected.size(); ++patch)
                            CheckNeutralAnchor(mapped,(patch%4)*(OutputWidth/4)+OutputWidth/8,
                                (patch/4)*(OutputHeight/4)+OutputHeight/8,expected[patch],toneAnchors);
                    }
                }
                else
                {
                    UINT previous = 0;
                    rampMonotonic = true;
                    for (UINT i = 0; i < 256; ++i)
                    {
                        const UINT x = i * (OutputWidth - 1) / 255;
                        CheckPoint(mapped,x,100,frame,options.whiteNits,ramp,sdr);
                        const UINT actual = ReadY(mapped,x,100);
                        if (actual < previous) rampMonotonic = false;
                        previous = actual;
                    }
                    if (!sdr)
                    {
                        CheckNeutralAnchor(mapped,60,100,106,toneAnchors);
                        CheckNeutralAnchor(mapped,90,100,120,toneAnchors);
                        const UINT a = ReadY(mapped,120,100), b = ReadY(mapped,240,100), c = ReadY(mapped,480,100), d = ReadY(mapped,1500,100);
                        highlightsDistinct = a < b && b < c && c < d;
                    }
                    for (UINT y = 600; y < 620; ++y)
                        for (UINT x = 0; x < 64; ++x)
                            CheckPoint(mapped,x,y,frame,options.whiteNits,edges,sdr);
                }
            }
            Require(completedFrames == 3 && patches.checked == 6144 && ramp.checked == 768 && edges.checked == 3840, "synthetic_conversion_incomplete");
            Require(!sdr || boundaries.checked == 72, "synthetic_sdr_boundary_checks_incomplete");
            Require(!sdr || sdrMidtoneCorrect, "sdr_source_luma_code_not_preserved");
            Require(sdr || (toneAnchors.checked == 30 && toneAnchors.maximumError <= Tolerance), "hdr_contrast_policy_check_failed");
            Require(patches.maximumError <= Tolerance && ramp.maximumError <= Tolerance && edges.maximumError <= Tolerance,
                "synthetic_code_values_outside_tolerance");
            Require(boundaries.maximumError <= Tolerance, "synthetic_boundary_values_outside_tolerance");
            Require(rampMonotonic && (sdr ? sdrRangeCorrect : highlightsDistinct),
                sdr ? "sdr_range_or_ramp_check_failed" : "highlight_compression_check_failed");
            CheckAspectFits(device.Get(),context.Get(),sdr,guard,aspectColors,aspectFrames,aspectBlackChecks);
            guard(); completed = true; evidence.reason = sdr ? "synthetic_sdr_conversion_completed" : "synthetic_hdr_conversion_completed";
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now()-started).count();
        std::cout << std::boolalpha << "{\"mode\":\"" << (sdr ? "synthetic_srgb_to_sdr_nv12" : "synthetic_scrgb_to_sdr_nv12") << "\",\"completed\":" << completed
            << ",\"reason\":\"" << evidence.reason << "\",\"hresult\":" << static_cast<UINT>(evidence.hr)
            << ",\"sourceWidth\":" << SourceWidth << ",\"sourceHeight\":" << SourceHeight
            << ",\"outputWidth\":" << OutputWidth << ",\"outputHeight\":" << OutputHeight
            << ",\"sourceEncoding\":\"" << (sdr ? "sRGB_BGRA8" : "linear_scRGB_P709") << "\",\"referenceWhiteNits\":" << options.whiteNits;
        if (sdr)
            std::cout << ",\"referenceWhiteOrigin\":\"not_applicable\",\"toneCurve\":\"none\",\"gamutPolicy\":\"none\""
                << ",\"resizeFilter\":\"bilinear_source_code_values\",\"sdrBlackWhiteExact\":" << sdrRangeCorrect
                << ",\"sdrGray128Luma126Exact\":" << sdrMidtoneCorrect;
        else
            std::cout << ",\"referenceWhiteOrigin\":\"source_SDR_metadata_only\",\"toneCurve\":\"Rec2020_Reinhard_gamma24\""
                << ",\"automaticSdrReferenceNits\":300,\"sourcePixelsIndependentOfSdrWhite\":true"
                << ",\"gamutPolicy\":\"Rec2020_then_SDR_gamut_clamp\",\"nonfinitePolicy\":\"black\",\"negativeWorkingLightPolicy\":\"zero\"";
        std::cout << ",\"invalidPixelCountCollected\":false,\"outputTransfer\":\"sRGB\",\"outputMatrix\":\"BT709\""
            << ",\"outputRange\":\"limited_16_235_16_240\",\"chromaSiting\":\"horizontal_left_vertical_center\""
            << ",\"shadersCreated\":" << evidence.shadersCreated << ",\"planeViewsCreated\":" << evidence.planeViewsCreated
            << ",\"submittedFrames\":" << evidence.submittedFrames << ",\"completedGpuFrames\":" << completedFrames
            << ",\"patchCodeChecks\":" << patches.checked << ",\"patchMaximumCodeError\":" << patches.maximumError
            << ",\"rampCodeChecks\":" << ramp.checked << ",\"rampMaximumCodeError\":" << ramp.maximumError
            << ",\"edgeCodeChecks\":" << edges.checked << ",\"edgeMaximumCodeError\":" << edges.maximumError
            << ",\"boundaryCodeChecks\":" << boundaries.checked << ",\"boundaryMaximumCodeError\":" << boundaries.maximumError
            << ",\"toneAnchorCodeChecks\":" << toneAnchors.checked << ",\"toneAnchorMaximumCodeError\":" << toneAnchors.maximumError
            << ",\"aspectFitFrames\":" << aspectFrames << ",\"aspectFitCodeChecks\":" << aspectColors.checked
            << ",\"aspectFitMaximumCodeError\":" << aspectColors.maximumError << ",\"aspectFitExactBlackChecks\":" << aspectBlackChecks
            << ",\"allowedCodeError\":" << Tolerance << ",\"rampMonotonic\":" << rampMonotonic
            << ",\"highlightsDistinct\":" << highlightsDistinct << ",\"elapsedMs\":" << elapsed
            << ",\"captureUsed\":false,\"softwareConversionFallback\":false,\"gameAppearanceVerified\":false"
            << ",\"displayWhiteQueried\":false,\"contentPeakMeasured\":false,\"encoderIntegrationVerified\":false"
            << ",\"decodeVerified\":false,\"realtimePerformanceVerified\":false}\n";
        return completed ? 0 : 3;
    }
}

int wmain(int argc, wchar_t** argv)
{
    Options options;
    if (!Parse(argc,argv,options)) { std::cout << "{\"completed\":false,\"reason\":\"invalid_arguments\"}\n"; return 2; }
    if (options.mode == Mode::Help)
    {
        std::cout << "Synthetic scRGB HDR or sRGB BGRA8 -> SDR GPU conversion; no capture, files or windows.\n"
            "Default/help initialize no graphics resources. --self-test is CPU-only.\n"
            "--hdr-conversion-fixture --reference-white-nits 10..1000 [--adapter-index 0..15] [--timeout-ms 1000..30000]\n"
            "--sdr-conversion-fixture [--adapter-index 0..15] [--timeout-ms 1000..30000]\n"
            "Defaults: adapter 0, 10000 ms. Reference white is an explicit synthetic input, never a display/content measurement.\n"
            "Three generated 3840x2160 FP16 patterns -> 1920x1080 limited BT709 NV12.\n"
            "Also checks 21:9-class, 32:9, 16:10 and 16:9 aspect fits at 360p/480p in NV12 and prepared RGB AYUV.\n"
            "SDR mode uses BGRA8 patterns including subpixel variation, ramp, edges and border checks; no reference white or tone mapping.\n"
            "Automatic 300-nit Rec.2020 Reinhard/gamma2.4 HDR-to-SDR mapping; source SDR-white metadata does not alter exposure.\n"
            "No capture, decode or gameplay performance claim.\n"
            "Requires Forza closed. Ctrl+C cancels. Use an external API-call watchdog.\n";
        return 0;
    }
    if (options.mode == Mode::SelfTest)
    {
        const UINT passed = SelfTest();
        std::cout << "{\"mode\":\"cpu_contracts\",\"passed\":" << passed << ",\"graphicsInitialized\":false}\n";
        return passed ? 0 : 1;
    }
    if (!SetConsoleCtrlHandler(Cancel,TRUE)) { std::cout << "{\"completed\":false,\"reason\":\"cancel_handler_failed\"}\n"; return 3; }
    const int result = Run(options);
    (void)SetConsoleCtrlHandler(Cancel,FALSE);
    return result;
}
