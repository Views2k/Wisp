#pragma once
#include <dxgi1_5.h>

namespace WispTearingPolicy
{
    inline constexpr UINT SwapChainFlags(bool cpuRendering, HRESULT featureResult, bool supported) noexcept
    {
        return DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT |
            (!cpuRendering && featureResult == S_OK && supported ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0u);
    }

    inline constexpr UINT PresentFlags(UINT swapChainFlags, UINT syncInterval) noexcept
    {
        return DXGI_PRESENT_DO_NOT_WAIT |
            (syncInterval == 0 && (swapChainFlags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING)
                ? DXGI_PRESENT_ALLOW_TEARING : 0u);
    }
}
