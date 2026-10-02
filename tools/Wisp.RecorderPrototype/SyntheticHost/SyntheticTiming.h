#pragma once
#include <cstdint>
#include <string>

namespace recorder::synthetic_timing
{
    enum class Stage : unsigned
    {
        Tick, CaptureTarget, VideoPump, VideoSubmit, CaptureFill, ProcessAudio, InitializeSpool,
        Readiness, SaveBegin, AppendVideo, AppendAudio, SealVideo, SealAudio, Flush, Write, CreateFile, Retain, Count
    };
    void Initialize() noexcept;
    bool Enabled() noexcept;
    void Freeze() noexcept;
    // Read only after the host thread and its owned workers have joined.
    std::string Report();
    class Scope final
    {
    public:
        Scope(Stage stage, std::uint64_t bytes = 0, std::int64_t mediaTime100ns = -1) noexcept;
        ~Scope();
        Scope(const Scope&) = delete;
        Scope& operator=(const Scope&) = delete;
    private:
        Stage stage_;
        std::uint64_t bytes_, began_ = 0;
        std::int64_t mediaTime100ns_;
    };
}

#if defined(WISP_SYNTHETIC_HOST_TIMING)
#define WISP_FIXTURE_TIME(name, stage, bytes, mediaTime) \
    ::recorder::synthetic_timing::Scope name(::recorder::synthetic_timing::Stage::stage, bytes, mediaTime)
#define WISP_FIXTURE_TRACK_TIME(name, isVideo, videoStage, audioStage, bytes, mediaTime) \
    ::recorder::synthetic_timing::Scope name((isVideo) ? ::recorder::synthetic_timing::Stage::videoStage : \
        ::recorder::synthetic_timing::Stage::audioStage, bytes, mediaTime)
#define WISP_FIXTURE_FREEZE() ::recorder::synthetic_timing::Freeze()
#else
#define WISP_FIXTURE_TIME(name, stage, bytes, mediaTime) ((void)0)
#define WISP_FIXTURE_TRACK_TIME(name, isVideo, videoStage, audioStage, bytes, mediaTime) ((void)0)
#define WISP_FIXTURE_FREEZE() ((void)0)
#endif
