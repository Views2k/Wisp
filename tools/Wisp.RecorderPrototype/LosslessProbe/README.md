# Lossless recording feasibility probe

Status: root built and ran the final reviewed capability probe and corrected synthetic fixture successfully. The selected adapter advertises H.264/HEVC lossless and full chroma. The H.264 identity-GBR fixture independently decoded with **zero differences across all44,236,800 RGB samples**, including matching per-frame hashes. This proves exact preservation for the synthetic prepared-RGB input only. Wisp playback and product recording integration remain unverified. No product recording, player, HDR, audio, settings, spool or export behavior is changed.

## What this probes

The executable opens a Direct3D 11 NVENC session on one explicitly selected NVIDIA adapter and reads H.264/HEVC capabilities, input formats, full-chroma profiles and the P1 lossless preset. It never initializes an encode, allocates frame buffers, reads any screen/game pixels, records audio or writes media. A 15-second watchdog bounds driver calls and terminates with exit code 14 without writing from the watchdog thread. Forza and Wisp must remain closed; checks fail closed on process enumeration errors. Output contains only scalar capabilities and status codes, without device names or identifiers.

A successful result establishes advertised capabilities only. It does not establish pixel equality, valid output, preview support, gameplay performance or a shippable lossless mode. A preset may contain defaults that still need explicit full-chroma overrides; its readback is evidence, not a ready product configuration.

Root owns build and execution. Help does not initialize GPU/driver resources.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\LosslessProbe\Build-NvencCapabilities.ps1
& .\tools\Wisp.RecorderPrototype\LosslessProbe\bin\Wisp.NvencCapabilities.exe --help
```

After confirming Forza and Wisp are closed:

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\LosslessProbe\bin\Wisp.NvencCapabilities.exe --probe --adapter-index 0
```

## Required fidelity contract

The selected separate mode is **lossless encoding of the prepared full-color SDR video**. Wisp first applies the existing selected-resolution scaling and accepted HDR-to-SDR appearance mapping. Every decoded 8-bit R, G and B sample must then equal the corresponding prepared full-chroma RGB sample. Alpha is not recorded. Preparation is explicitly disclosed; lossless compression does not claim original-resolution, original-HDR or original-FP16 equality. Frame cadence follows the selected recording rate and does not promise recording every 240 Hz display presentation. Existing normal mode and saved quality settings stay unchanged.

The current converter's `CodeRgb()` shader function contains the accepted appearance policy. A candidate lossless path would preserve its full-color result before the `PSY`/`PSUV` matrix conversion and chroma subsampling. HDR can remain enabled because the same existing HDR-to-SDR preparation is retained. No NV12 step, hidden lossy fallback or new tone-mapping policy is permitted after the prepared RGB comparison boundary. Native HDR archival and new HDR mastering/export metadata are outside this task.

Native NVENC RGB inputs can involve color conversion. Advertised BGRA input alone therefore proves nothing about the above contract. A next synthetic experiment must compare decoded RGB bytes against the prepared RGB source, including gradients, saturated one-pixel edges and moving fine detail. If ordinary RGB input fails equality, an explicitly reversible plane arrangement/full-chroma identity representation must be verified; no assumption that YUV444 color conversion is reversible is permitted.

## Integration boundaries to resolve before product changes

1. Query advertised lossless/full-chroma support and the driver API version with this probe.
2. Only after capability evidence and review, encode a bounded synthetic prepared-RGB source with Lossless tuning, explicit full-chroma profile, no B pictures and bounded random-access interval. Decode it independently and compare every RGB sample. Retain timing, output size and per-frame result; never call a near-equal result lossless.
3. Verify the same stream through Wisp's actual preview/thumbnail path and MP4 writer. Microsoft's documented H.264 decoder supports Baseline/Main/High and 4:2:0, while NVIDIA's H.264 lossless configuration requires High 4:4:4 Predictive. This is a concrete compatibility gap, not permission to assume the current player works. Any bundled decoder or new container is a separate dependency/integration decision requiring review.
4. Replace bitrate-derived spool budgeting for this mode with an explicit byte budget, measured actual write rate, disk headroom checks and a truthful remaining-history indicator. Never silently lower quality to satisfy a byte limit. Define any duration limit clearly before enabling capture.
5. Verify export preserves the original lossless stream. Audio remains the existing separately encoded game-audio track; the label must say lossless **video**, not imply lossless audio.
6. Measure the real performance impact before claiming the user's under-5% target. A capability query or synthetic encoder timing cannot establish that.

Raw SDR RGB24 storage scale (not an estimate of compressed output): 1080p60 is 373.25 MB/s, about 112 GB for five minutes; 2160p60 is 1.493 GB/s, about 448 GB for five minutes. Lossless compression is content-dependent, so current 96 Mbps / 3.6 GB-per-five-minute assumptions are not safe. FP16 RGBA at 2160p60 would be 3.981 GB/s before compression. These figures exclude audio/container overhead and are not hard encoded-size ceilings.

The minimal plausible hardware implementation uses the installed NVIDIA driver API, without requiring OBS or installing a driver. Support on other GPUs must remain honestly unavailable until separately demonstrated. A cross-vendor software lossless codec would add a codec/decode dependency and substantial CPU/storage questions; it is not a silent fallback.

## Synthetic feasibility fixture and measured results

Root's scalar follow-up isolated the first failure: the checked lock API returned success, expected/output timestamp0 matched, the picture was IDR, frame index0 and QP0, with2,352,862 bytes. Only the fixture's extra `hwEncodeStatus == 0` assertion failed; the measured hardware value was2. NVIDIA's sample uses the lock function's `NVENCSTATUS` return, not a zero comparison on that separate hardware field. The corrected fixture retains the checked API return, timestamp/order/no-B checks, explicit lossless QP/profile configuration and byte-equality gate, and records every raw hardware status without assigning undocumented error meanings. This correction is confined to the diagnostic.

Root's third run completed in406 ms and produced15,096,410 compressed bytes for16 synthetic720p frames. All16 hardware status values were2. Independent software decode reported High4:4:4 Predictive, `gbrp`, full range, GBR matrix and BT.709 transfer/primaries. The CPU verifier found zero differing component samples, maximum error0 and matching hashes on all16 frames. Evidence: `work/lossless-rgb-03-encode.json`, `work/lossless-rgb-03-probe.json` and `work/lossless-rgb-03-verify.json`. The short fixture's deliberately fine/random content and CPU-fed upload make its byte rate and wall time unsuitable for a gameplay-performance or five-minute storage promise.

The raw stream's reported `r_frame_rate=120/1` is not its progressive picture cadence. A bounded CPU header trace found `num_units_in_tick=1000`, `time_scale=120000`, `fixed_frame_rate_flag=1`, `frame_mbs_only_flag=1` and no picture-structure SEI requirement. Those are120 clock ticks/sec and two ticks per progressive picture:60 pictures/sec. The demuxer exposes16 packets, each duration0.016667 sec, but no PTS/DTS; raw `avg_frame_rate=25/1` is also a demuxer default, not measured capture cadence. The fixture's NVENC timestamps0..15 are opaque client tags and are not serialized into elementary-stream container timestamps. Do not retune NVENC to30fps to compensate for the raw stream's tick-rate report.

`NvencRgbFixture.cpp` generates exactly 16 deterministic prepared-RGB frames at 1280x720 and 60 fps. The source includes dark/mid-tone gradients, saturated one-pixel edges, fine repeating details and deterministic motion. It is not screen or game capture and does not execute the product HDR shader. Root owns all execution with Forza and Wisp closed.

It uses one path: H.264 High 4:4:4 Predictive, lossless P1, QP0 constant-QP, transform bypass, explicit chroma format3, eight-bit samples and zero B pictures. RGB bytes are rearranged into planar G/B/R with identity matrix0 and full-range VUI. No RGB-to-YCbCr transform or chroma subsampling is performed by the fixture. Independent decode must establish whether the encoder actually preserves and signals this representation; it is not assumed. The accepted BT.709 prepared-SDR transfer/primaries remain the declared appearance boundary.

The headless encoder owns 16 input/output buffer pairs and releases them before its session/device. It uses CPU-filled driver input buffers for this small correctness check; timing does not represent a future GPU-resident implementation. It submits all16 frames, drains with EOS and checks output timestamps/order and absence of B pictures. A 15-second watchdog bounds initialization, driver calls, file writes and cleanup. Only a new absolute fixed-drive output directory is accepted, held non-reparse ancestors prevent path replacement, and `CREATE_NEW` prevents overwriting an existing file. It checks 512 MiB headroom beyond the128 MiB compressed-output cap. A failure can leave only its own bounded partial `fixture.h264`; no cleanup deletes user files.

The CPU-only `--verify` command reads a held regular RGB24 file of exactly44,236,800 bytes, regenerates every source frame and compares all44,236,800 component samples. It reports per-frame source/decoded SHA-256, differing samples and maximum byte error, without pixel dumps or file paths. It returns3 on unequal samples,1 on an invalid check and14 on its watchdog. Frame-count/order/dimension correctness must also be checked in the independent decoder metadata. Encode success alone reports `pixelEqualityVerified:false`; no command claims Wisp playback compatibility.

The existing developer machine has FFmpeg9.0.1 and ffprobe on PATH. They are an independent software-decode oracle only: neither is bundled by Wisp, installed by this work, or a proposed product dependency. Root must invoke every external decoder command with a bounded process watchdog and retain its diagnostics locally; avoid printing paths or unfiltered stderr. The encoder and verifier already have their own15-second watchdogs. The raw decoder output is capped to at most17 frames, so an unexpected extra frame causes verification failure rather than silent truncation to the expected16.

Commands below describe the root-owned sequence. `$fixtureDirectory` must be a fresh workspace directory; `$fixtureExecutable` is the newly built tool. Help and `--verify` use no GPU resources.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\LosslessProbe\Build-NvencRgbFixture.ps1
& $fixtureExecutable --encode $fixtureDirectory
```

Inside the root's bounded external-process launcher, inspect the same synthetic elementary stream, decode with FFmpeg's software H.264 decoder and preserve its identity RGB signaling:

```[WINDOWS POWERSHELL]
& ffprobe.exe -v error -f h264 -count_frames -select_streams v:0 -show_entries stream=codec_name,profile,width,height,pix_fmt,color_range,color_space,color_transfer,color_primaries,has_b_frames,nb_read_frames -of json (Join-Path $fixtureDirectory 'fixture.h264')
& ffmpeg.exe -hide_banner -loglevel error -nostdin -n -hwaccel none -threads 1 -f h264 -i (Join-Path $fixtureDirectory 'fixture.h264') -map 0:v:0 -an -sn -dn -frames:v 17 -fps_mode passthrough -pix_fmt rgb24 -f rawvideo (Join-Path $fixtureDirectory 'decoded.rgb')
& $fixtureExecutable --verify (Join-Path $fixtureDirectory 'decoded.rgb')
```

Require H.264 High4:4:4 Predictive,1280x720,16 decoded frames,zero B pictures,full-range GBR/matrix0 and the declared BT.709 transfer/primaries. Require exactly zero differing component samples and matching hashes for every frame. A second representation variant is justified only by a concrete mismatch or unsupported API result, not an unexplained retry.

### Timestamped MP4 for the Windows compatibility check

Root can remux the exact elementary stream into a new workspace MP4, under its15-second external-process watchdog, without invoking an encoder. Explicit input `-r60` assigns the known synthetic schedule, frame index/60 seconds; it does not claim timestamps were present in the raw file. This is justified by the fixture's configured frame cadence and verified SPS/packet durations. `-c:v copy` retains the lossless coded pictures; no RGB, YUV or HDR conversion is applied. Existing files must not be overwritten.

```[WINDOWS POWERSHELL]
& ffmpeg.exe -hide_banner -loglevel error -nostdin -n -fflags +genpts -r 60 -f h264 -i (Join-Path $fixtureDirectory 'fixture.h264') -map 0:v:0 -c:v copy -frames:v 16 -an -sn -dn -video_track_timescale 60000 -movflags +faststart -fs 134217728 -f mp4 (Join-Path $fixtureDirectory 'fixture.mp4')
& ffprobe.exe -v error -count_frames -select_streams v:0 -show_entries stream=codec_name,profile,width,height,pix_fmt,color_range,color_space,color_transfer,color_primaries,r_frame_rate,avg_frame_rate,time_base,duration,nb_read_frames:packet=pts,dts,duration -of json (Join-Path $fixtureDirectory 'fixture.mp4')
```

Before WPF use, require16 frames/packets, track time base1/60000, timestamps0,1000,...15000, duration1000 ticks per packet and total duration16/60 seconds (within container rounding). Require unchanged High4:4:4/GBR metadata. Independently decode this MP4 to a new RGB24 file and run `--verify` again to establish that remuxing retained every sample. The command is prepared, not yet executed by this agent. This MP4 is solely a diagnostic compatibility input; the production Baseline-only writer is unchanged.

Then test the same synthetic stream through the existing Wisp preview decoder and MP4 writer in isolation. Do not redesign playback merely because the encoder can create the stream. If the native decoder cannot handle full chroma, report that exact blocker and obtain review of a narrow decoder plan before adding any dependency. Synthetic throughput is not a gameplay performance result; five-minute storage sizing, restart behavior and the under-5% gameplay requirement remain separate later gates.

Current source confirms the compatibility boundary: `ClipsPage.xaml.cs` uses WPF `MediaElement`; `ClipThumbnail.cpp` uses the Windows Media Foundation H.264 source reader; `Mp4ClipWriter.cpp` explicitly accepts only the Baseline profile. The app project bundles neither FFmpeg/libav nor another full-chroma decoder. A direct lossless stream cannot be substituted into these paths without reviewed compatibility work. No lossy preview proxy or NV12 fallback is authorized by this fixture.

Root subsequently verified the remux:16 frames,60/1 frame rate,1/60000 track time base, sequential PTS/DTS0..15000 and exact RGB equality again. The current WPF decoder rejected that MP4 with COM failure HRESULT `0xC00D11B1`. An additive reviewed decoder/thumbnail path is therefore required before this mode can be exposed. This is an observed local compatibility failure, not a claim about every Windows codec installation.

## Narrow capture integration plan, no product changes authorized yet

**Next GPU gate.** The successful input was driver-owned CPU-filled planar YUV444. Direct3D11 registered planar texture support is not established by that result. Microsoft documents `DXGI_FORMAT_AYUV` as a packed4:4:4 resource with RGBA8 integer/UNORM SRV, RTV and UAV views; view channels map R=V, G=U, B=Y, A=alpha. NVIDIA's vendor support discussion records unsupported stacked planar resources and an older driver bug that treated AYUV as RGB. Do not assume either limitation is unchanged today, and do not submit an undocumented triple-height R8 texture as if it were supported.

Root's bounded capability-only run now confirms AYUV input for both H.264 and HEVC (`work/nvenc-ayuv-capabilities.json`). The prepared `--encode-gpu` experiment creates real1280x720 `DXGI_FORMAT_AYUV` GPU textures and uses an integer pixel shader to generate the same16 known patterns. For identity GBR, set Y=G, U=B, V=R, so the typed RGBA8_UINT view writes `(R,B,G,255)`. It registers/maps the actual texture as `NV_ENC_BUFFER_FORMAT_AYUV`, keeps the verified High444/QP0/bypass/full-range/matrix0 settings, then uses the existing independent CPU decoder and verifier to compare every RGB byte. No source pixel upload/readback, screen capture, CUDA bridge or tone-mapping change is involved. CPU pattern generation supplies reference hashes only; four scalar shader constants are the only uploaded data. Query failure, texture/view rejection, changed mapped format or unequal output ends this path with the specific evidence; no hidden RGB/NV12 fallback.

The GPU variant keeps the15-second process watchdog,128 MiB output cap and held new output directory. Each shader draw is unbound, followed by a D3D11 event query/Flush and a bounded5-second completion wait before NVENC registration/map. The query reads only completion status, never pixels. All16 textures remain owned until output completion, with mapped inputs unregistered before their textures are released. This deliberately serialized correctness synchronization cannot establish gameplay overhead or propose a production scheduling policy. The prepared source does not yet prove texture/view/driver interoperability and has not been executed by this agent.

Root owns the following build/run, then the same bounded software decode and `--verify` sequence above, using a new directory. The old `--encode` CPU-input mode remains a separate explicit diagnostic; failure in the GPU variant does not invoke it.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\LosslessProbe\Build-NvencRgbFixture.ps1
& $fixtureExecutable --encode-gpu $fixtureDirectory
```

**Appearance and encoder seam.** In `HdrFrameConverter.cpp`, retain `CodeRgb()` exactly, adding only an explicit full-color output target before `PSY`/`PSUV`. Define the prepared comparison boundary as selected-resolution, full-range eight-bit quantized RGB after the existing SDR/HDR mapping; pack those bytes into the verified GPU representation. Normal NV12 output remains unchanged. Add a separate direct-NVENC session implementing the same bounded submit/pump/drain/close responsibilities as `HardwareVideoSession`, selected explicitly by mode in `RecorderHost.cpp`. Keep the existing capture target/reconnect logic, CFR epoch/duration policy, audio timeline/AAC path and GOP-start save semantics. Do not change normal MFT settings, or use the CPU-filled feasibility input as a production fallback. An unsupported capture adapter or cross-adapter input must report unavailability, not introduce an unmeasured CPU copy.

**Storage boundaries already present.** `EncodedSpool` already charges actual compressed bytes, retires whole sealed GOPs under pressure and includes pinned snapshot bytes. Preserve those ownership and accounting rules. What must change for lossless is `BuildPolicy`'s bitrate-derived quota, the absence of an available-history status and the strict `Retain` rejection when requested history has been evicted. Provide a separate explicit byte budget, report actual retained seconds and return a deliberately shorter complete-GOP save with its actual duration when the byte budget limits history. Keep the normal-mode strict contract unchanged. Do not relabel a configured quota as a physical disk failure, promise five minutes from a bitrate estimate or silently reduce quality.

The budget must reserve capacity for the active GOP, one pinned snapshot, finalized staging and the library-publication copy; check free space on the actual buffer/library volumes. If the current GOP cannot fit, report that exact mode/resolution capacity boundary rather than silently dropping dependent pictures. Current limits include16 MiB per video packet,16 GiB per finalized clip and a64 GiB spool validator ceiling. A raw4K RGB frame is already24.9 MB before compression, so the16 MiB packet guard cannot simply be assumed suitable. Establish a bounded per-mode packet ceiling with high-entropy maximum-resolution evidence and thread it through packet queues, spool readers and mux validation; never globally remove or arbitrarily multiply the bounds. A five-minute lossless save must not be promised until byte limits and publication headroom can actually accommodate it.

**Protocol and stored metadata.** Add an explicit video mode to `ClipsSettings`, `ClipRecordingSpec`, native settings and the bounded managed/native config protocol. Missing mode in existing settings and saved entries means current standard recording; Quality100 must retain its current meaning. Version the native wire contract or negotiate the new required capability so an old helper cannot silently record standard video for a lossless request. Carry validated codec/profile/full-range/full-chroma/transfer metadata and actual retained duration/bytes through finalized results and library records. The player/thumbnail route must choose from that validated mode, with plain unsupported-format errors and Copy details. The quality slider applies only to standard recording; lossless has no misleading quality percentage. Audio remains separately labeled AAC.

**MP4 boundary.** The existing writer requires Baseline, limited range and BT.709 YCbCr. Add a separate precise accepted lossless format; do not delete those normal-mode checks. The native packet observer must preserve SPS/PPS and exact timestamp/keyframe information when adapting NVENC Annex-B output. First prove that the Windows MPEG4 sink can copy High444 identity-GBR packets without changing sample entries, `avcC`, color metadata or timing; external FFmpeg remux success does not prove that sink supports it. If the sink refuses or rewrites metadata, review one additive mux path alongside the already-required decoder dependency. Export remains a copy of the finished original lossless MP4, not a re-encode or an automatic second export.

**Order of work.** GPU identity-AYUV equality first; supported in-app decode/display color handling and thumbnail generation second; native mux round-trip third. Only then integrate the separate mode, actual-byte/history reporting and connected protocol/library tests. Follow with bounded maximum-resolution/storage/save/restart tests and the separately authorized real driving/performance matrix. The40+ million exact synthetic samples prove neither the unchanged HDR appearance policy nor under5% gameplay cost.

## Pinned header and license

`nvEncodeAPI.h` is NVIDIA's MIT-licensed API header, obtained from FFmpeg's separate public header mirror. This is one source header only; no FFmpeg binary, encoder runtime, installer or package is included.

- Mirror tag: `n13.0.19.0`
- Commit: `e844e5b26f46bb77479f063029595293aa8f812d`
- Source: <https://raw.githubusercontent.com/FFmpeg/nv-codec-headers/e844e5b26f46bb77479f063029595293aa8f812d/include/ffnvcodec/nvEncodeAPI.h>
- SHA-256: `4fe4094541ef0f8a13249d97a8692dc5f835a6e9dd42eeadb3e2f7321d54dc7e`
- NVIDIA copyright and complete MIT notice are retained verbatim at the top of the header. The build checks the hash and fails if it changes.

## Primary references

- [NVIDIA NVENC programming guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html): driver-provided API, D3D11 devices, capability queries, lossless tuning and input-format negotiation.
- [Microsoft H.264 decoder](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-decoder): documented profile/chroma restrictions.
- [FFmpeg color-space definitions](https://github.com/FFmpeg/FFmpeg/blob/master/libavutil/pixfmt.h): matrix0/`AVCOL_SPC_RGB` uses GBR coefficient ordering; independent decode still must prove signaling and sample preservation.
- [FFmpeg input frame-rate option](https://ffmpeg.org/ffmpeg.html): input `-r` generates constant-rate timestamps; [H.264 tick-rate documentation](https://ffmpeg.org/ffprobe-all.html) distinguishes VUI ticks from frame rate.
- [Microsoft DXGI AYUV format](https://learn.microsoft.com/en-us/windows/win32/api/dxgiformat/ne-dxgiformat-dxgi_format): supported typed views and channel mapping.
- [NVIDIA AYUV/planar DirectX discussion](https://forums.developer.nvidia.com/t/unexpected-color-space-conversion-with-h-264-ayuv-format-444-yuv/197901): historical driver conversion issue and planar-resource boundary; current-device behavior still requires the proposed exact-byte test.
- The pinned NVIDIA header documents H.264 lossless's transform-bypass, zero-QP, constant-QP and High 4:4:4 Predictive requirements and enumerates supported API input formats.
