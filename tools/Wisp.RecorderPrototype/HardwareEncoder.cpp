#include "HardwareEncoder.h"
#include "HardwareEncoderInternal.h"

#include <d3d10_1.h>
#include <dxgi1_2.h>
#include <evr.h>
#include <mfapi.h>
#include <mferror.h>
#include <codecapi.h>
#include <strmif.h>
#include <tlhelp32.h>
#include <wrl/implements.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <condition_variable>
#include <cwchar>
#include <functional>
#include <memory>
#include <mutex>
#include <vector>

namespace recorder::encoder
{
    using Microsoft::WRL::ComPtr;
    using Clock = std::chrono::steady_clock;
    namespace detail
    {
        bool ForzaRunning()
        {
            const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            Check(snapshot == INVALID_HANDLE_VALUE ? HRESULT_FROM_WIN32(GetLastError()) : S_OK,
                "process_enumeration_failed");
            struct CloseHandleOnExit { HANDLE value; ~CloseHandleOnExit() { CloseHandle(value); } } close{ snapshot };
            PROCESSENTRY32W entry{};
            entry.dwSize = sizeof(entry);
            Check(Process32FirstW(snapshot, &entry) ? S_OK : HRESULT_FROM_WIN32(GetLastError()),
                "process_enumeration_failed");
            do
            {
                if (_wcsicmp(entry.szExeFile, L"ForzaHorizon6.exe") == 0 ||
                    _wcsicmp(entry.szExeFile, L"ForzaHorizon5.exe") == 0 ||
                    _wcsicmp(entry.szExeFile, L"ForzaHorizon4.exe") == 0 ||
                    _wcsicmp(entry.szExeFile, L"ForzaMotorsport.exe") == 0) return true;
            } while (Process32NextW(snapshot, &entry));
            Check(GetLastError() == ERROR_NO_MORE_FILES ? S_OK : HRESULT_FROM_WIN32(GetLastError()),
                "process_enumeration_failed");
            return false;
        }

        bool Number(const wchar_t* text, UINT minimum, UINT maximum, UINT& value) noexcept
        {
            if (!text || !*text) return false;
            UINT parsed = 0;
            for (; *text; ++text)
            {
                if (*text < L'0' || *text > L'9') return false;
                const UINT digit = static_cast<UINT>(*text - L'0');
                if (parsed > (maximum - digit) / 10) return false;
                parsed = parsed * 10 + digit;
            }
            if (parsed < minimum || parsed > maximum) return false;
            value = parsed;
            return true;
        }

        LONGLONG FrameTime(UINT frame, UINT frameRate) noexcept
        {
            return static_cast<LONGLONG>(frame) * 10000000 / frameRate;
        }

        struct Activations
        {
            IMFActivate** values = nullptr;
            UINT32 count = 0;
            ~Activations()
            {
                for (UINT32 i = 0; i < count; ++i) if (values[i]) values[i]->Release();
                CoTaskMemFree(values);
            }
        };

        struct Variant
        {
            VARIANT value{};
            ~Variant() { VariantClear(&value); }
        };

        bool Supported(ICodecAPI* codec, const GUID& key)
        {
            const HRESULT hr = codec->IsSupported(&key);
            if (hr == S_FALSE || hr == E_NOTIMPL) return false;
            Check(hr, "codec_property_query_failed");
            return hr == S_OK;
        }

        void SetControls(ICodecAPI* codec, Evidence& evidence, UINT requiredGopFrames)
        {
            evidence.bPictureControlSupported = Supported(codec, CODECAPI_AVEncMPVDefaultBPictureCount);
            if (evidence.bPictureControlSupported)
            {
                Variant requested;
                requested.value.vt = VT_UI4;
                requested.value.ulVal = 0;
                Check(codec->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &requested.value),
                    "zero_b_picture_setting_rejected");
            }
            evidence.lowLatencySupported = Supported(codec, CODECAPI_AVLowLatencyMode);
            if (evidence.lowLatencySupported)
            {
                Variant requested;
                requested.value.vt = VT_BOOL;
                requested.value.boolVal = VARIANT_TRUE;
                Check(codec->SetValue(&CODECAPI_AVLowLatencyMode, &requested.value),
                    "low_latency_setting_rejected");
            }
            // No portable MF lookahead-depth property is assumed. Low latency
            // does not prove vendor-internal lookahead or bitstream quality.
            if (requiredGopFrames)
            {
                CheckCodecSuccess(codec->IsSupported(&CODECAPI_AVEncMPVGOPSize), "gop_control_unsupported");
                evidence.gopControlSupported = true;
                evidence.gopModifiableQueryHr = codec->IsModifiable(&CODECAPI_AVEncMPVGOPSize);
                CheckGopModifiability(evidence.gopModifiableQueryHr);
                Variant requested;
                requested.value.vt = VT_UI4;
                requested.value.ulVal = requiredGopFrames;
                // SetValue S_FALSE means read-only; it is not an acknowledgement.
                CheckCodecSuccess(codec->SetValue(&CODECAPI_AVEncMPVGOPSize, &requested.value), "gop_setting_rejected");
            }
        }

        void VerifyControls(ICodecAPI* codec, Evidence& evidence, UINT requiredGopFrames)
        {
            if (evidence.bPictureControlSupported)
            {
                Variant observed;
                Check(codec->GetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &observed.value),
                    "zero_b_picture_readback_failed");
                Require(observed.value.vt == VT_UI4 && observed.value.ulVal == 0,
                    "zero_b_picture_readback_mismatch");
                evidence.bPictureZeroReadback = true;
            }
            if (evidence.lowLatencySupported)
            {
                Variant observed;
                Check(codec->GetValue(&CODECAPI_AVLowLatencyMode, &observed.value),
                    "low_latency_readback_failed");
                Require(observed.value.vt == VT_BOOL && observed.value.boolVal != VARIANT_FALSE,
                    "low_latency_readback_mismatch");
                evidence.lowLatencyReadback = true;
            }
            if (requiredGopFrames)
            {
                Variant observed;
                CheckCodecSuccess(codec->GetValue(&CODECAPI_AVEncMPVGOPSize, &observed.value), "gop_readback_failed");
                if (observed.value.vt == VT_UI4) evidence.negotiatedGopFrames = observed.value.ulVal;
                Require(GopReadbackMatches(requiredGopFrames, observed.value), "gop_readback_mismatch");
                evidence.gopSizeReadback = true;
            }
        }

        ComPtr<IMFMediaType> MediaType(bool compressed, const EncodeConfig& configuration)
        {
            ComPtr<IMFMediaType> type;
            Check(MFCreateMediaType(&type), "media_type_creation_failed");
            Check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "media_type_attribute_failed");
            Check(type->SetGUID(MF_MT_SUBTYPE, compressed ? MFVideoFormat_H264 : MFVideoFormat_NV12), "media_type_attribute_failed");
            Check(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, configuration.width, configuration.height), "media_type_attribute_failed");
            Check(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, configuration.frameRate, 1), "media_type_attribute_failed");
            Check(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO,
                configuration.pixelAspectNumerator, configuration.pixelAspectDenominator), "media_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "media_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709), "media_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709), "media_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709), "media_type_attribute_failed");
            Check(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235), "media_type_attribute_failed");
            if (configuration.chromaSiting)
                Check(type->SetUINT32(MF_MT_VIDEO_CHROMA_SITING, configuration.chromaSiting), "media_type_attribute_failed");
            if (compressed)
            {
                Check(type->SetUINT32(MF_MT_AVG_BITRATE, configuration.bitrate), "media_type_attribute_failed");
                Check(type->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base), "media_type_attribute_failed");
            }
            else
            {
                Check(type->SetUINT32(MF_MT_DEFAULT_STRIDE, configuration.width), "media_type_attribute_failed");
                Check(type->SetUINT32(MF_MT_SAMPLE_SIZE, configuration.width * configuration.height * 3 / 2), "media_type_attribute_failed");
                Check(type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE), "media_type_attribute_failed");
                Check(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE), "media_type_attribute_failed");
            }
            return type;
        }

        void VerifyOutput(HardwareSession& session, const EncodeConfig& configuration, Evidence& evidence)
        {
            ComPtr<IMFMediaType> current;
            Check(session.transform->GetOutputCurrentType(session.outputId, &current), "output_type_readback_failed");
            GUID major{}, subtype{};
            UINT32 width = 0, height = 0, numerator = 0, denominator = 0, profile = 0;
            Check(current->GetGUID(MF_MT_MAJOR_TYPE, &major), "output_type_readback_failed");
            Check(current->GetGUID(MF_MT_SUBTYPE, &subtype), "output_type_readback_failed");
            Check(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &width, &height), "output_type_readback_failed");
            Check(MFGetAttributeRatio(current.Get(), MF_MT_FRAME_RATE, &numerator, &denominator), "output_type_readback_failed");
            Check(current->GetUINT32(MF_MT_MPEG2_PROFILE, &profile), "output_type_readback_failed");
            Require(major == MFMediaType_Video && subtype == MFVideoFormat_H264 &&
                width == configuration.width && height == configuration.height && numerator == configuration.frameRate && denominator == 1 &&
                profile == eAVEncH264VProfile_Base, "negotiated_output_mismatch");
            UINT32 aspectNumerator = 0, aspectDenominator = 0, bitrate = 0;
            Check(MFGetAttributeRatio(current.Get(), MF_MT_PIXEL_ASPECT_RATIO, &aspectNumerator, &aspectDenominator), "output_type_readback_failed");
            Check(current->GetUINT32(MF_MT_AVG_BITRATE, &bitrate), "output_type_readback_failed");
            Require(aspectNumerator == configuration.pixelAspectNumerator && aspectDenominator == configuration.pixelAspectDenominator &&
                bitrate == configuration.bitrate, "negotiated_output_mismatch");
            if (configuration.chromaSiting)
            {
                UINT32 chroma = 0;
                Check(current->GetUINT32(MF_MT_VIDEO_CHROMA_SITING, &chroma), "output_chroma_readback_failed");
                Require(chroma == configuration.chromaSiting, "output_chroma_readback_mismatch");
            }
            evidence.baselineProfile = true;
            UINT32 bytes = 0;
            const HRESULT headerHr = current->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &bytes);
            if (headerHr != MF_E_ATTRIBUTENOTFOUND) Check(headerHr, "sequence_header_query_failed");
            Require(bytes <= 65536, "sequence_header_size_invalid");
            evidence.sequenceHeaderBytes = (std::max)(evidence.sequenceHeaderBytes, bytes);
            evidence.configurationNegotiated = true;
        }

        void Initialize(ID3D11Device* device, UINT candidateIndex, const EncodeConfig& configuration,
            HardwareSession& session, Evidence& evidence, bool requireGameClosed, UINT requiredGopFrames)
        {
            if (const char* reason = ValidateConfiguration(configuration)) throw Failure{ reason, E_INVALIDARG };
            evidence.configuration = configuration;
            evidence.configurationValidated = true;
            Require(requiredGopFrames == 0 || requiredGopFrames == 2 * configuration.frameRate, "gop_request_invalid");
            evidence.requestedGopFrames = requiredGopFrames;
            Require(device && !session.transform && candidateIndex <= 15, "invalid_initializer_arguments");
            if (requireGameClosed) Require(!ForzaRunning(), "forza_running");
            ComPtr<IDXGIDevice> dxgi;
            Check(device->QueryInterface(IID_PPV_ARGS(&dxgi)), "device_dxgi_query_failed");
            ComPtr<IDXGIAdapter> adapter;
            Check(dxgi->GetAdapter(&adapter), "device_adapter_query_failed");
            DXGI_ADAPTER_DESC description{};
            Check(adapter->GetDesc(&description), "device_adapter_description_failed");
            ComPtr<IDXGIAdapter1> adapter1;
            Check(adapter.As(&adapter1), "hardware_adapter_query_failed");
            DXGI_ADAPTER_DESC1 description1{};
            Check(adapter1->GetDesc1(&description1), "hardware_adapter_query_failed");
            Require((description1.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0, "software_adapter_refused");

            ComPtr<IMFAttributes> filter;
            Check(MFCreateAttributes(&filter, 1), "encoder_filter_creation_failed");
            Check(filter->SetBlob(MFT_ENUM_ADAPTER_LUID,
                reinterpret_cast<const UINT8*>(&description.AdapterLuid), sizeof(LUID)), "adapter_filter_failed");
            MFT_REGISTER_TYPE_INFO input{ MFMediaType_Video, MFVideoFormat_NV12 };
            MFT_REGISTER_TYPE_INFO output{ MFMediaType_Video, MFVideoFormat_H264 };
            Activations candidates;
            Check(MFTEnum2(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                &input, &output, filter.Get(), &candidates.values, &candidates.count), "hardware_enumeration_failed");
            evidence.hardwareCandidates = candidates.count;
            Require(candidateIndex < candidates.count && candidates.values[candidateIndex], "hardware_candidate_unavailable");
            session.activation = candidates.values[candidateIndex];
            UINT32 linkLength = 0;
            Check(session.activation->GetStringLength(MFT_ENUM_HARDWARE_URL_Attribute, &linkLength), "hardware_registration_missing");
            Require(linkLength > 0, "hardware_registration_missing");
            evidence.hardwareRegistration = true;
            if (requireGameClosed) Require(!ForzaRunning(), "forza_running");
            Check(session.activation->ActivateObject(IID_PPV_ARGS(&session.transform)), "hardware_activation_failed");
            Check(session.transform.As(&session.shutdown), "async_shutdown_interface_missing");
            ComPtr<IMFAttributes> attributes;
            Check(session.transform->GetAttributes(&attributes), "transform_attributes_failed");
            linkLength = 0;
            Check(attributes->GetStringLength(MFT_ENUM_HARDWARE_URL_Attribute, &linkLength), "activated_hardware_attribute_missing");
            Require(linkLength > 0, "activated_hardware_attribute_missing");
            evidence.activatedHardwareAttribute = true;
            UINT32 value = 0;
            Check(attributes->GetUINT32(MF_TRANSFORM_ASYNC, &value), "asynchronous_attribute_missing");
            Require(value == TRUE, "synchronous_encoder_refused");
            evidence.asynchronous = true;
            Check(attributes->GetUINT32(MF_SA_D3D11_AWARE, &value), "d3d11_attribute_missing");
            Require(value == TRUE, "non_d3d11_encoder_refused");
            evidence.d3d11Aware = true;
            Check(attributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE), "async_unlock_failed");
            UINT token = 0;
            Check(MFCreateDXGIDeviceManager(&token, &session.manager), "device_manager_creation_failed");
            Check(session.manager->ResetDevice(device, token), "device_manager_binding_failed");
            Check(session.transform->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                reinterpret_cast<ULONG_PTR>(session.manager.Get())), "same_device_manager_rejected");
            evidence.sameDeviceManager = true;

            DWORD inputs = 0, outputs = 0;
            Check(session.transform->GetStreamCount(&inputs, &outputs), "stream_count_failed");
            Require(inputs == 1 && outputs == 1, "multiple_streams_unsupported");
            const HRESULT ids = session.transform->GetStreamIDs(1, &session.inputId, 1, &session.outputId);
            if (ids != E_NOTIMPL) Check(ids, "stream_ids_failed");
            ComPtr<ICodecAPI> codec;
            Check(session.transform.As(&codec), "codec_controls_missing");
            SetControls(codec.Get(), evidence, requiredGopFrames);
            const auto outputType = MediaType(true, configuration);
            Check(session.transform->SetOutputType(session.outputId, outputType.Get(), 0), "h264_baseline_configuration_rejected");
            const auto inputType = MediaType(false, configuration);
            Check(session.transform->SetInputType(session.inputId, inputType.Get(), 0), "nv12_configuration_rejected");
            VerifyOutput(session, configuration, evidence);
            VerifyControls(codec.Get(), evidence, requiredGopFrames);
            ComPtr<IMFAttributes> inputAttributes;
            const HRESULT attributesHr = session.transform->GetInputStreamAttributes(session.inputId, &inputAttributes);
            if (attributesHr == E_NOTIMPL) evidence.inputBindFlags = D3D11_BIND_RENDER_TARGET;
            else
            {
                Check(attributesHr, "input_attributes_failed");
                Require(inputAttributes.Get() != nullptr, "input_attribute_store_missing");
                evidence.inputAttributeStoreAvailable = true;
                const HRESULT bind = inputAttributes->GetUINT32(MF_SA_D3D11_BINDFLAGS, &evidence.inputBindFlags);
                if (bind == MF_E_ATTRIBUTENOTFOUND) evidence.inputBindFlags = D3D11_BIND_RENDER_TARGET;
                else
                {
                    Check(bind, "input_bind_flags_failed");
                    evidence.inputBindHintAvailable = true;
                }
            }
            constexpr UINT allowed = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_VIDEO_ENCODER;
            Require((evidence.inputBindFlags & ~allowed) == 0, "input_bind_flags_incompatible");
        }

        void CreateTextures(ID3D11Device* device, const std::shared_ptr<SharedState>& state, UINT bindFlags,
            const EncodeConfig& configuration, bool provider)
        {
            std::vector<BYTE> pixels;
            if (!provider) pixels.assign(static_cast<size_t>(configuration.width) * configuration.height * 3 / 2, 128);
            D3D11_TEXTURE2D_DESC description{};
            description.Width = configuration.width;
            description.Height = configuration.height;
            description.MipLevels = 1;
            description.ArraySize = 1;
            description.Format = DXGI_FORMAT_NV12;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            description.BindFlags = bindFlags;
            for (UINT index = 0; index < PoolSize; ++index)
            {
                // Three generated neutral grey levels; no captured pixel data.
                if (!provider)
                    std::fill(pixels.begin(), pixels.begin() + static_cast<size_t>(configuration.width) * configuration.height,
                        static_cast<BYTE>(32 + index * 80));
                D3D11_SUBRESOURCE_DATA data{};
                data.pSysMem = pixels.data();
                data.SysMemPitch = configuration.width;
                Check(device->CreateTexture2D(&description, provider ? nullptr : &data, &state->textures[index]), "nv12_texture_creation_failed");
            }
        }

        bool Submit(HardwareSession& session, const std::shared_ptr<SharedState>& state, UINT slot, UINT frame,
            const EncodeConfig&, FixtureFrameProvider* provider, ID3D10Multithread* multithread, Evidence& evidence,
            LONGLONG time100ns, LONGLONG duration100ns)
        {
            bool allocatorArmed = false;
            try
            {
                if (provider)
                {
                    Require(multithread != nullptr && multithread->GetMultithreadProtected(), "provider_context_protection_missing");
                    struct GraphicsLock
                    {
                        ID3D10Multithread* value;
                        explicit GraphicsLock(ID3D10Multithread* resource) : value(resource) { value->Enter(); }
                        ~GraphicsLock() { value->Leave(); }
                    } lock{ multithread };
                    const HRESULT written = provider->Fill(frame, slot, state->textures[slot].Get());
                    if (written == S_FALSE)
                    {
                        // No sample reached the encoder. Any partial conversion
                        // remains ordered before this surface's next GPU use.
                        std::lock_guard<std::mutex> guard(state->mutex);
                        Require(state->ownership.Release(slot), "unsubmitted_surface_release_failed");
                        return false;
                    }
                    Check(written, "fixture_frame_provider_failed");
                    Require(written == S_OK, "fixture_frame_not_written");
                    ++evidence.providerFramesFilled;
                }
                ComPtr<IMFMediaBuffer> buffer;
                Check(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), state->textures[slot].Get(), 0, FALSE, &buffer),
                    "dxgi_sample_buffer_failed");
                ComPtr<IMFSample> sample;
                Check(MFCreateVideoSampleFromSurface(nullptr, &sample), "tracked_sample_creation_failed");
                Check(sample->AddBuffer(buffer.Get()), "sample_buffer_attach_failed");
                Check(sample->SetSampleTime(time100ns), "input_timestamp_failed");
                Check(sample->SetSampleDuration(duration100ns), "input_duration_failed");
                ComPtr<IMFTrackedSample> tracked;
                Check(sample.As(&tracked), "tracked_sample_interface_missing");
                const std::weak_ptr<SharedState> weak = state;
                const auto callback = Microsoft::WRL::Make<Callback>([weak, slot](IMFAsyncResult*) -> HRESULT
                {
                    if (auto shared = weak.lock())
                    {
                        {
                            std::lock_guard<std::mutex> lock(shared->mutex);
                            if (!shared->ownership.Release(slot)) shared->callbackHr = E_UNEXPECTED;
                        }
                        shared->changed.notify_all();
                    }
                    return S_OK;
                });
                Require(callback.Get() != nullptr, "sample_callback_allocation_failed");
                Check(tracked->SetAllocator(callback.Get(), nullptr), "sample_completion_tracking_failed");
                allocatorArmed = true;
                Check(session.transform->ProcessInput(session.inputId, sample.Get(), 0), "hardware_process_input_failed");
                // All local sample references die here. Only the tracked-sample
                // completion callback can release this surface slot afterwards.
                return true;
            }
            catch (...)
            {
                if (!allocatorArmed)
                {
                    std::lock_guard<std::mutex> lock(state->mutex);
                    (void)state->ownership.Release(slot);
                }
                throw;
            }
        }

        void ReadOutput(HardwareSession& session, const EncodeConfig&, Evidence& evidence, FixtureObserver* observer,
            LONGLONG expectedTime100ns, bool checksum, GopTracker* gop)
        {
            MFT_OUTPUT_STREAM_INFO info{};
            Check(session.transform->GetOutputStreamInfo(session.outputId, &info), "output_stream_info_failed");
            ComPtr<IMFSample> owned;
            if (!(info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)))
            {
                DWORD alignmentMask = 0;
                Require(info.cbSize > 0 && info.cbSize <= 16 * 1024 * 1024 && AlignmentMask(info.cbAlignment, alignmentMask),
                    "output_allocation_size_invalid");
                ComPtr<IMFMediaBuffer> buffer;
                Check(MFCreateAlignedMemoryBuffer(info.cbSize, alignmentMask, &buffer), "compressed_buffer_allocation_failed");
                Check(MFCreateSample(&owned), "output_sample_creation_failed");
                Check(owned->AddBuffer(buffer.Get()), "output_buffer_attach_failed");
            }
            MFT_OUTPUT_DATA_BUFFER output{};
            output.dwStreamID = session.outputId;
            output.pSample = owned.Get();
            DWORD status = 0;
            const HRESULT hr = session.transform->ProcessOutput(0, 1, &output, &status);
            ComPtr<IMFSample> provided;
            if (output.pSample && output.pSample != owned.Get()) provided.Attach(output.pSample);
            ComPtr<IMFCollection> events;
            events.Attach(output.pEvents);
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE) throw Failure{ "encoder_output_type_changed", hr };
            Check(hr, "hardware_process_output_failed");
            Require(output.pSample != nullptr &&
                (output.dwStatus & MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE) != MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE,
                "empty_output_sample");
            LONGLONG time = 0;
            Check(output.pSample->GetSampleTime(&time), "output_timestamp_missing");
            Require(time == expectedTime100ns, "output_timestamp_order_mismatch");
            UINT32 clean = 0;
            const HRESULT cleanHr = output.pSample->GetUINT32(MFSampleExtension_CleanPoint, &clean);
            if (cleanHr == MF_E_ATTRIBUTENOTFOUND) ++evidence.missingCleanPointAttributes;
            else
            {
                Check(cleanHr, "clean_point_query_failed");
                if (clean != 0) ++evidence.cleanPoints;
            }
            // Live sessions verify actual random-access spacing before any
            // packet reaches storage. A missing CleanPoint attribute is false.
            if (gop)
                if (const char* reason = gop->Observe(evidence.outputSamples, cleanHr == S_OK && clean != 0, evidence))
                    throw Failure{ reason, E_FAIL };
            DWORD count = 0;
            Check(output.pSample->GetBufferCount(&count), "output_buffer_count_failed");
            Require(count > 0 && count <= 16, "output_buffer_count_invalid");
            std::uint64_t sampleBytes = 0;
            for (DWORD index = 0; index < count; ++index)
            {
                ComPtr<IMFMediaBuffer> buffer;
                Check(output.pSample->GetBufferByIndex(index, &buffer), "output_buffer_query_failed");
                DWORD bytes = 0;
                Check(buffer->GetCurrentLength(&bytes), "output_length_failed");
                Require(bytes <= 16 * 1024 * 1024 && sampleBytes + bytes <= 16 * 1024 * 1024,
                    "compressed_sample_too_large");
                if (checksum)
                {
                    BYTE* data = nullptr;
                    DWORD lockedBytes = 0;
                    Check(buffer->Lock(&data, nullptr, &lockedBytes), "compressed_buffer_lock_failed");
                    struct Unlock { IMFMediaBuffer* value; ~Unlock() { value->Unlock(); } } unlock{ buffer.Get() };
                    Require(lockedBytes == bytes && (data || bytes == 0), "compressed_buffer_length_changed");
                    for (DWORD i = 0; i < bytes; ++i)
                    {
                        evidence.checksumFnv1a64 ^= data[i];
                        evidence.checksumFnv1a64 *= 1099511628211ull;
                    }
                }
                sampleBytes += bytes;
            }
            Require(sampleBytes > 0, "empty_encoded_packet");
            if (observer)
            {
                ComPtr<IMFMediaType> type;
                Check(session.transform->GetOutputCurrentType(session.outputId, &type), "observer_output_type_failed");
                Check(observer->OnOutput(type.Get(), output.pSample), "fixture_output_observer_failed");
            }
            evidence.encodedBytes += sampleBytes;
            ++evidence.outputSamples;
        }

        struct Runtime
        {
            bool com = false;
            bool mf = false;
            ~Runtime()
            {
                if (mf) MFShutdown();
                if (com) CoUninitialize();
            }
            void Start()
            {
                Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "mta_initialization_failed");
                com = true;
                Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "mf_initialization_failed");
                mf = true;
            }
        };
    }

    using namespace detail;

    const char* ValidateConfiguration(const EncodeConfig& configuration) noexcept
    {
        const bool preset = (configuration.width == 640 && configuration.height == 360) ||
            (configuration.width == 854 && configuration.height == 480) ||
            (configuration.width == 1280 && configuration.height == 720) ||
            (configuration.width == 1920 && configuration.height == 1080) ||
            (configuration.width == 2560 && configuration.height == 1440) ||
            (configuration.width == 3840 && configuration.height == 2160);
        if (!preset) return "encoding_resolution_unsupported";
        if (configuration.frameRate != 30 && configuration.frameRate != 60) return "encoding_frame_rate_unsupported";
        if (configuration.bitrate < MinimumBitrate || configuration.bitrate > MaximumBitrate)
            return "encoding_bitrate_out_of_bounds";
        // 854x480 is the even NV12 raster nearest 16:9. Preserve the exact
        // display aspect with SAR 1280:1281, rather than silently stretching.
        const UINT numerator = configuration.height == 480 ? 1280 : 1;
        const UINT denominator = configuration.height == 480 ? 1281 : 1;
        if (configuration.pixelAspectNumerator != numerator || configuration.pixelAspectDenominator != denominator)
            return "encoding_pixel_aspect_unsupported";
        constexpr UINT leftProgressive = MFVideoChromaSubsampling_MPEG2 | MFVideoChromaSubsampling_ProgressiveChroma;
        if (configuration.chromaSiting != 0 && configuration.chromaSiting != MFVideoChromaSubsampling_MPEG2 &&
            configuration.chromaSiting != leftProgressive)
            return "encoding_chroma_siting_unsupported";
        return nullptr;
    }

    bool ParseOptions(int argc, const wchar_t* const* argv, Options& result) noexcept
    {
        result = {};
        if (argc == 1) return true;
        if (argc < 1 || !argv) return false;
        if (argc == 2 && wcscmp(argv[1], L"--help") == 0) return true;
        if (argc == 2 && wcscmp(argv[1], L"--self-test") == 0)
        {
            result.mode = Mode::SelfTest;
            return true;
        }
        if (wcscmp(argv[1], L"--encode-fixture") != 0) return false;
        result.mode = Mode::Encode;
        UINT seen = 0;
        for (int i = 2; i < argc; i += 2)
        {
            if (i + 1 >= argc) return false;
            UINT bit = 0;
            UINT* field = nullptr;
            UINT minimum = 0, maximum = 0;
            if (wcscmp(argv[i], L"--adapter-index") == 0) { bit = 1; field = &result.adapterIndex; maximum = 15; }
            else if (wcscmp(argv[i], L"--encoder-index") == 0) { bit = 2; field = &result.encoderIndex; maximum = 15; }
            else if (wcscmp(argv[i], L"--frames") == 0) { bit = 4; field = &result.frames; minimum = 1; maximum = 150; }
            else if (wcscmp(argv[i], L"--timeout-ms") == 0) { bit = 8; field = &result.timeoutMs; minimum = 1000; maximum = 30000; }
            else return false;
            if ((seen & bit) || !Number(argv[i + 1], minimum, maximum, *field)) return false;
            seen |= bit;
        }
        return true;
    }

    HardwareSession::~HardwareSession() { (void)Close(); }
    HRESULT HardwareSession::Close() noexcept
    {
        HRESULT first = S_OK;
        const auto remember = [&first](HRESULT hr) { if (FAILED(hr) && hr != MF_E_SHUTDOWN && SUCCEEDED(first)) first = hr; };
        if (transform && streaming)
        {
            remember(transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0));
            remember(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0));
            streaming = false;
        }
        if (shutdown) remember(shutdown->Shutdown());
        if (activation) remember(activation->ShutdownObject());
        shutdown.Reset();
        transform.Reset();
        activation.Reset();
        manager.Reset();
        return first;
    }

    bool InitializeHardwareH264(ID3D11Device* device, UINT candidateIndex,
        HardwareSession& session, Evidence& evidence) noexcept
    {
        return InitializeHardwareH264(device, candidateIndex, EncodeConfig{}, session, evidence);
    }

    bool InitializeHardwareH264(ID3D11Device* device, UINT candidateIndex,
        const EncodeConfig& configuration, HardwareSession& session, Evidence& evidence) noexcept
    {
        const EncodeConfig selected = configuration;
        try { Initialize(device, candidateIndex, selected, session, evidence, true); return true; }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }
        evidence.cleanupHr = session.Close();
        return false;
    }

    Evidence RunSyntheticFixture(const Options& options, const std::atomic<bool>& cancelled,
        FixtureObserver* observer, FixtureFrameProvider* provider) noexcept
    {
        Evidence evidence;
        const EncodeConfig configuration = options.configuration;
        const auto started = Clock::now();
        Runtime runtime;
        HardwareSession session;
        std::shared_ptr<SharedState> state;
        ComPtr<IMFMediaEventGenerator> generator;
        ComPtr<Callback> eventCallback;
        try
        {
            Require(options.mode == Mode::Encode && options.adapterIndex <= 15 && options.encoderIndex <= 15 &&
                options.frames >= 1 && options.frames <= 150 && options.timeoutMs >= 1000 && options.timeoutMs <= 30000,
                "invalid_fixture_options");
            if (const char* reason = ValidateConfiguration(configuration)) throw Failure{ reason, E_INVALIDARG };
            evidence.configuration = configuration;
            evidence.configurationValidated = true;
            evidence.frameProviderUsed = provider != nullptr;
            const auto deadline = started + std::chrono::milliseconds(options.timeoutMs);
            auto lastGameCheck = started - std::chrono::seconds(1);
            const auto checkGuard = [&]()
            {
                Require(!cancelled.load(), "cancelled");
                Require(Clock::now() < deadline, "encoder_deadline_reached");
                if (Clock::now() - lastGameCheck >= std::chrono::milliseconds(100))
                {
                    Require(!ForzaRunning(), "forza_running");
                    lastGameCheck = Clock::now();
                }
            };
            checkGuard();
            runtime.Start();
            ComPtr<IDXGIFactory1> factory;
            Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "adapter_factory_failed");
            ComPtr<IDXGIAdapter1> adapter;
            Check(factory->EnumAdapters1(options.adapterIndex, &adapter), "selected_adapter_unavailable");
            DXGI_ADAPTER_DESC1 adapterDescription{};
            Check(adapter->GetDesc1(&adapterDescription), "adapter_description_failed");
            Require(!(adapterDescription.Flags & DXGI_ADAPTER_FLAG_SOFTWARE), "software_adapter_refused");
            checkGuard();
            ComPtr<ID3D11Device> device;
            ComPtr<ID3D11DeviceContext> context;
            const D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
            D3D_FEATURE_LEVEL selected{};
            Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION, &device, &selected, &context),
                "hardware_d3d11_device_failed");
            ComPtr<ID3D10Multithread> multithread;
            Check(device.As(&multithread), "device_multithread_interface_missing");
            (void)multithread->SetMultithreadProtected(TRUE);
            Require(multithread->GetMultithreadProtected() != FALSE, "device_multithread_protection_failed");
            checkGuard();
            if (!InitializeHardwareH264(device.Get(), options.encoderIndex, configuration, session, evidence))
                throw Failure{ evidence.reason, evidence.hr };
            checkGuard();
            state = std::make_shared<SharedState>();
            evidence.allocatedInputBindFlags = evidence.inputBindFlags | (provider ? D3D11_BIND_RENDER_TARGET : 0u);
            CreateTextures(device.Get(), state, evidence.allocatedInputBindFlags, configuration, provider != nullptr);
            if (provider)
            {
                Check(provider->Initialize(device.Get(), configuration), "fixture_frame_provider_initialization_failed");
                checkGuard();
            }
            Check(session.transform.As(&generator), "async_event_interface_missing");
            const std::weak_ptr<SharedState> weak = state;
            const ComPtr<IMFMediaEventGenerator> callbackGenerator = generator;
            eventCallback = Microsoft::WRL::Make<Callback>([weak, callbackGenerator](IMFAsyncResult* result) -> HRESULT
            {
                ComPtr<IMFMediaEvent> event;
                const HRESULT hr = callbackGenerator->EndGetEvent(result, &event);
                if (auto shared = weak.lock())
                {
                    {
                        std::lock_guard<std::mutex> lock(shared->mutex);
                        shared->eventPending = false;
                        if (!shared->stopping)
                        {
                            if (FAILED(hr)) shared->callbackHr = hr;
                            else if (shared->event) shared->callbackHr = E_UNEXPECTED;
                            else shared->event = event;
                        }
                    }
                    shared->changed.notify_all();
                }
                return S_OK;
            });
            Require(eventCallback.Get() != nullptr, "event_callback_allocation_failed");
            const auto arm = [&]()
            {
                {
                    std::lock_guard<std::mutex> lock(state->mutex);
                    Require(!state->eventPending && !state->event && !state->stopping, "event_ownership_invalid");
                    state->eventPending = true;
                }
                const HRESULT hr = generator->BeginGetEvent(eventCallback.Get(), nullptr);
                if (FAILED(hr))
                {
                    std::lock_guard<std::mutex> lock(state->mutex);
                    state->eventPending = false;
                    throw Failure{ "async_event_subscription_failed", hr };
                }
            };
            arm();
            Check(session.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "begin_streaming_failed");
            session.streaming = true;
            Check(session.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "start_stream_failed");
            UINT inputCredits = 0;
            bool draining = false;
            while (!evidence.drainComplete)
            {
                checkGuard();
                Check(device->GetDeviceRemovedReason(), "d3d11_device_removed");
                while (inputCredits && evidence.submitted < options.frames)
                {
                    checkGuard();
                    int slot = -1;
                    {
                        std::lock_guard<std::mutex> lock(state->mutex);
                        Check(state->callbackHr, "asynchronous_callback_failed");
                        slot = state->ownership.Acquire();
                    }
                    if (slot < 0) break;
                    Submit(session, state, static_cast<UINT>(slot), evidence.submitted,
                        configuration, provider, multithread.Get(), evidence,
                        FrameTime(evidence.submitted, configuration.frameRate),
                        FrameTime(evidence.submitted + 1, configuration.frameRate) - FrameTime(evidence.submitted, configuration.frameRate));
                    if (observer) Check(observer->OnInput(evidence.submitted, static_cast<UINT>(slot)), "fixture_input_observer_failed");
                    ++evidence.submitted;
                    --inputCredits;
                }
                if (!draining && evidence.submitted == options.frames)
                {
                    Check(session.transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, session.inputId), "end_of_stream_failed");
                    Check(session.transform->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, session.inputId), "drain_request_failed");
                    draining = true;
                    inputCredits = 0;
                }
                ComPtr<IMFMediaEvent> event;
                {
                    std::unique_lock<std::mutex> lock(state->mutex);
                    state->changed.wait_until(lock, (std::min)(deadline, Clock::now() + std::chrono::milliseconds(50)), [&]()
                    {
                        return state->event || FAILED(state->callbackHr) ||
                            (inputCredits && state->ownership.current < PoolSize) || cancelled.load();
                    });
                    Check(state->callbackHr, "asynchronous_callback_failed");
                    event = std::move(state->event);
                }
                if (!event) continue;
                HRESULT status = S_OK;
                Check(event->GetStatus(&status), "event_status_query_failed");
                Check(status, "hardware_event_failed");
                MediaEventType type = MEUnknown;
                Check(event->GetType(&type), "event_type_query_failed");
                if (type == METransformNeedInput)
                {
                    UINT32 stream = 0;
                    Check(event->GetUINT32(MF_EVENT_MFT_INPUT_STREAM_ID, &stream), "input_event_stream_failed");
                    Require(stream == session.inputId, "input_event_stream_mismatch");
                    ++evidence.needInputEvents;
                    if (!draining)
                    {
                        Require(inputCredits < 32, "input_credit_limit_exceeded");
                        ++inputCredits;
                    }
                }
                else if (type == METransformHaveOutput)
                {
                    ++evidence.haveOutputEvents;
                    Require(evidence.outputSamples < options.frames, "excess_output_samples");
                    ReadOutput(session, configuration, evidence, observer,
                        FrameTime(evidence.outputSamples, configuration.frameRate), true);
                }
                else if (type == METransformDrainComplete)
                {
                    Require(draining, "unexpected_drain_completion");
                    UINT32 stream = 0;
                    Check(event->GetUINT32(MF_EVENT_MFT_INPUT_STREAM_ID, &stream), "drain_event_stream_failed");
                    Require(stream == session.inputId, "drain_event_stream_mismatch");
                    evidence.drainComplete = true;
                }
                else throw Failure{ "unexpected_transform_event", E_UNEXPECTED };
                if (!evidence.drainComplete) arm();
            }
            VerifyOutput(session, configuration, evidence);
            Require(evidence.submitted == options.frames && evidence.outputSamples == options.frames &&
                evidence.encodedBytes > 0, "encoded_frame_count_mismatch");
            Require(evidence.cleanPoints > 0, "clean_point_evidence_missing");
            Require(evidence.sequenceHeaderBytes > 0, "sequence_header_evidence_missing");
            evidence.completed = true;
            evidence.reason = "synthetic_hardware_encode_completed";
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }

        if (state)
        {
            std::lock_guard<std::mutex> lock(state->mutex);
            state->stopping = true;
        }
        const HRESULT closed = session.Close();
        if (FAILED(closed)) evidence.cleanupHr = closed;
        generator.Reset();
        eventCallback.Reset();
        if (state)
        {
            std::unique_lock<std::mutex> lock(state->mutex);
            state->changed.wait_for(lock, std::chrono::seconds(2), [&]()
            {
                return state->ownership.current == 0 && !state->eventPending;
            });
            evidence.samplesReturned = state->ownership.current == 0;
            evidence.eventCallbackDrained = !state->eventPending;
            evidence.returnedSamples = state->ownership.returned;
            evidence.peakOwnedSamples = state->ownership.peak;
            if (FAILED(state->callbackHr) && SUCCEEDED(evidence.cleanupHr)) evidence.cleanupHr = state->callbackHr;
        }
        if (evidence.completed && (!evidence.samplesReturned || !evidence.eventCallbackDrained || FAILED(evidence.cleanupHr)))
        {
            evidence.completed = false;
            evidence.reason = "encoder_cleanup_incomplete";
            evidence.hr = FAILED(evidence.cleanupHr) ? evidence.cleanupHr : E_FAIL;
        }
        evidence.elapsedMs = static_cast<std::uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - started).count());
        return evidence;
    }

    UINT RunContractTests() noexcept
    {
        UINT passed = 0;
        Options parsed;
        const wchar_t* defaults[]{ L"fixture" };
        if (!ParseOptions(1, defaults, parsed) || parsed.mode != Mode::Help) return 0;
        ++passed;
        const wchar_t* self[]{ L"fixture", L"--self-test" };
        if (!ParseOptions(2, self, parsed) || parsed.mode != Mode::SelfTest) return 0;
        ++passed;
        const wchar_t* encode[]{ L"fixture", L"--encode-fixture" };
        if (!ParseOptions(2, encode, parsed) || parsed.mode != Mode::Encode || parsed.frames != 60 || parsed.timeoutMs != 10000) return 0;
        ++passed;
        const wchar_t* valid[]{ L"fixture", L"--encode-fixture", L"--frames", L"150", L"--timeout-ms", L"30000", L"--adapter-index", L"15", L"--encoder-index", L"1" };
        if (!ParseOptions(10, valid, parsed) || parsed.frames != 150 || parsed.encoderIndex != 1) return 0;
        ++passed;
        const wchar_t* invalid[][6]{
            {L"fixture", L"--capture"},
            {L"fixture", L"--encode-fixture", L"--frames"},
            {L"fixture", L"--encode-fixture", L"--frames", L"0"},
            {L"fixture", L"--encode-fixture", L"--frames", L"151"},
            {L"fixture", L"--encode-fixture", L"--frames", L"-1"},
            {L"fixture", L"--encode-fixture", L"--frames", L"1.5"},
            {L"fixture", L"--encode-fixture", L"--frames", L"99999999999999999999"},
            {L"fixture", L"--encode-fixture", L"--frames", L"1", L"--frames", L"2"},
            {L"fixture", L"--encode-fixture", L"--timeout-ms", L"999"},
            {L"fixture", L"--encode-fixture", L"--timeout-ms", L"30001"},
            {L"fixture", L"--encode-fixture", L"--adapter-index", L"16"},
            {L"fixture", L"--self-test", L"--encode-fixture"},
            {L"fixture", L"--help", L"--encode-fixture"},
            {L"fixture", L"--encode-fixture", L"--output", L"file"}
        };
        for (const auto& args : invalid)
        {
            int argc = 0;
            while (argc < 6 && args[argc]) ++argc;
            if (ParseOptions(argc, args, parsed)) return 0;
            ++passed;
        }
        Ownership ownership;
        DWORD alignmentMask = 0;
        if (!AlignmentMask(0, alignmentMask) || alignmentMask != 0) return 0;
        ++passed;
        if (!AlignmentMask(1, alignmentMask) || alignmentMask != 0) return 0;
        ++passed;
        if (!AlignmentMask(16, alignmentMask) || alignmentMask != 15) return 0;
        ++passed;
        if (!AlignmentMask(65536, alignmentMask) || alignmentMask != 65535) return 0;
        ++passed;
        if (AlignmentMask(3, alignmentMask) || AlignmentMask(65535, alignmentMask) || AlignmentMask(131072, alignmentMask)) return 0;
        ++passed;
        if (ownership.Acquire() != 0 || ownership.Acquire() != 1 || ownership.Acquire() != 2 || ownership.Acquire() != -1) return 0;
        ++passed;
        if (!ownership.Release(1) || ownership.Release(1) || ownership.Release(PoolSize) || ownership.Acquire() != 1) return 0;
        ++passed;
        if (!ownership.Release(0) || !ownership.Release(1) || !ownership.Release(2) || ownership.current != 0 || ownership.peak != 3 || ownership.returned != 4) return 0;
        ++passed;
        if (FrameTime(30) != 10000000 || FrameTime(1) != 333333 || FrameTime(3) - FrameTime(2) != 333334) return 0;
        ++passed;
        EncodeConfig configuration;
        if (ValidateConfiguration(configuration) || configuration.width != Width || configuration.height != Height ||
            configuration.frameRate != FrameRate || configuration.bitrate != Bitrate || configuration.chromaSiting != 0) return 0;
        ++passed;
        constexpr std::array<std::array<UINT,2>,6> sizes{{ {640,360}, {854,480}, {1280,720}, {1920,1080}, {2560,1440}, {3840,2160} }};
        for (const auto& size : sizes)
            for (const UINT rate : { 30u, 60u })
            {
                configuration.width = size[0]; configuration.height = size[1]; configuration.frameRate = rate;
                configuration.pixelAspectNumerator = size[1] == 480 ? 1280 : 1;
                configuration.pixelAspectDenominator = size[1] == 480 ? 1281 : 1;
                if (ValidateConfiguration(configuration)) return 0;
                ++passed;
            }
        configuration = {}; configuration.bitrate = MinimumBitrate;
        if (ValidateConfiguration(configuration)) return 0;
        configuration.bitrate = MaximumBitrate;
        if (ValidateConfiguration(configuration)) return 0;
        ++passed;
        configuration.bitrate = MinimumBitrate - 1;
        if (!ValidateConfiguration(configuration)) return 0;
        configuration.bitrate = MaximumBitrate + 1;
        if (!ValidateConfiguration(configuration)) return 0;
        ++passed;
        configuration = {}; configuration.frameRate = 0;
        if (!ValidateConfiguration(configuration)) return 0;
        configuration.frameRate = 59;
        if (!ValidateConfiguration(configuration)) return 0;
        ++passed;
        configuration = {}; configuration.width = 1919;
        if (!ValidateConfiguration(configuration)) return 0;
        configuration = {}; configuration.width = 854; configuration.height = 480;
        if (!ValidateConfiguration(configuration)) return 0;
        ++passed;
        configuration = {}; configuration.chromaSiting = MFVideoChromaSubsampling_MPEG2 | MFVideoChromaSubsampling_ProgressiveChroma;
        if (ValidateConfiguration(configuration)) return 0;
        configuration.chromaSiting = MFVideoChromaSubsampling_MPEG1;
        if (!ValidateConfiguration(configuration)) return 0;
        ++passed;
        if (FrameTime(60,60) != 10000000 || FrameTime(1,60) != 166666 ||
            FrameTime(3,60) - FrameTime(2,60) != 166667) return 0;
        return passed + 1;
    }
}
