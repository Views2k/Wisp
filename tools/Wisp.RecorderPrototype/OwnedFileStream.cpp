#include "OwnedFileStream.h"
#include <cstring>

namespace recorder::exporting
{
    namespace
    {
        // Above the maximum supported five-minute encoded clip, while bounding
        // malformed seek/size requests from a failed mux operation.
        constexpr LONGLONG MaximumFileBytes = 16ll * 1024 * 1024 * 1024;
        HRESULT LastError() noexcept { return HRESULT_FROM_WIN32(GetLastError()); }
        bool Valid(HANDLE file) noexcept { return file && file != INVALID_HANDLE_VALUE; }
    }
    OwnedFileStream::OwnedFileStream(HANDLE ownedFile) noexcept : file_(ownedFile) {}
    OwnedFileStream::~OwnedFileStream()
    {
        // Checked finalization is explicit. Destruction never flushes a failed
        // operation or deletes its partial output.
        if (Valid(file_)) CloseHandle(file_);
    }
    HRESULT OwnedFileStream::FlushAndClose(ULONGLONG& finalBytes) noexcept
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!Valid(file_)) { finalBytes = finalBytes_; return closeResult_; }
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file_, &size)) closeResult_ = LastError();
        else if (size.QuadPart < 0 || size.QuadPart > MaximumFileBytes) closeResult_ = STG_E_MEDIUMFULL;
        else finalBytes_ = static_cast<ULONGLONG>(size.QuadPart);
        if (!FlushFileBuffers(file_) && SUCCEEDED(closeResult_)) closeResult_ = LastError();
        if (!CloseHandle(file_))
        {
            if (SUCCEEDED(closeResult_)) closeResult_ = LastError();
            // Retain ownership for destructor cleanup after failed CloseHandle.
        }
        else file_ = INVALID_HANDLE_VALUE;
        finalBytes = finalBytes_;
        return closeResult_;
    }
    HRESULT OwnedFileStream::Read(void* destination, ULONG bytes, ULONG* read)
    {
        if (read) *read = 0;
        if (bytes && !destination) return STG_E_INVALIDPOINTER;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!Valid(file_)) return STG_E_REVERTED;
        DWORD actual = 0;
        if (!ReadFile(file_, destination, bytes, &actual, nullptr)) return LastError();
        if (read) *read = actual;
        return actual == bytes ? S_OK : S_FALSE;
    }
    HRESULT OwnedFileStream::Write(const void* source, ULONG bytes, ULONG* written)
    {
        if (written) *written = 0;
        if (bytes && !source) return STG_E_INVALIDPOINTER;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!Valid(file_)) return STG_E_REVERTED;
        LARGE_INTEGER zero{}, position{};
        if (!SetFilePointerEx(file_, zero, &position, FILE_CURRENT)) return LastError();
        if (position.QuadPart < 0 || position.QuadPart > MaximumFileBytes - bytes) return STG_E_MEDIUMFULL;
        DWORD actual = 0;
        if (!WriteFile(file_, source, bytes, &actual, nullptr)) return LastError();
        if (written) *written = actual;
        return actual == bytes ? S_OK : STG_E_MEDIUMFULL;
    }
    HRESULT OwnedFileStream::Seek(LARGE_INTEGER move, DWORD origin, ULARGE_INTEGER* position)
    {
        if (origin > STREAM_SEEK_END) return STG_E_INVALIDFUNCTION;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!Valid(file_)) return STG_E_REVERTED;
        LARGE_INTEGER base{}, zero{};
        if (origin == STREAM_SEEK_CUR && !SetFilePointerEx(file_, zero, &base, FILE_CURRENT)) return LastError();
        if (origin == STREAM_SEEK_END && !GetFileSizeEx(file_, &base)) return LastError();
        if (base.QuadPart < 0 || base.QuadPart > MaximumFileBytes ||
            move.QuadPart < -base.QuadPart || move.QuadPart > MaximumFileBytes - base.QuadPart)
            return STG_E_INVALIDFUNCTION;
        LARGE_INTEGER target{}; target.QuadPart = base.QuadPart + move.QuadPart;
        if (!SetFilePointerEx(file_, target, nullptr, FILE_BEGIN)) return LastError();
        if (position) position->QuadPart = static_cast<ULONGLONG>(target.QuadPart);
        return S_OK;
    }
    HRESULT OwnedFileStream::SetSize(ULARGE_INTEGER size)
    {
        if (size.QuadPart > static_cast<ULONGLONG>(MaximumFileBytes)) return STG_E_MEDIUMFULL;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!Valid(file_)) return STG_E_REVERTED;
        FILE_END_OF_FILE_INFO end{}; end.EndOfFile.QuadPart = static_cast<LONGLONG>(size.QuadPart);
        return SetFileInformationByHandle(file_, FileEndOfFileInfo, &end, sizeof(end)) ? S_OK : LastError();
    }
    HRESULT OwnedFileStream::Commit(DWORD flags)
    {
        if (flags != STGC_DEFAULT) return STG_E_INVALIDFLAG;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!Valid(file_)) return STG_E_REVERTED;
        return FlushFileBuffers(file_) ? S_OK : LastError();
    }
    HRESULT OwnedFileStream::Stat(STATSTG* status, DWORD flags)
    {
        if (!status) return STG_E_INVALIDPOINTER;
        std::memset(status, 0, sizeof(*status));
        if (flags != STATFLAG_NONAME && flags != STATFLAG_DEFAULT) return STG_E_INVALIDFLAG;
        std::lock_guard<std::mutex> lock(mutex_);
        if (!Valid(file_)) return STG_E_REVERTED;
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file_, &size)) return LastError();
        status->type = STGTY_STREAM;
        status->cbSize.QuadPart = static_cast<ULONGLONG>(size.QuadPart);
        status->grfMode = STGM_READWRITE | STGM_SHARE_EXCLUSIVE;
        // No filename is exposed; a stream may return null for its name.
        return S_OK;
    }
    HRESULT OwnedFileStream::CopyTo(IStream*, ULARGE_INTEGER, ULARGE_INTEGER* read, ULARGE_INTEGER* written)
    { if (read) read->QuadPart = 0; if (written) written->QuadPart = 0; return E_NOTIMPL; }
    HRESULT OwnedFileStream::Revert() { return STG_E_INVALIDFUNCTION; }
    HRESULT OwnedFileStream::LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) { return STG_E_INVALIDFUNCTION; }
    HRESULT OwnedFileStream::UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) { return STG_E_INVALIDFUNCTION; }
    HRESULT OwnedFileStream::Clone(IStream** clone) { if (!clone) return E_POINTER; *clone = nullptr; return E_NOTIMPL; }
}
