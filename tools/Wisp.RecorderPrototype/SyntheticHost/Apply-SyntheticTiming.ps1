param(
    [Parameter(Mandatory = $true)][string]$NativeDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$native = [IO.Path]::GetFullPath($NativeDirectory)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$suffix = '\source\tools\Wisp.RecorderPrototype'
if (-not $native.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Extracted diagnostic source is required.' }
$buildRoot = $native.Substring(0, $native.Length - $suffix.Length)
if ($evidence -ne (Join-Path $buildRoot 'instrumentation') -or (Test-Path -LiteralPath $evidence)) { throw 'Fresh diagnostic instrumentation evidence is required.' }
foreach ($path in @($native, $buildRoot)) {
    $info = Get-Item -LiteralPath $path
    if (-not $info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Regular extracted source is required.' }
}
[void][IO.Directory]::CreateDirectory($evidence)
$original = Join-Path $evidence 'original'
[void][IO.Directory]::CreateDirectory($original)
$edits = [Collections.Generic.List[object]]::new()
function Replace-Exactly([ref]$Text, [string]$Before, [string]$After, [string]$Label) {
    $Before = $Before.Replace("`r`n", "`n"); $After = $After.Replace("`r`n", "`n")
    $anchorCount = 0; $position = 0
    while (($position = $Text.Value.IndexOf($Before, $position, [StringComparison]::Ordinal)) -ge 0) { $anchorCount++; $position += $Before.Length }
    if ($anchorCount -ne 1) { throw "Instrumentation anchor count failed: $Label" }
    $Text.Value = $Text.Value.Replace($Before, $After)
    $edits.Add([ordered]@{ file = $file; boundary = $Label; matches = $anchorCount; before = $Before; after = $After })
}
function Instrument-Method([ref]$Text, [string]$Signature, [string]$Stage, [string]$Label) {
    $before = $Signature + "`n        {"
    Replace-Exactly $Text $before ($before + "`n            WISP_FIXTURE_TIME(fixtureTiming, $Stage, 0, -1);") $Label
}
$diffs = [Collections.Generic.List[string]]::new()
foreach ($file in @('RecorderHost.cpp', 'EncodedSpool.cpp')) {
    $path = Join-Path $native $file
    $beforePath = Join-Path $original $file
    [IO.File]::Copy($path, $beforePath, $false)
    $raw = [IO.File]::ReadAllText($path)
    $crlf = $raw.Contains("`r`n")
    $text = $raw.Replace("`r`n", "`n")
    $firstInclude = if ($file -eq 'RecorderHost.cpp') { '#include "RecorderHost.h"' } else { '#include "EncodedSpool.h"' }
    Replace-Exactly ([ref]$text) $firstInclude ($firstInclude + "`n" + '#include "SyntheticTiming.h"') 'diagnostic-header'
    if ($file -eq 'RecorderHost.cpp') {
        Instrument-Method ([ref]$text) '        void MediaSession::CheckTarget()' 'CaptureTarget' 'capture-target'
        Instrument-Method ([ref]$text) '        void MediaSession::ProcessAudio(UINT maximumPackets)' 'ProcessAudio' 'process-audio'
        Instrument-Method ([ref]$text) '        void MediaSession::InitializeSpool()' 'InitializeSpool' 'initialize-spool'
        Instrument-Method ([ref]$text) '        void MediaSession::CheckReadiness()' 'Readiness' 'readiness'
        Instrument-Method ([ref]$text) '        bool MediaSession::Tick() noexcept' 'Tick' 'host-tick'
        Instrument-Method ([ref]$text) '        HRESULT MediaSession::Fill(UINT, ID3D11Texture2D* destination) noexcept' 'CaptureFill' 'capture-fill'
        Instrument-Method ([ref]$text) '        bool MediaSession::BeginSave(const protocol::Command& command) noexcept' 'SaveBegin' 'save-begin'
        $before = '            bool PumpVideo() noexcept { return config_.losslessVideo ? losslessVideo_.Pump() : video_.Pump(0); }'
        $after = '            bool PumpVideo() noexcept { WISP_FIXTURE_TIME(fixtureTiming, VideoPump, 0, -1); return config_.losslessVideo ? losslessVideo_.Pump() : video_.Pump(0); }'
        Replace-Exactly ([ref]$text) $before $after 'video-pump'
        $before = @'
                    const auto status = config_.losslessVideo ? losslessVideo_.TrySubmit(nextFrame_, pts, *this) :
                        video_.TrySubmit(nextFrame_, pts, *this);
'@
        $after = @'
                    encoder::SubmitResult status;
                    {
                        WISP_FIXTURE_TIME(fixtureVideoSubmitTiming, VideoSubmit, 0, pts);
                        status = config_.losslessVideo ? losslessVideo_.TrySubmit(nextFrame_, pts, *this) :
                            video_.TrySubmit(nextFrame_, pts, *this);
                    }
'@
        Replace-Exactly ([ref]$text) $before $after 'video-submit'
        $before = '            if (firstFailure_.recorded) return;'
        Replace-Exactly ([ref]$text) $before ($before + "`n            WISP_FIXTURE_FREEZE();") 'freeze-first-failure'
    } else {
        $before = '        void Flush(HANDLE handle) { Check(FlushFileBuffers(handle), "spool_flush_failed"); }'
        $after = '        void Flush(HANDLE handle) { WISP_FIXTURE_TIME(fixtureTiming, Flush, 0, -1); Check(FlushFileBuffers(handle), "spool_flush_failed"); }'
        Replace-Exactly ([ref]$text) $before $after 'flush-file-buffers'
        $before = "        void Write(HANDLE file, const void* data, DWORD bytes)`n        {"
        Replace-Exactly ([ref]$text) $before ($before + "`n            WISP_FIXTURE_TIME(fixtureTiming, Write, bytes, -1);") 'write-file'
        $sealOriginal = @'
        void Seal(const std::shared_ptr<File>& file)
        { if (file && !file->sealed) { Flush(file->handle.value); file->sealed = true; } }
'@
        $sealCurrent = @'
        void Seal(const std::shared_ptr<File>& file)
        {
            // Checked writes already publish the readable prefix; temporary GOPs need no durability barrier.
            if (file && !file->sealed) file->sealed = true;
        }
'@
        $sealOriginal = $sealOriginal.Replace("`r`n", "`n")
        $sealCurrent = $sealCurrent.Replace("`r`n", "`n")
        $hasOriginal = $text.Contains($sealOriginal); $hasCurrent = $text.Contains($sealCurrent)
        if ($hasOriginal -eq $hasCurrent) { throw 'Exactly one approved Seal implementation is required.' }
        $before = if ($hasOriginal) { $sealOriginal } else { $sealCurrent }
        $after = @'
        void Seal(const std::shared_ptr<File>& file)
        {
            if (file && !file->sealed)
            {
                WISP_FIXTURE_TRACK_TIME(fixtureTiming, file->track == Track::Video, SealVideo, SealAudio, file->committedBytes, file->first);
                Flush(file->handle.value); file->sealed = true;
            }
        }
'@
        if ($hasCurrent) { $after = $after.Replace('Flush(file->handle.value); file->sealed = true;', 'file->sealed = true;') }
        Replace-Exactly ([ref]$text) $before $after 'seal-track-file'
        $before = '            const bool video = track == Track::Video;'
        Replace-Exactly ([ref]$text) $before ($before + "`n            WISP_FIXTURE_TRACK_TIME(fixtureTiming, video, AppendVideo, AppendAudio, size, time);") 'append-track'
        $before = "        std::shared_ptr<File> CreateFile(State& state, Track track, bool configuration = false)`n        {"
        Replace-Exactly ([ref]$text) $before ($before + "`n            WISP_FIXTURE_TIME(fixtureTiming, CreateFile, 0, -1);") 'create-spool-file'
        $before = @'
    bool EncodedSpool::RetainInternal(MediaTime span, std::shared_ptr<const Snapshot>& output,
        const RetentionBudget* budget, AvailablePlan* available) noexcept
    {
'@
        Replace-Exactly ([ref]$text) $before ($before + "`n        WISP_FIXTURE_TIME(fixtureTiming, Retain, 0, -1);") 'retain-snapshot'
    }
    if ($crlf) { $text = $text.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    $diff = @(& git --no-pager diff --no-index --no-ext-diff --text -- $beforePath $path)
    if ($LASTEXITCODE -ne 1) { throw 'Instrumentation diff was not produced.' }
    foreach ($line in $diff) {
        if ($line.StartsWith('diff --git ')) { $diffs.Add("diff --git original/$file instrumented/$file") }
        elseif ($line.StartsWith('--- ')) { $diffs.Add("--- original/$file") }
        elseif ($line.StartsWith('+++ ')) { $diffs.Add("+++ instrumented/$file") }
        else { $diffs.Add($line) }
    }
}
[IO.File]::WriteAllLines((Join-Path $evidence 'instrumentation.diff'), $diffs, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $evidence 'exact-edits.json'), ($edits | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
[ordered]@{ files = 2; matchedBoundaries = $edits.Count; productionFilesChanged = $false; runtimeExecuted = $false } | ConvertTo-Json -Compress
