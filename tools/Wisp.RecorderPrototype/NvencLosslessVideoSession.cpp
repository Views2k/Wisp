#include "NvencLosslessVideoSession.h"
#include "ConversionOutput.h"
#include "LosslessProbe/nvEncodeAPI.h"

#include <d3d10.h>
#include <dxgi1_2.h>
#include <codecapi.h>
#include <mfapi.h>
#include <array>
#include <algorithm>
#include <cstring>
#include <limits>
#include <vector>

namespace recorder::lossless
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        struct Failure { const char* reason; HRESULT hr; UINT nvencStatus = 0; };
        void Check(HRESULT hr, const char* reason) { if (FAILED(hr)) throw Failure{ reason, hr }; }
        void Require(bool value, const char* reason, HRESULT hr = E_INVALIDARG) { if (!value) throw Failure{ reason, hr }; }
        void Nv(NVENCSTATUS status, const char* reason)
        { if (status != NV_ENC_SUCCESS) throw Failure{ reason, E_FAIL, static_cast<UINT>(status) }; }
        template<class T> T Export(HMODULE module, const char* name)
        {
            const auto address = GetProcAddress(module, name);
            Require(address != nullptr, "nvenc_api_export_missing", E_NOTIMPL);
            T function{}; static_assert(sizeof(function) == sizeof(address));
            std::memcpy(&function, &address, sizeof(function)); return function;
        }
        struct DeviceLock
        {
            ID3D10Multithread* value;
            explicit DeviceLock(ID3D10Multithread* target) : value(target) { value->Enter(); }
            ~DeviceLock() { value->Leave(); }
        };
        enum class Stage { Free, GpuPending, DeclinedGpuPending, Encoding };
        struct Slot
        {
            ComPtr<ID3D11Texture2D> texture;
            ComPtr<ID3D11Query> completed;
            NV_ENC_REGISTERED_PTR registered = nullptr;
            NV_ENC_INPUT_PTR mapped = nullptr;
            NV_ENC_OUTPUT_PTR bitstream = nullptr;
            HANDLE event = nullptr;
            bool eventRegistered = false;
            bool outputAcquired = false;
            Stage stage = Stage::Free;
            UINT index = 0;
            LONGLONG time = 0, duration = 0;
            ULONGLONG began = 0;
        };
    }

    const char* ValidateConfiguration(const encoder::EncodeConfig& value, const Options& options) noexcept
    {
        if (const auto reason = conversion::ValidateOutputConfiguration({ value.width, value.height, value.frameRate,
            value.pixelAspectNumerator, value.pixelAspectDenominator })) return reason;
        if (value.bitrate != 0 || value.chromaSiting != 0) return "lossless_requires_no_bitrate_or_subsampling";
        if (options.operationTimeoutMs < 100 || options.operationTimeoutMs > 10000 || options.epochTime100ns < 0)
            return "lossless_options_invalid";
        return nullptr;
    }
    bool ValidateFrameTime(UINT rate, LONGLONG epoch, UINT index, LONGLONG time, LONGLONG& duration) noexcept
    {
        duration = 0;
        if ((rate != 30 && rate != 60) || epoch < 0 || index == (std::numeric_limits<UINT>::max)()) return false;
        const auto relative = static_cast<LONGLONG>(index) * 10000000 / rate;
        const auto end = (static_cast<LONGLONG>(index) + 1) * 10000000 / rate;
        if (epoch > (std::numeric_limits<LONGLONG>::max)() - end || time != epoch + relative) return false;
        duration = end - relative; return duration > 0;
    }

    struct NvencLosslessVideoSession::Impl
    {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<ID3D10Multithread> multithread;
        ComPtr<IMFMediaType> mediaType;
        HMODULE module = nullptr;
        NV_ENCODE_API_FUNCTION_LIST api{};
        void* encodeSession = nullptr;
        std::array<Slot, SurfaceCount> slots;
        NV_ENC_OUTPUT_PTR lockedOutput = nullptr;
        HANDLE eosEvent = nullptr;
        bool eosRegistered = false, draining = false, eosSent = false;
        DWORD owner = 0;
        encoder::EncodeConfig config{};
        Options options{};
        const std::atomic<bool>* cancelled = nullptr;
        encoder::PacketObserver* observer = nullptr;
        UINT nextSubmit = 0, nextEncode = 0, nextOutput = 0, lastKeyframe = 0;
        ULONGLONG drainBegan = 0;
        const char* cleanupReason = "not_started";

        HRESULT Close() noexcept
        {
            try
            {
                // Cancellation stops new work, not retirement of work already owned by the GPU.
                // No observer callbacks or new frame submissions are made during this cleanup.
                const auto began = GetTickCount64();
                bool quiescent = false;
                while (!quiescent)
                {
                    Require(GetTickCount64() - began < options.operationTimeoutMs,
                        "lossless_cleanup_timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                    bool gpuComplete = true;
                    for (const auto& slot : slots)
                    {
                        if (slot.stage != Stage::GpuPending && slot.stage != Stage::DeclinedGpuPending) continue;
                        BOOL complete = FALSE; HRESULT status;
                        { DeviceLock lock(multithread.Get()); status = context->GetData(slot.completed.Get(), &complete, sizeof(complete), D3D11_ASYNC_GETDATA_DONOTFLUSH); }
                        Check(status, "lossless_cleanup_gpu_failed");
                        gpuComplete = gpuComplete && status == S_OK && complete != FALSE;
                    }
                    // The flush event is registered before any input/output resources are created.
                    // Before that point initialization cannot have submitted encoding work.
                    if (encodeSession && eosRegistered && !eosSent)
                    {
                        NV_ENC_PIC_PARAMS eos{}; eos.version = NV_ENC_PIC_PARAMS_VER;
                        eos.encodePicFlags = NV_ENC_PIC_FLAG_EOS; eos.completionEvent = eosEvent;
                        const auto status = api.nvEncEncodePicture(encodeSession, &eos);
                        if (status != NV_ENC_ERR_ENCODER_BUSY) { Nv(status, "lossless_cleanup_eos_failed"); eosSent = true; }
                    }
                    for (UINT work = 0; work < SurfaceCount && nextOutput < nextEncode; ++work)
                    {
                        auto& slot = slots[nextOutput % SurfaceCount];
                        Require(slot.stage == Stage::Encoding && slot.index == nextOutput,
                            "lossless_cleanup_queue_invalid", E_UNEXPECTED);
                        if (!slot.outputAcquired)
                        {
                            const DWORD ready = WaitForSingleObject(slot.event, 0);
                            if (ready == WAIT_TIMEOUT) break;
                            Require(ready == WAIT_OBJECT_0, "lossless_cleanup_event_failed", HRESULT_FROM_WIN32(GetLastError()));
                            NV_ENC_LOCK_BITSTREAM output{}; output.version = NV_ENC_LOCK_BITSTREAM_VER;
                            output.outputBitstream = slot.bitstream; output.doNotWait = 1;
                            const auto status = api.nvEncLockBitstream(encodeSession, &output);
                            if (status == NV_ENC_ERR_LOCK_BUSY) break;
                            Nv(status, "lossless_cleanup_lock_failed");
                            lockedOutput = slot.bitstream; slot.outputAcquired = true;
                        }
                        if (lockedOutput)
                        {
                            Require(lockedOutput == slot.bitstream, "lossless_cleanup_locked_output_invalid", E_UNEXPECTED);
                            Nv(api.nvEncUnlockBitstream(encodeSession, lockedOutput), "lossless_cleanup_unlock_failed"); lockedOutput = nullptr;
                        }
                        if (slot.mapped)
                        { Nv(api.nvEncUnmapInputResource(encodeSession, slot.mapped), "lossless_cleanup_unmap_failed"); slot.mapped = nullptr; }
                        slot.stage = Stage::Free; slot.outputAcquired = false; ++nextOutput;
                    }
                    bool encoderComplete = !eosRegistered;
                    if (eosSent && nextOutput == nextEncode)
                    {
                        const DWORD ready = WaitForSingleObject(eosEvent, 0);
                        Require(ready == WAIT_OBJECT_0 || ready == WAIT_TIMEOUT, "lossless_cleanup_flush_wait_failed");
                        encoderComplete = ready == WAIT_OBJECT_0;
                    }
                    quiescent = gpuComplete && encoderComplete;
                    if (!quiescent) Sleep(1);
                }
                if (encodeSession)
                {
                    for (auto& slot : slots)
                    {
                        if (slot.mapped) { Nv(api.nvEncUnmapInputResource(encodeSession, slot.mapped), "lossless_cleanup_unmap_failed"); slot.mapped = nullptr; }
                        if (slot.registered) { Nv(api.nvEncUnregisterResource(encodeSession, slot.registered), "lossless_cleanup_unregister_failed"); slot.registered = nullptr; }
                        if (slot.bitstream) { Nv(api.nvEncDestroyBitstreamBuffer(encodeSession, slot.bitstream), "lossless_cleanup_buffer_failed"); slot.bitstream = nullptr; }
                        if (slot.eventRegistered)
                        {
                            NV_ENC_EVENT_PARAMS event{}; event.version = NV_ENC_EVENT_PARAMS_VER; event.completionEvent = slot.event;
                            Nv(api.nvEncUnregisterAsyncEvent(encodeSession, &event), "lossless_cleanup_event_unregister_failed"); slot.eventRegistered = false;
                        }
                    }
                    if (eosRegistered)
                    {
                        NV_ENC_EVENT_PARAMS event{}; event.version = NV_ENC_EVENT_PARAMS_VER; event.completionEvent = eosEvent;
                        Nv(api.nvEncUnregisterAsyncEvent(encodeSession, &event), "lossless_cleanup_eos_unregister_failed"); eosRegistered = false;
                    }
                    Nv(api.nvEncDestroyEncoder(encodeSession), "lossless_cleanup_encoder_failed"); encodeSession = nullptr;
                }
                for (auto& slot : slots)
                {
                    slot.texture.Reset(); slot.completed.Reset();
                    if (slot.event) { Check(CloseHandle(slot.event) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "lossless_cleanup_handle_failed"); slot.event = nullptr; }
                    slot.stage = Stage::Free;
                }
                if (eosEvent) { Check(CloseHandle(eosEvent) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "lossless_cleanup_eos_handle_failed"); eosEvent = nullptr; }
                if (module) { Check(FreeLibrary(module) ? S_OK : HRESULT_FROM_WIN32(GetLastError()), "lossless_cleanup_module_failed"); module = nullptr; }
                cleanupReason = "lossless_cleanup_complete";
                return S_OK;
            }
            catch (const Failure& error) { cleanupReason = error.reason; return error.hr; }
            catch (...) { cleanupReason = "lossless_cleanup_failed"; return E_FAIL; }
        }
    };

    NvencLosslessVideoSession::NvencLosslessVideoSession() noexcept = default;
    NvencLosslessVideoSession::~NvencLosslessVideoSession()
    {
        // A failed retirement must not release objects still referenced by the driver.
        // The recorder helper must exit on cleanup failure; its process owns this quarantine.
        if (FAILED(Close())) (void)impl_.release();
    }
    bool NvencLosslessVideoSession::Fail(const char* reason, HRESULT hr, UINT status) noexcept
    {
        if (!failed_) { evidence_.reason = reason; evidence_.hr = hr; evidence_.nvencStatus = status; }
        failed_ = true; return false;
    }
    void NvencLosslessVideoSession::Guard() const
    {
        Require(impl_ && evidence_.initialized && !closed_ && !failed_, "lossless_session_unavailable", E_UNEXPECTED);
        Require(impl_->owner == GetCurrentThreadId(), "lossless_wrong_worker", E_UNEXPECTED);
        Require(!impl_->cancelled->load(), "lossless_cancelled", HRESULT_FROM_WIN32(ERROR_CANCELLED));
        Check(impl_->device->GetDeviceRemovedReason(), "lossless_device_removed");
    }

    bool NvencLosslessVideoSession::Initialize(ID3D11Device* device, const encoder::EncodeConfig& config,
        const Options& options, const std::atomic<bool>& cancelled, encoder::PacketObserver& observer) noexcept
    {
        try
        {
            Require(device && !impl_ && !closed_ && !failed_, "lossless_invalid_initialize");
            if (const auto reason = ValidateConfiguration(config, options)) throw Failure{ reason, E_INVALIDARG };
            Require(!cancelled.load(), "lossless_cancelled", HRESULT_FROM_WIN32(ERROR_CANCELLED));
            impl_ = std::make_unique<Impl>();
            auto& value = *impl_;
            value.device = device; device->GetImmediateContext(&value.context);
            Check(device->QueryInterface(IID_PPV_ARGS(&value.multithread)), "lossless_multithread_interface_missing");
            Require(value.multithread->GetMultithreadProtected() != FALSE, "lossless_device_not_multithread_protected");
            value.config = config; value.options = options; value.cancelled = &cancelled; value.observer = &observer;
            value.owner = GetCurrentThreadId();
            ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIAdapter1> adapter1;
            Check(device->QueryInterface(IID_PPV_ARGS(&dxgi)), "lossless_dxgi_device_missing");
            Check(dxgi->GetAdapter(&adapter), "lossless_adapter_missing");
            Check(adapter.As(&adapter1), "lossless_adapter_missing");
            DXGI_ADAPTER_DESC1 description{}; Check(adapter1->GetDesc1(&description), "lossless_adapter_query_failed");
            Require(description.VendorId == 0x10de && !(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE),
                "lossless_nvidia_hardware_required", E_NOTIMPL);
            value.module = LoadLibraryExW(L"nvEncodeAPI64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            Require(value.module != nullptr, "lossless_driver_api_missing", E_NOTIMPL);
            using Version = NVENCSTATUS(NVENCAPI*)(std::uint32_t*);
            using Create = NVENCSTATUS(NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
            std::uint32_t supported = 0;
            Nv(Export<Version>(value.module, "NvEncodeAPIGetMaxSupportedVersion")(&supported), "lossless_api_version_failed");
            Require(supported >= ((NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION), "lossless_api_version_unsupported", E_NOTIMPL);
            auto& api = value.api; api.version = NV_ENCODE_API_FUNCTION_LIST_VER;
            Nv(Export<Create>(value.module, "NvEncodeAPICreateInstance")(&api), "lossless_api_create_failed");
            Require(api.nvEncOpenEncodeSessionEx && api.nvEncDestroyEncoder && api.nvEncGetEncodeCaps &&
                api.nvEncGetInputFormatCount && api.nvEncGetInputFormats && api.nvEncGetEncodeProfileGUIDCount &&
                api.nvEncGetEncodeProfileGUIDs && api.nvEncGetEncodePresetConfigEx && api.nvEncInitializeEncoder &&
                api.nvEncRegisterResource && api.nvEncUnregisterResource && api.nvEncMapInputResource && api.nvEncUnmapInputResource &&
                api.nvEncCreateBitstreamBuffer && api.nvEncDestroyBitstreamBuffer && api.nvEncLockBitstream && api.nvEncUnlockBitstream &&
                api.nvEncRegisterAsyncEvent && api.nvEncUnregisterAsyncEvent && api.nvEncGetSequenceParams && api.nvEncEncodePicture,
                "lossless_required_api_missing", E_NOTIMPL);
            NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open{};
            open.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER; open.apiVersion = NVENCAPI_VERSION;
            open.device = device; open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
            Nv(api.nvEncOpenEncodeSessionEx(&open, &value.encodeSession), "lossless_session_open_failed");
            Require(value.encodeSession != nullptr, "lossless_session_missing");
            for (auto cap : { NV_ENC_CAPS_SUPPORT_LOSSLESS_ENCODE, NV_ENC_CAPS_SUPPORT_YUV444_ENCODE, NV_ENC_CAPS_ASYNC_ENCODE_SUPPORT })
            {
                NV_ENC_CAPS_PARAM query{}; query.version = NV_ENC_CAPS_PARAM_VER; query.capsToQuery = cap;
                int available = 0;
                Nv(api.nvEncGetEncodeCaps(value.encodeSession, NV_ENC_CODEC_H264_GUID, &query, &available), "lossless_capability_query_failed");
                Require(available == 1, "lossless_required_capability_missing", E_NOTIMPL);
            }
            UINT count = 0, written = 0;
            Nv(api.nvEncGetInputFormatCount(value.encodeSession, NV_ENC_CODEC_H264_GUID, &count), "lossless_format_count_failed");
            Require(count && count <= 64, "lossless_format_count_invalid");
            std::vector<NV_ENC_BUFFER_FORMAT> formats(count);
            Nv(api.nvEncGetInputFormats(value.encodeSession, NV_ENC_CODEC_H264_GUID, formats.data(), count, &written), "lossless_formats_failed");
            Require(written <= count, "lossless_format_count_invalid"); formats.resize(written);
            Require(std::find(formats.begin(), formats.end(), NV_ENC_BUFFER_FORMAT_AYUV) != formats.end(), "lossless_ayuv_unavailable", E_NOTIMPL);
            count = written = 0;
            Nv(api.nvEncGetEncodeProfileGUIDCount(value.encodeSession, NV_ENC_CODEC_H264_GUID, &count), "lossless_profile_count_failed");
            Require(count && count <= 64, "lossless_profile_count_invalid");
            std::vector<GUID> profiles(count);
            Nv(api.nvEncGetEncodeProfileGUIDs(value.encodeSession, NV_ENC_CODEC_H264_GUID, profiles.data(), count, &written), "lossless_profiles_failed");
            Require(written <= count, "lossless_profile_count_invalid"); profiles.resize(written);
            Require(std::find(profiles.begin(), profiles.end(), NV_ENC_H264_PROFILE_HIGH_444_GUID) != profiles.end(), "lossless_high444_unavailable", E_NOTIMPL);
            NV_ENC_PRESET_CONFIG preset{}; preset.version = NV_ENC_PRESET_CONFIG_VER; preset.presetCfg.version = NV_ENC_CONFIG_VER;
            Nv(api.nvEncGetEncodePresetConfigEx(value.encodeSession, NV_ENC_CODEC_H264_GUID, NV_ENC_PRESET_P1_GUID,
                NV_ENC_TUNING_INFO_LOSSLESS, &preset), "lossless_preset_failed");
            auto codec = preset.presetCfg;
            codec.profileGUID = NV_ENC_H264_PROFILE_HIGH_444_GUID; codec.gopLength = config.frameRate * 2;
            codec.frameIntervalP = 1; codec.frameFieldMode = NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME;
            codec.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CONSTQP; codec.rcParams.constQP = {};
            codec.rcParams.enableAQ = codec.rcParams.enableTemporalAQ = codec.rcParams.enableLookahead = 0;
            codec.rcParams.lookaheadDepth = 0;
            codec.rcParams.enableMinQP = codec.rcParams.enableMaxQP = codec.rcParams.enableInitialRCQP = 0;
            auto& h264 = codec.encodeCodecConfig.h264Config;
            h264.chromaFormatIDC = 3; h264.qpPrimeYZeroTransformBypassFlag = 1; h264.separateColourPlaneFlag = 0;
            h264.disableDeblockingFilterIDC = 1; h264.inputBitDepth = h264.outputBitDepth = NV_ENC_BIT_DEPTH_8;
            h264.idrPeriod = config.frameRate * 2; h264.repeatSPSPPS = 1; h264.h264VUIParameters = {};
            auto& vui = h264.h264VUIParameters;
            vui.videoSignalTypePresentFlag = 1; vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
            vui.videoFullRangeFlag = 1; vui.colourDescriptionPresentFlag = 1;
            vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT709; vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
            vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_RGB;
            NV_ENC_INITIALIZE_PARAMS initialize{}; initialize.version = NV_ENC_INITIALIZE_PARAMS_VER;
            initialize.encodeGUID = NV_ENC_CODEC_H264_GUID; initialize.presetGUID = NV_ENC_PRESET_P1_GUID;
            initialize.encodeWidth = config.width; initialize.encodeHeight = config.height;
            initialize.darWidth = config.width * config.pixelAspectNumerator; initialize.darHeight = config.height * config.pixelAspectDenominator;
            initialize.frameRateNum = config.frameRate; initialize.frameRateDen = 1;
            initialize.enablePTD = 1; initialize.enableEncodeAsync = 1; initialize.encodeConfig = &codec;
            initialize.tuningInfo = NV_ENC_TUNING_INFO_LOSSLESS;
            Nv(api.nvEncInitializeEncoder(value.encodeSession, &initialize), "lossless_encoder_initialize_failed");
            value.eosEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            Require(value.eosEvent != nullptr, "lossless_eos_event_failed", HRESULT_FROM_WIN32(GetLastError()));
            NV_ENC_EVENT_PARAMS eos{}; eos.version = NV_ENC_EVENT_PARAMS_VER; eos.completionEvent = value.eosEvent;
            Nv(api.nvEncRegisterAsyncEvent(value.encodeSession, &eos), "lossless_eos_register_failed"); value.eosRegistered = true;
            for (auto& slot : value.slots)
            {
                Require(!cancelled.load(), "lossless_cancelled", HRESULT_FROM_WIN32(ERROR_CANCELLED));
                D3D11_TEXTURE2D_DESC texture{}; texture.Width = config.width; texture.Height = config.height;
                texture.MipLevels = texture.ArraySize = texture.SampleDesc.Count = 1; texture.Format = DXGI_FORMAT_AYUV;
                texture.Usage = D3D11_USAGE_DEFAULT; texture.BindFlags = D3D11_BIND_RENDER_TARGET;
                Check(device->CreateTexture2D(&texture, nullptr, &slot.texture), "lossless_ayuv_texture_failed");
                D3D11_QUERY_DESC query{}; query.Query = D3D11_QUERY_EVENT;
                Check(device->CreateQuery(&query, &slot.completed), "lossless_gpu_query_failed");
                NV_ENC_REGISTER_RESOURCE resource{}; resource.version = NV_ENC_REGISTER_RESOURCE_VER;
                resource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX; resource.resourceToRegister = slot.texture.Get();
                resource.width = config.width; resource.height = config.height; resource.pitch = 0;
                resource.bufferFormat = NV_ENC_BUFFER_FORMAT_AYUV; resource.bufferUsage = NV_ENC_INPUT_IMAGE;
                Nv(api.nvEncRegisterResource(value.encodeSession, &resource), "lossless_ayuv_register_failed");
                slot.registered = resource.registeredResource; Require(slot.registered != nullptr, "lossless_registered_input_missing");
                NV_ENC_CREATE_BITSTREAM_BUFFER output{}; output.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
                Nv(api.nvEncCreateBitstreamBuffer(value.encodeSession, &output), "lossless_bitstream_create_failed");
                slot.bitstream = output.bitstreamBuffer; Require(slot.bitstream != nullptr, "lossless_bitstream_missing");
                slot.event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
                Require(slot.event != nullptr, "lossless_event_create_failed", HRESULT_FROM_WIN32(GetLastError()));
                NV_ENC_EVENT_PARAMS event{}; event.version = NV_ENC_EVENT_PARAMS_VER; event.completionEvent = slot.event;
                Nv(api.nvEncRegisterAsyncEvent(value.encodeSession, &event), "lossless_event_register_failed"); slot.eventRegistered = true;
            }
            std::vector<BYTE> header(65536); UINT headerSize = 0;
            NV_ENC_SEQUENCE_PARAM_PAYLOAD sequence{}; sequence.version = NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER;
            sequence.inBufferSize = static_cast<UINT>(header.size()); sequence.spsppsBuffer = header.data(); sequence.outSPSPPSPayloadSize = &headerSize;
            Nv(api.nvEncGetSequenceParams(value.encodeSession, &sequence), "lossless_sequence_header_failed");
            Require(headerSize > 0 && headerSize <= header.size(), "lossless_sequence_header_size_invalid");
            Check(MFCreateMediaType(&value.mediaType), "lossless_media_type_failed");
            auto* type = value.mediaType.Get();
            Check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "lossless_media_attribute_failed");
            Check(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264), "lossless_media_attribute_failed");
            Check(MFSetAttributeSize(type, MF_MT_FRAME_SIZE, config.width, config.height), "lossless_media_attribute_failed");
            Check(MFSetAttributeRatio(type, MF_MT_FRAME_RATE, config.frameRate, 1), "lossless_media_attribute_failed");
            Check(MFSetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, config.pixelAspectNumerator, config.pixelAspectDenominator), "lossless_media_attribute_failed");
            Check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "lossless_media_attribute_failed");
            Check(type->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_444), "lossless_media_attribute_failed");
            Check(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709), "lossless_media_attribute_failed");
            Check(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709), "lossless_media_attribute_failed");
            Check(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_Identity), "lossless_media_attribute_failed");
            Check(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_0_255), "lossless_media_attribute_failed");
            Check(type->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, header.data(), headerSize), "lossless_media_header_failed");
            Check(observer.OnConfiguration(type, config), "lossless_configuration_observer_failed");
            evidence_.initialized = true; evidence_.asynchronous = true; evidence_.reason = "lossless_session_initialized";
            return true;
        }
        catch (const Failure& error) { Fail(error.reason, error.hr, error.nvencStatus); }
        catch (const std::bad_alloc&) { Fail("lossless_allocation_failed", E_OUTOFMEMORY); }
        catch (...) { Fail("lossless_initialize_failed", E_FAIL); }
        (void)Close(); return false;
    }

    bool NvencLosslessVideoSession::CanAcceptInput() const noexcept
    {
        return impl_ && evidence_.initialized && !failed_ && !closed_ && !impl_->draining &&
            impl_->owner == GetCurrentThreadId() && !impl_->cancelled->load() &&
            impl_->nextSubmit != (std::numeric_limits<UINT>::max)() &&
            impl_->slots[impl_->nextSubmit % SurfaceCount].stage == Stage::Free;
    }
    encoder::SubmitResult NvencLosslessVideoSession::TrySubmit(UINT index, LONGLONG time, encoder::FrameWriter& writer) noexcept
    {
        try
        {
            Guard(); auto& value = *impl_;
            Require(!value.draining && index == value.nextSubmit, "lossless_input_order_invalid");
            LONGLONG duration = 0;
            Require(ValidateFrameTime(value.config.frameRate, value.options.epochTime100ns, index, time, duration), "lossless_input_time_invalid");
            if (!CanAcceptInput()) return encoder::SubmitResult::WouldBlock;
            auto& slot = value.slots[index % SurfaceCount];
            slot.index = index; slot.time = time; slot.duration = duration; slot.began = GetTickCount64(); slot.stage = Stage::GpuPending;
            HRESULT written;
            {
                DeviceLock lock(value.multithread.Get());
                written = writer.Fill(index, slot.texture.Get());
                // Even a failed writer may have queued partial GPU work on the borrowed surface.
                value.context->End(slot.completed.Get()); value.context->Flush();
            }
            if (written == S_FALSE)
            {
                // Keep ownership until even a partial GPU conversion retires.
                // This slot never enters NVENC or advances its media index.
                slot.stage = Stage::DeclinedGpuPending;
                return encoder::SubmitResult::WouldBlock;
            }
            Check(written, "lossless_frame_writer_failed");
            Require(written == S_OK, "lossless_frame_not_written", E_UNEXPECTED);
            ++value.nextSubmit; ++evidence_.submitted;
            evidence_.peakInFlight = (std::max)(evidence_.peakInFlight, value.nextSubmit - value.nextOutput);
            return encoder::SubmitResult::Submitted;
        }
        catch (const Failure& error) { Fail(error.reason, error.hr, error.nvencStatus); }
        catch (...) { Fail("lossless_submit_failed", E_FAIL); }
        return encoder::SubmitResult::Failed;
    }

    bool NvencLosslessVideoSession::Pump() noexcept
    {
        try
        {
            Guard(); auto& value = *impl_; auto& api = value.api;
            for (const auto& slot : value.slots)
                if (slot.stage != Stage::Free)
                    Require(GetTickCount64() - slot.began < value.options.operationTimeoutMs, "lossless_slot_timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            for (auto& slot : value.slots)
            {
                if (slot.stage != Stage::DeclinedGpuPending) continue;
                BOOL completed = FALSE; HRESULT status;
                { DeviceLock lock(value.multithread.Get()); status = value.context->GetData(slot.completed.Get(), &completed, sizeof(completed), D3D11_ASYNC_GETDATA_DONOTFLUSH); }
                Check(status, "lossless_declined_gpu_completion_failed");
                if (status == S_OK && completed) slot.stage = Stage::Free;
            }
            for (UINT work = 0; work < SurfaceCount && value.nextEncode < value.nextSubmit; ++work)
            {
                auto& slot = value.slots[value.nextEncode % SurfaceCount];
                Require(slot.stage == Stage::GpuPending && slot.index == value.nextEncode, "lossless_gpu_queue_invalid");
                if (!slot.mapped)
                {
                    BOOL completed = FALSE; HRESULT status;
                    { DeviceLock lock(value.multithread.Get()); status = value.context->GetData(slot.completed.Get(), &completed, sizeof(completed), D3D11_ASYNC_GETDATA_DONOTFLUSH); }
                    Check(status, "lossless_gpu_completion_failed");
                    if (status == S_FALSE || !completed) break;
                    NV_ENC_MAP_INPUT_RESOURCE mapped{}; mapped.version = NV_ENC_MAP_INPUT_RESOURCE_VER; mapped.registeredResource = slot.registered;
                    Nv(api.nvEncMapInputResource(value.encodeSession, &mapped), "lossless_input_map_failed");
                    slot.mapped = mapped.mappedResource;
                    Require(slot.mapped && mapped.mappedBufferFmt == NV_ENC_BUFFER_FORMAT_AYUV, "lossless_mapped_format_invalid");
                }
                Require(ResetEvent(slot.event) != FALSE, "lossless_event_reset_failed", HRESULT_FROM_WIN32(GetLastError()));
                NV_ENC_PIC_PARAMS picture{}; picture.version = NV_ENC_PIC_PARAMS_VER;
                picture.inputWidth = value.config.width; picture.inputHeight = value.config.height; picture.inputPitch = value.config.width;
                picture.frameIdx = slot.index; picture.inputTimeStamp = static_cast<std::uint64_t>(slot.time); picture.inputDuration = static_cast<std::uint64_t>(slot.duration);
                picture.inputBuffer = slot.mapped; picture.bufferFmt = NV_ENC_BUFFER_FORMAT_AYUV;
                picture.outputBitstream = slot.bitstream; picture.completionEvent = slot.event; picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
                if (slot.index % (value.config.frameRate * 2) == 0) picture.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
                const auto status = api.nvEncEncodePicture(value.encodeSession, &picture);
                if (status == NV_ENC_ERR_ENCODER_BUSY) break;
                if (status != NV_ENC_ERR_NEED_MORE_INPUT) Nv(status, "lossless_encode_failed");
                slot.stage = Stage::Encoding; ++value.nextEncode; ++evidence_.encoded;
            }
            if (value.draining && !value.eosSent && value.nextEncode == value.nextSubmit)
            {
                NV_ENC_PIC_PARAMS eos{}; eos.version = NV_ENC_PIC_PARAMS_VER; eos.encodePicFlags = NV_ENC_PIC_FLAG_EOS; eos.completionEvent = value.eosEvent;
                const auto status = api.nvEncEncodePicture(value.encodeSession, &eos);
                if (status != NV_ENC_ERR_ENCODER_BUSY) { Nv(status, "lossless_eos_failed"); value.eosSent = true; }
            }
            for (UINT work = 0; work < SurfaceCount && value.nextOutput < value.nextEncode; ++work)
            {
                auto& slot = value.slots[value.nextOutput % SurfaceCount];
                Require(slot.stage == Stage::Encoding && slot.index == value.nextOutput, "lossless_output_queue_invalid");
                const DWORD ready = WaitForSingleObject(slot.event, 0);
                if (ready == WAIT_TIMEOUT) break;
                Require(ready == WAIT_OBJECT_0, "lossless_completion_event_failed", HRESULT_FROM_WIN32(GetLastError()));
                NV_ENC_LOCK_BITSTREAM output{}; output.version = NV_ENC_LOCK_BITSTREAM_VER;
                output.outputBitstream = slot.bitstream; output.doNotWait = 1;
                const auto status = api.nvEncLockBitstream(value.encodeSession, &output);
                if (status == NV_ENC_ERR_LOCK_BUSY) break;
                Nv(status, "lossless_bitstream_lock_failed"); value.lockedOutput = slot.bitstream; slot.outputAcquired = true;
                evidence_.largestPacketBytes = (std::max)(evidence_.largestPacketBytes, output.bitstreamSizeInBytes);
                Require(output.bitstreamBufferPtr && output.bitstreamSizeInBytes > 0 && output.bitstreamSizeInBytes <= MaximumPacketBytes,
                    "lossless_packet_exceeds_bound", HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER));
                Require(output.outputTimeStamp == static_cast<std::uint64_t>(slot.time) && output.outputDuration == static_cast<std::uint64_t>(slot.duration) &&
                    output.pictureType != NV_ENC_PIC_TYPE_B && output.pictureType != NV_ENC_PIC_TYPE_BI && output.frameAvgQP == 0,
                    "lossless_output_contract_failed");
                const bool keyframe = output.pictureType == NV_ENC_PIC_TYPE_IDR;
                Require((slot.index != 0 || keyframe) && (keyframe || slot.index - value.lastKeyframe < value.config.frameRate * 2), "lossless_gop_bound_failed");
                if (keyframe) value.lastKeyframe = slot.index;
                ComPtr<IMFMediaBuffer> buffer; ComPtr<IMFSample> sample;
                Check(MFCreateMemoryBuffer(output.bitstreamSizeInBytes, &buffer), "lossless_packet_buffer_failed");
                BYTE* target = nullptr; Check(buffer->Lock(&target, nullptr, nullptr), "lossless_packet_buffer_lock_failed");
                std::memcpy(target, output.bitstreamBufferPtr, output.bitstreamSizeInBytes);
                Check(buffer->Unlock(), "lossless_packet_buffer_unlock_failed");
                Check(buffer->SetCurrentLength(output.bitstreamSizeInBytes), "lossless_packet_length_failed");
                Check(MFCreateSample(&sample), "lossless_sample_failed");
                Check(sample->AddBuffer(buffer.Get()), "lossless_sample_buffer_failed");
                Check(sample->SetSampleTime(slot.time), "lossless_sample_time_failed");
                Check(sample->SetSampleDuration(slot.duration), "lossless_sample_duration_failed");
                Check(sample->SetUINT32(MFSampleExtension_CleanPoint, keyframe ? 1 : 0), "lossless_sample_keyframe_failed");
                const UINT bytes = output.bitstreamSizeInBytes;
                Nv(api.nvEncUnlockBitstream(value.encodeSession, slot.bitstream), "lossless_bitstream_unlock_failed"); value.lockedOutput = nullptr;
                Nv(api.nvEncUnmapInputResource(value.encodeSession, slot.mapped), "lossless_input_unmap_failed"); slot.mapped = nullptr;
                slot.stage = Stage::Free; slot.outputAcquired = false; ++value.nextOutput;
                Check(value.observer->OnPacket(value.mediaType.Get(), sample.Get()), "lossless_packet_observer_failed");
                ++evidence_.outputSamples; evidence_.compressedBytes += bytes;
            }
            if (value.draining)
            {
                Require(GetTickCount64() - value.drainBegan < value.options.operationTimeoutMs, "lossless_drain_timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                if (value.eosSent && value.nextOutput == value.nextSubmit)
                {
                    const DWORD complete = WaitForSingleObject(value.eosEvent, 0);
                    Require(complete == WAIT_OBJECT_0 || complete == WAIT_TIMEOUT, "lossless_eos_event_wait_failed");
                    if (complete == WAIT_OBJECT_0) { evidence_.drainComplete = true; evidence_.reason = "lossless_drain_complete"; }
                }
            }
            return true;
        }
        catch (const Failure& error) { return Fail(error.reason, error.hr, error.nvencStatus); }
        catch (const std::bad_alloc&) { return Fail("lossless_packet_allocation_failed", E_OUTOFMEMORY); }
        catch (...) { return Fail("lossless_pump_failed", E_FAIL); }
    }

    bool NvencLosslessVideoSession::BeginDrain() noexcept
    {
        try { Guard(); if (!impl_->draining) { impl_->draining = true; impl_->drainBegan = GetTickCount64(); } return true; }
        catch (const Failure& error) { return Fail(error.reason, error.hr, error.nvencStatus); }
        catch (...) { return Fail("lossless_begin_drain_failed", E_FAIL); }
    }
    bool NvencLosslessVideoSession::Drain() noexcept
    {
        if (!BeginDrain()) return false;
        while (!evidence_.drainComplete) { if (!Pump()) return false; if (!evidence_.drainComplete) Sleep(1); }
        return true;
    }
    HRESULT NvencLosslessVideoSession::Close() noexcept
    {
        if (closed_) return evidence_.cleanupHr;
        if (impl_ && impl_->owner && impl_->owner != GetCurrentThreadId())
        {
            evidence_.cleanupReason = "lossless_cleanup_wrong_worker";
            evidence_.resourcesRetained = true;
            return evidence_.cleanupHr = E_UNEXPECTED;
        }
        closed_ = true;
        if (impl_)
        {
            evidence_.cleanupHr = impl_->Close(); evidence_.cleanupReason = impl_->cleanupReason;
            evidence_.resourcesRetained = FAILED(evidence_.cleanupHr);
            if (SUCCEEDED(evidence_.cleanupHr)) impl_.reset();
        }
        return evidence_.cleanupHr;
    }
}
