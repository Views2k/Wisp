#include "RecorderHost.h"
#include "HardwareVideoSession.h"
#include "HdrFrameConverter.h"

#include <d3d10.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <tlhelp32.h>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cwchar>
#include <iostream>
#include <mutex>
#include <thread>
#include <vector>

namespace
{
    using Microsoft::WRL::ComPtr;
    constexpr UINT FramesPerCase = 4;
    constexpr UINT SourceWidth = 1920, SourceHeight = 1080;
    constexpr std::array<UINT, 6> Heights{ 360, 480, 720, 1080, 1440, 2160 };
    constexpr std::array<UINT, 2> Rates{ 30, 60 };

    void Check(HRESULT result) { if (FAILED(result)) throw result; }
    void Require(bool condition) { if (!condition) throw E_FAIL; }

    bool GameClosed() noexcept
    {
        const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return false;
        PROCESSENTRY32W entry{ sizeof(entry) };
        bool closed = Process32FirstW(snapshot, &entry) != FALSE;
        if (closed)
        {
            do
            {
                if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0 ||
                    _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") == 0 ||
                    _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") == 0 ||
                    _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") == 0)
                { closed = false; break; }
            } while (Process32NextW(snapshot, &entry));
            if (closed && GetLastError() != ERROR_NO_MORE_FILES) closed = false;
        }
        if (!CloseHandle(snapshot)) closed = false;
        return closed;
    }

    // Only this fixture can be terminated. No driver call is assumed to return
    // merely because its caller also has a clock deadline.
    class CaseWatchdog final
    {
    public:
        CaseWatchdog() : worker_([this] { Run(); }) {}
        ~CaseWatchdog()
        {
            { std::lock_guard<std::mutex> lock(mutex_); stopped_ = true; }
            changed_.notify_one();
            worker_.join();
        }
        void Arm()
        {
            { std::lock_guard<std::mutex> lock(mutex_);
                deadline_ = std::chrono::steady_clock::now() + std::chrono::seconds(10); armed_ = true; }
            changed_.notify_one();
        }
        void Disarm()
        {
            { std::lock_guard<std::mutex> lock(mutex_); armed_ = false; }
            changed_.notify_one();
        }
    private:
        void Run() noexcept
        {
            std::unique_lock<std::mutex> lock(mutex_);
            while (!stopped_)
            {
                if (!armed_) { changed_.wait(lock, [this] { return stopped_ || armed_; }); continue; }
                const auto deadline = deadline_;
                if (changed_.wait_until(lock, deadline, [this, deadline]
                    { return stopped_ || !armed_ || deadline_ != deadline; })) continue;
                // Exit code is a fixed timeout signal; a missing final summary
                // must not be interpreted as successful completion.
                TerminateProcess(GetCurrentProcess(), 14);
                std::terminate();
            }
        }
        std::mutex mutex_;
        std::condition_variable changed_;
        bool stopped_ = false, armed_ = false;
        std::chrono::steady_clock::time_point deadline_{};
        std::thread worker_;
    };

    recorder::encoder::EncodeConfig Configuration(UINT height, UINT rate)
    {
        recorder::host::Settings settings;
        settings.height = height; settings.frameRate = rate;
        recorder::host::Policy policy;
        Require(recorder::host::BuildPolicy(settings, policy));
        recorder::encoder::EncodeConfig config;
        config.width = policy.width; config.height = policy.height;
        config.frameRate = policy.frameRate; config.bitrate = policy.bitrate;
        config.pixelAspectNumerator = policy.aspectNumerator;
        config.pixelAspectDenominator = policy.aspectDenominator;
        config.chromaSiting = MFVideoChromaSubsampling_MPEG2;
        Require(recorder::encoder::ValidateConfiguration(config) == nullptr);
        return config;
    }
    recorder::conversion::OutputConfiguration Output(const recorder::encoder::EncodeConfig& config) noexcept
    {
        return { config.width, config.height, config.frameRate,
            config.pixelAspectNumerator, config.pixelAspectDenominator };
    }
    LONGLONG Time(UINT frame, UINT rate) noexcept
    { return static_cast<LONGLONG>(frame) * 10000000 / rate; }

    struct Observer final : recorder::encoder::PacketObserver
    {
        recorder::encoder::EncodeConfig expected;
        UINT configurations = 0, packets = 0;
        std::uint64_t bytes = 0;
        explicit Observer(const recorder::encoder::EncodeConfig& config) : expected(config) {}
        HRESULT OnConfiguration(IMFMediaType* type, const recorder::encoder::EncodeConfig& config) noexcept override
        {
            if (!type || config.width != expected.width || config.height != expected.height ||
                config.frameRate != expected.frameRate || config.bitrate != expected.bitrate ||
                config.pixelAspectNumerator != expected.pixelAspectNumerator ||
                config.pixelAspectDenominator != expected.pixelAspectDenominator ||
                config.chromaSiting != expected.chromaSiting) return E_FAIL;
            GUID major{}, subtype{}; UINT width = 0, height = 0, numerator = 0, denominator = 0;
            if (FAILED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) || major != MFMediaType_Video ||
                FAILED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) || subtype != MFVideoFormat_H264 ||
                FAILED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height)) ||
                width != expected.width || height != expected.height ||
                FAILED(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &numerator, &denominator)) ||
                numerator != expected.frameRate || denominator != 1 ||
                FAILED(MFGetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, &numerator, &denominator)) ||
                numerator != expected.pixelAspectNumerator || denominator != expected.pixelAspectDenominator)
                return E_FAIL;
            ++configurations;
            return S_OK;
        }
        HRESULT OnPacket(IMFMediaType* type, IMFSample* sample) noexcept override
        {
            if (!sample || packets >= FramesPerCase || FAILED(OnConfiguration(type, expected))) return E_FAIL;
            LONGLONG time = 0, duration = 0; DWORD length = 0;
            if (FAILED(sample->GetSampleTime(&time)) || time != Time(packets, expected.frameRate) ||
                FAILED(sample->GetSampleDuration(&duration)) ||
                duration != Time(packets + 1, expected.frameRate) - Time(packets, expected.frameRate) ||
                FAILED(sample->GetTotalLength(&length)) || length == 0 || length > 64u * 1024 * 1024)
                return E_FAIL;
            bytes += length; ++packets;
            return S_OK;
        }
    };

    struct Writer final : recorder::encoder::FrameWriter
    {
        recorder::hdr::HdrFrameConverter converter;
        recorder::hdr::Evidence evidence;
        const std::array<ComPtr<ID3D11Texture2D>, 2>& sources;
        explicit Writer(const std::array<ComPtr<ID3D11Texture2D>, 2>& value) : sources(value) {}
        HRESULT Fill(UINT frame, ID3D11Texture2D* destination) noexcept override
        {
            return converter.Submit(sources[frame % sources.size()].Get(), destination, evidence) ? S_OK : evidence.hr;
        }
    };

    std::array<ComPtr<ID3D11Texture2D>, 2> MakeSources(ID3D11Device* device)
    {
        std::array<ComPtr<ID3D11Texture2D>, 2> textures;
        std::vector<std::uint32_t> pixels(static_cast<size_t>(SourceWidth) * SourceHeight);
        constexpr std::array<std::uint32_t, 4> colors{ 0xff202020u, 0xffe0e0e0u, 0xffd02020u, 0xff2040d0u };
        D3D11_TEXTURE2D_DESC description{};
        description.Width = SourceWidth; description.Height = SourceHeight;
        description.MipLevels = 1; description.ArraySize = 1;
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM; description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT; description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        for (UINT pattern = 0; pattern < textures.size(); ++pattern)
        {
            for (UINT y = 0; y < SourceHeight; ++y)
                for (UINT x = 0; x < SourceWidth; ++x)
                    pixels[static_cast<size_t>(y) * SourceWidth + x] = colors[(x / 240 + y / 270 + pattern) % colors.size()];
            const D3D11_SUBRESOURCE_DATA initial{ pixels.data(), SourceWidth * sizeof(std::uint32_t), 0 };
            Check(device->CreateTexture2D(&description, &initial, &textures[pattern]));
        }
        return textures;
    }

    bool RunCase(ID3D11Device* device, const std::array<ComPtr<ID3D11Texture2D>, 2>& sources,
        UINT height, UINT rate, bool& cleanupOkay)
    {
        const auto config = Configuration(height, rate);
        const auto output = Output(config);
        Observer observer(config);
        Writer writer(sources);
        recorder::encoder::Evidence result;
        std::atomic<bool> cancelled{ false };
        recorder::encoder::HardwareVideoSession session;
        bool okay = false;
        if (!GameClosed()) { result.reason = "game_closed_guard_failed"; result.hr = E_ABORT; }
        else if (!writer.converter.Initialize(device, SourceWidth, SourceHeight,
            recorder::hdr::SourceEncoding::SrgbBgra8, 0.0f, output, writer.evidence))
        { result.reason = "conversion_initialization_failed"; result.hr = writer.evidence.hr; }
        else
        {
            recorder::encoder::LiveOptions options;
            options.operationTimeoutMs = 2000;
            okay = session.Initialize(device, config, options, cancelled, observer);
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
            UINT frame = 0;
            while (okay && frame < FramesPerCase)
            {
                if (!GameClosed() || std::chrono::steady_clock::now() >= deadline)
                { cancelled.store(true); okay = false; break; }
                okay = session.Pump(50);
                if (!okay) break;
                const auto submitted = session.TrySubmit(frame, Time(frame, rate), writer);
                if (submitted == recorder::encoder::SubmitResult::Submitted) ++frame;
                else if (submitted == recorder::encoder::SubmitResult::Failed) okay = false;
            }
            if (okay && !GameClosed()) { cancelled.store(true); okay = false; }
            if (okay) okay = session.Drain();
            if (!GameClosed()) { cancelled.store(true); okay = false; }
            result = session.Result();
            if (cancelled.load()) { result.reason = "fixture_guard_failed"; result.hr = E_ABORT; }
        }
        const auto closed = session.Close();
        if (!GameClosed()) { okay = false; result.reason = "game_closed_guard_failed"; result.hr = E_ABORT; }
        if (FAILED(closed)) { cleanupOkay = false; result.cleanupHr = closed; }
        const auto& closedEvidence = session.Result();
        if (result.configurationValidated)
        {
            result.samplesReturned = closedEvidence.samplesReturned;
            result.returnedSamples = closedEvidence.returnedSamples;
            result.eventCallbackDrained = closedEvidence.eventCallbackDrained;
            if (FAILED(closedEvidence.cleanupHr)) { cleanupOkay = false; result.cleanupHr = closedEvidence.cleanupHr; }
        }
        okay = okay && result.completed && SUCCEEDED(closed) && SUCCEEDED(result.cleanupHr) &&
            result.submitted == FramesPerCase && result.outputSamples == FramesPerCase &&
            observer.packets == FramesPerCase && observer.bytes > 0 &&
            result.cleanPoints > 0 && result.sequenceHeaderBytes > 0 && result.gopSizeReadback &&
            result.negotiatedGopFrames == 2 * rate && result.samplesReturned &&
            result.returnedSamples == FramesPerCase && result.eventCallbackDrained &&
            writer.evidence.submittedFrames == FramesPerCase;
        std::cout << "{\"mode\":\"preset_encode_case\",\"height\":" << height << ",\"width\":" << config.width
            << ",\"frameRate\":" << rate << ",\"bitrate\":" << config.bitrate
            << ",\"sarNumerator\":" << config.pixelAspectNumerator << ",\"sarDenominator\":" << config.pixelAspectDenominator
            << ",\"completed\":" << okay << ",\"reason\":\"" << result.reason << "\",\"hresult\":" << static_cast<UINT>(result.hr)
            << ",\"cleanupHresult\":" << static_cast<UINT>(result.cleanupHr) << ",\"submitted\":" << result.submitted
            << ",\"outputSamples\":" << result.outputSamples << ",\"encodedBytes\":" << result.encodedBytes
            << ",\"headerBytes\":" << result.sequenceHeaderBytes << ",\"cleanPoints\":" << result.cleanPoints
            << ",\"negotiatedGopFrames\":" << result.negotiatedGopFrames << ",\"samplesReturned\":" << result.samplesReturned
            << ",\"eventCallbackDrained\":" << result.eventCallbackDrained << ",\"conversionFrames\":" << writer.evidence.submittedFrames
            << ",\"fullGopIntervalMeasured\":false,\"realtimeThroughputMeasured\":false}" << std::endl;
        return okay;
    }

    UINT CpuContracts()
    {
        UINT checks = 0;
        for (const auto height : Heights) for (const auto rate : Rates)
        {
            const auto config = Configuration(height, rate);
            Require(recorder::conversion::ValidateOutputConfiguration(Output(config)) == nullptr); ++checks;
            Require(recorder::hdr::ValidateConfiguration(SourceWidth, SourceHeight,
                recorder::hdr::SourceEncoding::SrgbBgra8, 0.0f, Output(config)) == nullptr); ++checks;
            LONGLONG duration = 0;
            Require(recorder::encoder::ValidateCfrTime(config, 0, FramesPerCase - 1,
                Time(FramesPerCase - 1, rate), duration) && duration > 0); ++checks;
        }
        return checks;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 1 || (argc == 2 && std::wcscmp(argv[1], L"--help") == 0))
    {
        std::cout << "--preset-fixture: 12 finite hardware cases, four generated SDR frames each. No capture, audio or files.\n"
            "--self-test: CPU-only preset and timestamp contracts. Forza must be closed for the hardware fixture.\n";
        return 0;
    }
    if (argc == 2 && std::wcscmp(argv[1], L"--self-test") == 0)
    {
        try { std::cout << "{\"mode\":\"preset_cpu_contracts\",\"passed\":" << CpuContracts() << "}\n"; return 0; }
        catch (...) { return 2; }
    }
    if (argc != 2 || std::wcscmp(argv[1], L"--preset-fixture") != 0) return 2;
    if (!GameClosed()) { std::cout << "{\"reason\":\"game_closed_guard_failed\"}\n"; return 3; }
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com)) return 4;
    const HRESULT mf = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(mf)) { CoUninitialize(); return 4; }
    UINT attempted = 0, passed = 0;
    bool cleanupOkay = true;
    HRESULT fixtureHr = S_OK;
    {
        try
        {
            ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter;
            ComPtr<ID3D11Device> device; ComPtr<ID3D10Multithread> multithread;
            Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory))); Check(factory->EnumAdapters1(0, &adapter));
            DXGI_ADAPTER_DESC1 description{}; Check(adapter->GetDesc1(&description));
            Require(!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE));
            const D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
            Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                &level, 1, D3D11_SDK_VERSION, &device, nullptr, nullptr));
            Check(device.As(&multithread)); (void)multithread->SetMultithreadProtected(TRUE);
            const auto sources = MakeSources(device.Get());
            CaseWatchdog watchdog;
            std::cout << std::boolalpha;
            for (const auto height : Heights)
            {
                for (const auto rate : Rates)
                {
                    Require(GameClosed());
                    watchdog.Arm();
                    ++attempted;
                    if (RunCase(device.Get(), sources, height, rate, cleanupOkay)) ++passed;
                    watchdog.Disarm();
                    if (!cleanupOkay) break;
                }
                if (!cleanupOkay) break;
            }
        }
        catch (HRESULT hr) { fixtureHr = hr; }
        catch (...) { fixtureHr = E_FAIL; }
    }
    const auto shutdown = MFShutdown(); CoUninitialize();
    cleanupOkay = cleanupOkay && SUCCEEDED(shutdown);
    const bool gameAbsentAtCompletion = GameClosed();
    const bool completed = attempted == 12 && passed == 12 && cleanupOkay && SUCCEEDED(fixtureHr) && gameAbsentAtCompletion;
    std::cout << std::boolalpha << "{\"mode\":\"preset_encode_summary\",\"attempted\":" << attempted
        << ",\"passed\":" << passed << ",\"completed\":" << completed << ",\"cleanupOkay\":" << cleanupOkay
        << ",\"hresult\":" << static_cast<UINT>(fixtureHr) << ",\"mfShutdownHresult\":" << static_cast<UINT>(shutdown)
        << ",\"gameAbsentAtCompletion\":" << gameAbsentAtCompletion
        << ",\"captureUsed\":false,\"audioActivated\":false,\"filesWritten\":false,\"pixelsDecoded\":false,\"gamePerformanceMeasured\":false}" << std::endl;
    return completed ? 0 : 5;
}
