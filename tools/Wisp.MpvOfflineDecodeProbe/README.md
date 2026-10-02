# Headless libmpv lossless playback proof

Diagnostic only. Root owns restore/build/run. No product playback, capture, settings,
source media, export or installed files are changed. No window, GPU output,
hardware decoder, capture API or audible output is used: `vo=null`, `ao=null`,
`hwdec=no`. AAC still decodes into libmpv's null audio clock sink.

## Exact inputs

- Official upstream development artifact:
  `libmpv-v0.41.0-dev-ga1f50f2c3-36640285359-x86_64-w64-mingw32-lgpl.zip`.
  Archive 56,076,756 bytes, SHA256
  `7e349cc9589b7ced492ed0207c24808f7c328a0ffa96cdae6a069861a1a856a4`.
- Extracted `libmpv-2.dll`: 126,284,288 bytes, SHA256
  `1E23E98611C99137565B684D7D59B5BF30DC3B81F3C8E606DF6D503F0BD6B8A3`.
- Exact archive `include/mpv/client.h`: 86,136 bytes, API 2.5, SHA256
  `1ACF99EE77C8C2A6F1D1993BD81BBC8A91D27FB5924E80171670E6139A4BD353`.
  The small managed interop declarations follow this x64 C ABI; no wrapper package.
- Only the previously reported 965,569,830-byte MP4 is accepted, pinned SHA256
  `48A33FB214FE9F4D4802FAF88E71E61280C2C8CC680F2DF4A0135A1416806ABD`.
- Existing independently decoded 148-frame RGB24 oracle is read and pinned at
  `work/clips-short-save-20261001/ffmpeg-rgb.sha256`, SHA256
  `CE20DFAC0739354A4992D5977120E4C5E241C649BF1F9DA3A754A85E3F4DF3A2`.

Source/runtime/oracle files and their non-reparse parents are held against
replacement. Output must be a fresh directory below the checkout's `work`.
LoadLibraryEx searches only the exact pinned DLL directory and System32. The
dependency directory must contain only one top-level DLL. Runtime version queries
emit bounded identifiers; no native log text, paths or frame pixels are printed.

## Sequence and limits

1. Disable config, scripts, external media discovery, network references, disk
   cache, terminal/input bindings, OSD and position persistence before opening.
2. Set pause before loading. Require 3840x2160, decoded `gbrp`, expected duration,
   null outputs and first decoded frame; hold paused at zero for two seconds.
3. Play through the original. Pause for raw unscaled samples near 0.7 and 1.25 s,
   then continue to EOF. Initial frame is sampled too.
4. Exact paused seek to 0.7 s with a fourth sample; seek zero and replay to EOF.
5. Complete native teardown, then convert only the four retained BGR0 samples to
   RGB24 and compare their hashes with the original FFmpeg oracle.

`screenshot-sw=yes` forces the generic current-decoded-frame path even with null
VO. No image is converted or hashed on every decoded frame. At most four
33,177,600-byte sample copies are retained plus one reusable 24,883,200-byte RGB
buffer after teardown. Screenshots remain in memory; output contains scalar JSON
and hashes only. Repeated original frames retain every matching oracle index;
pixel equality does not itself identify a unique presentation timestamp.
`framedrop=no` is a diagnostic correctness setting, not a proposed
shipping performance policy.

Forward compressed packet budget is 256 MiB; back cache and separate decoded
frame queue are disabled. The diagnostic rejects observed forward cache above
384 MiB (allowing one bounded packet and accounting overhead), private process
memory above 3 GiB, missing required queue/drop metrics, any native error log,
decoder error, queue overflow or dropped-frame count. Memory and closed-app
guards run at most every 250 ms; queue/clock observations at most every 100 ms.
Soft deadline 30 seconds; independent 45-second process watchdog performs no
logging before termination. Native destruction remains inside that hard bound.

## Root-only build/run

From the app checkout, using a new output name for each accepted run:

```[WINDOWS POWERSHELL]
dotnet build tools/Wisp.MpvOfflineDecodeProbe/Wisp.MpvOfflineDecodeProbe.csproj -c Release
$probeSource = Join-Path $env:LOCALAPPDATA 'Wisp/Clips/29e58a5054f547e7b7fc7535c16252da.mp4'
$probeDependency = Join-Path $PWD 'work/clips-short-save-20261001/mpv-dependency/verified'
$probeOutput = Join-Path $PWD 'work/clips-short-save-20261001/mpv-headless-01'
& tools/Wisp.MpvOfflineDecodeProbe/bin/Release/net8.0-windows/Wisp.MpvOfflineDecodeProbe.exe --source $probeSource --dependency $probeDependency --output $probeOutput
```

## Source-backed rationale and remaining distribution work

[Official build listing](https://github.com/mpv-player/mpv/releases/tag/git-release)
labels this variant LGPLv3, built with `-Dgpl=false` and LGPL FFmpeg. It is an
upstream development build, not a stable release. The release identifies mpv
commit `a1f50f2c38206dc943f331cf5a5b02f97a0ce219` and CI run `36640285359`.

Reviewed upstream queue source: `demux/demux.c:read_packet` stops prefetch at its
byte limit, without discarding unread reference packets. If another stream is
empty at the limit, it signals temporary EOF for that stream; this needs the
actual A/V test. `filters/f_decoder_wrapper.c:lavc_process` puts an unconsumed
packet back on EAGAIN. `video/out/vo.c:vo_get_current_frame` retains the current
decoded frame; `player/screenshot.c:screenshot_get` uses it for software captures.
See [demux source](https://github.com/mpv-player/mpv/blob/master/demux/demux.c),
[decoder wrapper](https://github.com/mpv-player/mpv/blob/master/filters/f_decoder_wrapper.c),
[screenshot source](https://github.com/mpv-player/mpv/blob/master/player/screenshot.c).
These source observations must be matched to the candidate's source archive
before any shipping claim; exact header/runtime identity is already pinned here.

Public CI recipe uses a mutable BtbN container tag and shallow FFmpeg/LuaJIT
clones. A shipping dependency manifest still needs resolved image digest, exact
linked revisions/configurations, corresponding source/notices and DLL
replacement terms for the full static library closure. No license-completeness
claim is made by this probe.

A pass establishes sampled pixel correctness plus the reported headless
decoder/clock/control/cleanup behavior on this clip. It does not prove equality
of every decoded frame, actual HWND presentation, audible A/V synchronization,
long-session reliability, 60 displayed fps or under-5% game performance. It does
not recompress a proxy or alter the original/export path.
