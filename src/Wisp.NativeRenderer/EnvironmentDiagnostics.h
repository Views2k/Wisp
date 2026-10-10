#pragma once
#include <dxgi1_6.h>
#include <shellscalingapi.h>

// Bounded background inventory. No device identity, EDID, paths, HWNDs or LUIDs
// leave this function; it neither creates a render device nor changes a display.
struct WispEnvironmentAdapter
{
    uint32_t vendorId, deviceId, driverMajor, driverMinor, driverBuild, driverRevision;
    uint32_t driverValid, dedicatedMemoryMiB, software;
    wchar_t model[128];
};
struct WispEnvironmentDisplay
{
    uint32_t adapterIndex, width, height, refreshMilliHz, scalePercent;
    uint32_t hdr, bitsPerColor;
};
struct WispEnvironmentSnapshot
{
    uint32_t size, status, reason, adapterCount, displayCount;
    WispEnvironmentAdapter adapters[4];
    WispEnvironmentDisplay displays[8];
};
static_assert(sizeof(WispEnvironmentAdapter) == 292, "Environment adapter ABI mismatch");
static_assert(sizeof(WispEnvironmentDisplay) == 28, "Environment display ABI mismatch");
static_assert(sizeof(WispEnvironmentSnapshot) == 1412, "Environment snapshot ABI mismatch");

WISP_API HRESULT __cdecl WispReadEnvironment(WispEnvironmentSnapshot* snapshot) noexcept
{
    if (!snapshot || snapshot->size != sizeof(WispEnvironmentSnapshot)) return E_INVALIDARG;
    *snapshot = {}; snapshot->size = sizeof(WispEnvironmentSnapshot);
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    const auto created = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(created)) return created;
    snapshot->status = 1;
    DISPLAYCONFIG_PATH_INFO paths[32]{};
    DISPLAYCONFIG_MODE_INFO modes[64]{};
    UINT pathCount = 32, modeCount = 64;
    const bool pathsAvailable = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths, &modeCount, modes, nullptr) == ERROR_SUCCESS;
    if (!pathsAvailable) pathCount = 0;
    using ReadScale = HRESULT(WINAPI*)(HMONITOR, DEVICE_SCALE_FACTOR*);
    const HMODULE scalingModule = LoadLibraryExW(L"shcore.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    const auto readScale = scalingModule ? reinterpret_cast<ReadScale>(GetProcAddress(scalingModule, "GetScaleFactorForMonitor")) : nullptr;
    for (UINT a = 0; a <= 4; ++a)
    {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        const auto adapterResult = factory->EnumAdapters1(a, &adapter);
        if (adapterResult == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(adapterResult) || a == 4)
        { snapshot->status = 2; snapshot->reason = 3; break; }
        DXGI_ADAPTER_DESC1 description{};
        if (FAILED(adapter->GetDesc1(&description)))
        { snapshot->status = 2; snapshot->reason = 3; continue; }
        const UINT adapterIndex = snapshot->adapterCount++;
        auto& entry = snapshot->adapters[adapterIndex];
        entry.vendorId = description.VendorId; entry.deviceId = description.DeviceId;
        entry.dedicatedMemoryMiB = static_cast<uint32_t>(std::min<SIZE_T>(description.DedicatedVideoMemory / (1024 * 1024), 1048576));
        entry.software = (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
        wcsncpy_s(entry.model, description.Description, _TRUNCATE);
        LARGE_INTEGER driver{};
        if (SUCCEEDED(adapter->CheckInterfaceSupport(__uuidof(IDXGIDevice), &driver)))
        {
            entry.driverValid = 1;
            entry.driverMajor = HIWORD(driver.HighPart); entry.driverMinor = LOWORD(driver.HighPart);
            entry.driverBuild = HIWORD(driver.LowPart); entry.driverRevision = LOWORD(driver.LowPart);
        }
        for (UINT o = 0; o <= 8; ++o)
        {
            Microsoft::WRL::ComPtr<IDXGIOutput> output;
            const auto outputResult = adapter->EnumOutputs(o, &output);
            if (outputResult == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(outputResult) || snapshot->displayCount == 8 || o == 8)
            { snapshot->status = 2; snapshot->reason = 4; break; }
            DXGI_OUTPUT_DESC descriptionOutput{};
            if (FAILED(output->GetDesc(&descriptionOutput)))
            { snapshot->status = 2; snapshot->reason = 4; continue; }
            if (!descriptionOutput.AttachedToDesktop) continue;
            DEVMODEW mode{}; mode.dmSize = sizeof(mode);
            if (!EnumDisplaySettingsW(descriptionOutput.DeviceName, ENUM_CURRENT_SETTINGS, &mode) || !mode.dmPelsWidth || !mode.dmPelsHeight)
            { snapshot->status = 2; snapshot->reason = 4; continue; }
            auto& display = snapshot->displays[snapshot->displayCount++];
            display.adapterIndex = adapterIndex; display.width = mode.dmPelsWidth; display.height = mode.dmPelsHeight;
            // Prefer the active rational path rate. EnumDisplaySettings is only integer Hz.
            if (mode.dmDisplayFrequency > 1) display.refreshMilliHz = mode.dmDisplayFrequency * 1000;
            DEVICE_SCALE_FACTOR scale{};
            if (readScale && SUCCEEDED(readScale(descriptionOutput.Monitor, &scale))) display.scalePercent = static_cast<uint32_t>(scale);
            Microsoft::WRL::ComPtr<IDXGIOutput6> output6;
            DXGI_OUTPUT_DESC1 color{};
            const bool colorAvailable = SUCCEEDED(output.As(&output6)) && SUCCEEDED(output6->GetDesc1(&color));
            if (colorAvailable) display.bitsPerColor = color.BitsPerColor;
            bool matched = false;
            for (UINT p = 0; p < pathCount; ++p)
            {
                const auto& path = paths[p];
                DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = sizeof(source); source.header.adapterId = path.sourceInfo.adapterId; source.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS || _wcsicmp(source.viewGdiDeviceName, descriptionOutput.DeviceName) != 0) continue;
                // A cloned source has no unique target HDR/rate; keep those facts unknown.
                if (matched) { display.hdr = 0; display.refreshMilliHz = 0; break; }
                matched = true;
                const auto rate = path.targetInfo.refreshRate;
                if (rate.Denominator) display.refreshMilliHz = static_cast<uint32_t>(static_cast<uint64_t>(rate.Numerator) * 1000 / rate.Denominator);
                DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO advanced{};
                advanced.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                advanced.header.size = sizeof(advanced); advanced.header.adapterId = path.targetInfo.adapterId; advanced.header.id = path.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(&advanced.header) == ERROR_SUCCESS)
                {
                    // Advanced color can also mean wide-gamut SDR. Confirm the HDR wire color space.
                    if (!advanced.advancedColorEnabled) display.hdr = 1;
                    else if (colorAvailable && color.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020) display.hdr = 2;
                }
            }
        }
    }
    if (scalingModule) FreeLibrary(scalingModule);
    if (!snapshot->adapterCount) { snapshot->status = 0; snapshot->reason = 1; }
    return S_OK;
}
