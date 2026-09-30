#pragma once

namespace recorder::capture
{
    // Permission-only child. Requires a fixed request/check line plus EOF;
    // the check operation never requests access or opens a permission prompt.
    // never creates a capture item/session, device, window, or audio client.
    int RunBorderlessAccessStdio() noexcept;
    unsigned RunBorderlessAccessContracts() noexcept;
}
