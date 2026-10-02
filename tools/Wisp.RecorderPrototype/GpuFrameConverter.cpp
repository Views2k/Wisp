#include "GpuFrameConverter.h"

#include <dxgi1_2.h>
#include <cmath>
#include <new>

namespace recorder::conversion
{
    using Microsoft::WRL::ComPtr;
    namespace
    {
        struct Failure { const char* reason; HRESULT hr; };
        void Check(HRESULT hr, const char* reason)
        {
            if (FAILED(hr)) throw Failure{ reason, hr };
        }
        void Require(bool value, const char* reason)
        {
            if (!value) throw Failure{ reason, E_FAIL };
        }
        bool SameIdentity(IUnknown* left, IUnknown* right)
        {
            ComPtr<IUnknown> leftIdentity, rightIdentity;
            Check(left->QueryInterface(IID_PPV_ARGS(&leftIdentity)), "resource_identity_query_failed");
            Check(right->QueryInterface(IID_PPV_ARGS(&rightIdentity)), "resource_identity_query_failed");
            return leftIdentity.Get() == rightIdentity.Get();
        }
    }

    const char* ValidateSource(UINT width, UINT height, SourceEncoding encoding) noexcept
    {
        return ValidateSource(width, height, encoding, {});
    }

    const char* ValidateSource(UINT width, UINT height, SourceEncoding encoding,
        const OutputConfiguration& output) noexcept
    {
        if (encoding == SourceEncoding::LinearScRgbFp16) return "hdr_tone_mapping_not_implemented";
        if (encoding != SourceEncoding::SdrBgraG22P709) return "source_color_encoding_unknown";
        if (const char* reason = ValidateSourceGeometry(width, height)) return reason;
        return ValidateOutputConfiguration(output);
    }

    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height) noexcept
    {
        return ValidateSurfaces(input, output, width, height, {});
    }

    const char* ValidateSurfaces(const D3D11_TEXTURE2D_DESC& input,
        const D3D11_TEXTURE2D_DESC& output, UINT width, UINT height,
        const OutputConfiguration& configuration) noexcept
    {
        if (const char* reason = ValidateOutputConfiguration(configuration)) return reason;
        if (input.Format != DXGI_FORMAT_B8G8R8A8_UNORM || input.Width != width || input.Height != height ||
            input.MipLevels != 1 || input.ArraySize != 1 || input.SampleDesc.Count != 1 || input.SampleDesc.Quality != 0 ||
            input.Usage != D3D11_USAGE_DEFAULT || input.CPUAccessFlags != 0)
            return "source_surface_incompatible";
        if (output.Format != DXGI_FORMAT_NV12 || output.Width != configuration.width || output.Height != configuration.height ||
            output.MipLevels != 1 || output.ArraySize != 1 || output.SampleDesc.Count != 1 || output.SampleDesc.Quality != 0 ||
            output.Usage != D3D11_USAGE_DEFAULT || output.CPUAccessFlags != 0 ||
            !(output.BindFlags & D3D11_BIND_RENDER_TARGET))
            return "destination_surface_incompatible";
        return nullptr;
    }

    bool GpuFrameConverter::Initialize(ID3D11Device* device, UINT sourceWidth, UINT sourceHeight,
        SourceEncoding encoding, ConversionEvidence& evidence) noexcept
    {
        return Initialize(device, sourceWidth, sourceHeight, encoding, {}, evidence);
    }

    bool GpuFrameConverter::Initialize(ID3D11Device* device, UINT sourceWidth, UINT sourceHeight,
        SourceEncoding encoding, const OutputConfiguration& output, ConversionEvidence& evidence) noexcept
    {
        try
        {
            if (const char* reason = ValidateSource(sourceWidth, sourceHeight, encoding, output))
                throw Failure{ reason, E_INVALIDARG };
            Require(device != nullptr && !device_, "invalid_converter_initialization");
            ComPtr<IDXGIDevice> dxgiDevice;
            Check(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)), "device_dxgi_query_failed");
            ComPtr<IDXGIAdapter> adapter;
            Check(dxgiDevice->GetAdapter(&adapter), "device_adapter_query_failed");
            ComPtr<IDXGIAdapter1> adapter1;
            Check(adapter.As(&adapter1), "device_adapter_query_failed");
            DXGI_ADAPTER_DESC1 description{};
            Check(adapter1->GetDesc1(&description), "adapter_description_failed");
            Require(!(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE), "software_adapter_refused");
            Require(device->GetFeatureLevel() >= D3D_FEATURE_LEVEL_11_0, "feature_level_11_required");
            device_ = device;
            sourceWidth_ = sourceWidth;
            sourceHeight_ = sourceHeight;
            output_ = output;
            evidence.output = output_;
            Check(device_.As(&videoDevice_), "video_device_unavailable");
            ComPtr<ID3D11DeviceContext> context;
            device_->GetImmediateContext(&context);
            Check(context.As(&videoContext_), "video_context1_unavailable");
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
            content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            content.InputFrameRate = { output_.frameRate, 1 };
            content.InputWidth = sourceWidth;
            content.InputHeight = sourceHeight;
            content.OutputFrameRate = { output_.frameRate, 1 };
            content.OutputWidth = output_.width;
            content.OutputHeight = output_.height;
            content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            Check(videoDevice_->CreateVideoProcessorEnumerator(&content, &enumerator_), "video_processor_enumeration_failed");
            ComPtr<ID3D11VideoProcessorEnumerator1> enumerator1;
            Check(enumerator_.As(&enumerator1), "video_processor_enumerator1_unavailable");
            BOOL supported = FALSE;
            Check(enumerator1->CheckVideoProcessorFormatConversion(DXGI_FORMAT_B8G8R8A8_UNORM, InputColor,
                DXGI_FORMAT_NV12, OutputColor, &supported), "format_conversion_query_failed");
            Require(supported != FALSE, "sdr_bgra_to_nv12_unsupported");
            evidence.formatConversionSupported = true;
            Check(videoDevice_->CreateVideoProcessor(enumerator_.Get(), 0, &processor_), "video_processor_creation_failed");
            videoContext_->VideoProcessorSetStreamFrameFormat(processor_.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
            videoContext_->VideoProcessorSetStreamAutoProcessingMode(processor_.Get(), 0, FALSE);
            videoContext_->VideoProcessorSetStreamColorSpace1(processor_.Get(), 0, InputColor);
            videoContext_->VideoProcessorSetOutputColorSpace1(processor_.Get(), OutputColor);
            videoContext_->VideoProcessorSetStreamOutputRate(processor_.Get(), 0, D3D11_VIDEO_PROCESSOR_OUTPUT_RATE_NORMAL, FALSE, nullptr);
            videoContext_->VideoProcessorSetStreamAlpha(processor_.Get(), 0, TRUE, 1.0f);
            videoContext_->VideoProcessorSetOutputAlphaFillMode(processor_.Get(), D3D11_VIDEO_PROCESSOR_ALPHA_FILL_MODE_OPAQUE, 0);
            const RECT sourceRect{ 0, 0, static_cast<LONG>(sourceWidth), static_cast<LONG>(sourceHeight) };
            const auto fit = FitSourceToOutput(sourceWidth, sourceHeight, output_);
            // The video processor accepts integer rectangles; round the shared
            // fit to its nearest pixel boundary, with at most one pixel error.
            RECT destinationRect{ static_cast<LONG>(std::floor(fit.left + 0.5f)),
                static_cast<LONG>(std::floor(fit.top + 0.5f)),
                static_cast<LONG>(std::floor(fit.left + fit.width + 0.5f)),
                static_cast<LONG>(std::floor(fit.top + fit.height + 0.5f)) };
            if (destinationRect.right == destinationRect.left) ++destinationRect.right;
            if (destinationRect.bottom == destinationRect.top) ++destinationRect.bottom;
            const RECT targetRect{ 0, 0, static_cast<LONG>(output_.width), static_cast<LONG>(output_.height) };
            D3D11_VIDEO_COLOR background{};
            background.RGBA.A = 1.0f;
            videoContext_->VideoProcessorSetOutputBackgroundColor(processor_.Get(), FALSE, &background);
            videoContext_->VideoProcessorSetStreamSourceRect(processor_.Get(), 0, TRUE, &sourceRect);
            videoContext_->VideoProcessorSetStreamDestRect(processor_.Get(), 0, TRUE, &destinationRect);
            videoContext_->VideoProcessorSetOutputTargetRect(processor_.Get(), TRUE, &targetRect);
            DXGI_COLOR_SPACE_TYPE observedInput = DXGI_COLOR_SPACE_CUSTOM, observedOutput = DXGI_COLOR_SPACE_CUSTOM;
            videoContext_->VideoProcessorGetStreamColorSpace1(processor_.Get(), 0, &observedInput);
            videoContext_->VideoProcessorGetOutputColorSpace1(processor_.Get(), &observedOutput);
            Require(observedInput == InputColor && observedOutput == OutputColor, "color_state_readback_mismatch");
            evidence.colorStateVerified = true;
            BOOL automatic = TRUE;
            videoContext_->VideoProcessorGetStreamAutoProcessingMode(processor_.Get(), 0, &automatic);
            Require(automatic == FALSE, "automatic_processing_still_enabled");
            evidence.autoProcessingDisabled = true;
            evidence.reason = "sdr_converter_initialized";
            return true;
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }
        processor_.Reset(); enumerator_.Reset(); videoContext_.Reset(); videoDevice_.Reset(); device_.Reset();
        return false;
    }

    bool GpuFrameConverter::Submit(ID3D11Texture2D* source, ID3D11Texture2D* destination,
        UINT frameIndex, ConversionEvidence& evidence) noexcept
    {
        try
        {
            Require(source && destination && processor_, "invalid_conversion_arguments");
            Require(!SameIdentity(source, destination), "aliased_conversion_surfaces");
            ComPtr<ID3D11Device> inputDevice, outputDevice;
            source->GetDevice(&inputDevice);
            destination->GetDevice(&outputDevice);
            Require(inputDevice && outputDevice && SameIdentity(inputDevice.Get(), device_.Get()) &&
                SameIdentity(outputDevice.Get(), device_.Get()), "cross_device_conversion_refused");
            D3D11_TEXTURE2D_DESC inputDescription{}, outputDescription{};
            source->GetDesc(&inputDescription);
            destination->GetDesc(&outputDescription);
            if (const char* reason = ValidateSurfaces(inputDescription, outputDescription, sourceWidth_, sourceHeight_, output_))
                throw Failure{ reason, E_INVALIDARG };
            Check(device_->GetDeviceRemovedReason(), "d3d11_device_removed");
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputViewDescription{};
            inputViewDescription.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11VideoProcessorInputView> inputView;
            Check(videoDevice_->CreateVideoProcessorInputView(source, enumerator_.Get(), &inputViewDescription, &inputView),
                "video_processor_input_view_failed");
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDescription{};
            outputViewDescription.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11VideoProcessorOutputView> outputView;
            Check(videoDevice_->CreateVideoProcessorOutputView(destination, enumerator_.Get(), &outputViewDescription, &outputView),
                "video_processor_output_view_failed");
            D3D11_VIDEO_PROCESSOR_STREAM stream{};
            stream.Enable = TRUE;
            stream.OutputIndex = 0;
            stream.InputFrameOrField = frameIndex;
            stream.pInputSurface = inputView.Get();
            Check(videoContext_->VideoProcessorBlt(processor_.Get(), outputView.Get(), frameIndex, 1, &stream), "video_processor_blt_failed");
            ++evidence.submittedBlits;
            evidence.reason = "sdr_conversion_submitted";
            return true;
        }
        catch (const Failure& failure) { evidence.reason = failure.reason; evidence.hr = failure.hr; }
        catch (const std::bad_alloc&) { evidence.reason = "allocation_failed"; evidence.hr = E_OUTOFMEMORY; }
        catch (...) { evidence.reason = "unexpected_native_failure"; evidence.hr = E_FAIL; }
        return false;
    }
}
