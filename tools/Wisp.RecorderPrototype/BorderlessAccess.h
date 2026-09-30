#pragma once

namespace recorder::capture
{
    // Explicit permission-only child. Requires the fixed request line plus EOF;
    // never creates a capture item/session, device, window, or audio client.
    int RunBorderlessAccessStdio() noexcept;
    unsigned RunBorderlessAccessContracts() noexcept;
}
