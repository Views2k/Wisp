#pragma once

#include <windows.h>
#include <objidl.h>
#include <wrl/implements.h>
#include <mutex>

namespace recorder::exporting
{
    // Adopts only an already-created recorder file handle. No path reopen,
    // rename or deletion. MF may call this stream from its own worker.
    class OwnedFileStream final : public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IStream>
    {
    public:
        explicit OwnedFileStream(HANDLE ownedFile) noexcept;
        ~OwnedFileStream();
        HRESULT FlushAndClose(ULONGLONG& finalBytes) noexcept;
        IFACEMETHOD(Read)(void*, ULONG, ULONG*) override;
        IFACEMETHOD(Write)(const void*, ULONG, ULONG*) override;
        IFACEMETHOD(Seek)(LARGE_INTEGER, DWORD, ULARGE_INTEGER*) override;
        IFACEMETHOD(SetSize)(ULARGE_INTEGER) override;
        IFACEMETHOD(CopyTo)(IStream*, ULARGE_INTEGER, ULARGE_INTEGER*, ULARGE_INTEGER*) override;
        IFACEMETHOD(Commit)(DWORD) override;
        IFACEMETHOD(Revert)() override;
        IFACEMETHOD(LockRegion)(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override;
        IFACEMETHOD(UnlockRegion)(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override;
        IFACEMETHOD(Stat)(STATSTG*, DWORD) override;
        IFACEMETHOD(Clone)(IStream**) override;
    private:
        std::mutex mutex_;
        HANDLE file_ = INVALID_HANDLE_VALUE;
        HRESULT closeResult_ = S_OK;
        ULONGLONG finalBytes_ = 0;
    };
}
