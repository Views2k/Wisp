# Isolated lossless playback feasibility probe

This project is outside the product and solution. It tests official `LibVLCSharp 3.10.1` plus `VideoLAN.LibVLC.Windows 3.0.24`; there is no WPF VideoView package, system install, product dependency or product license change. Root owns restore, build and execution. No capture or game memory read is performed.

The only accepted media is the existing generated 16-frame, 1280x720, 60 fps, silent fixture from `../Wisp.RecorderPrototype/LosslessProbe/NvencRgbFixture.cpp`. Its MP4 is 15,097,409 bytes with SHA-256 `584257587584AC7A35485C58BA00EBE5595B6A4B53F0CF3F9FF2FC1D7BA47F16`. Root independently verified all 44,236,800 decoded RGB samples before this probe. Do not point the probe at a user recording or change its hash to admit one.

## Check performed

1. Require Wisp and Forza closed and a nonzero foreground window. Pin the source read-only and its non-reparse directory ancestry; require a new output directory under this checkout's `work` directory.
2. Load native libraries from the tool's app-local x64 output. The project copies DLLs and plugins only, excluding Lua/HTTP pages, headers/import libraries and other architectures. Inspect actual restored/build output before execution; this is not yet a redistribution approval.
3. Show a muted, nonactivating, noninteractive child HWND with a verified native viewport region. There is no separate overlay window and no call to focus or activate anything. Audio decoding is disabled at engine creation, volume is zero and mouse/key handling is disabled.
4. Open using LibVLC's `:start-paused`, observe Paused, then issue one paused seek to time0 to prepare its preview frame. Require a video output, 1280x720 and the initial frame time, then hold that position for two seconds. The short fixture must not finish or advance without the explicit transport step.
5. Request one original-size synthetic PNG snapshot while paused and compare all 2,764,800 RGB component samples against the generator's exact frame-zero values. The PNG reader ignores color profiles and only unpacks/reorders channels. There is no gamma adjustment, channel guessing, rescaling, tolerance or color heuristic. Unequal samples fail and report differing count, maximum byte error and hashes.
6. Only after equality passes, explicitly resume and await the short clip's end. Dispose the player, media, engine and child window. Native callbacks only update bounded scalars; they never call back into LibVLC, write logs or touch WPF. A 16-second soft deadline and unconditional 20-second process watchdog cover preparation and cleanup.

The report distinguishes exact decoded snapshot values from display appearance. A PNG snapshot does **not** prove the compositor's displayed colors, successful display of every frame, large-file buffering, A/V sync, performance, or a production integration. The synthetic clip has no audio and lasts less than one second. LibVLC's buffer percentage describes its cache target, never the entire file.

Outputs are `libvlc-probe.json` and `first-frame.png` inside the fresh output directory. The PNG contains only the known generated pattern, never desktop/game pixels. The JSON contains scalar state and hashes, no file paths, native logs, account details or exception messages. Existing files are not overwritten or deleted. Exit0 requires all checks; exit2 is a bounded failure; watchdog exit124 may leave no final report.

After the first runtime reached watchdog exit124 without a final report, two bounded diagnostic files were added before another run. `stages.jsonl` flushes a whitelisted checkpoint before and after native initialization, media attachment, Play, snapshot, Stop and disposal boundaries. `watchdog-state.jsonl` independently samples only managed stage/event counters and the name of the current native getter every250ms; it makes no LibVLC or WPF calls. Both are create-new, limited to512KiB, and contain no paths, exception messages, raw native logs or pixels. The unconditional20-second watchdog does no logging or lock acquisition. For exit124, the last complete sampler line is its last-known context (up to250ms old if storage was responding), not a guaranteed final watchdog callback record. A logging failure must not suppress cleanup. The original deadlines, media state predicates and exact RGB comparison are unchanged.

Instrumented run02 reached the existing soft deadline waiting for the native clip region, with all media event counts zero and clean disposal. The host had subscribed only to `LayoutUpdated`, which can occur before `IsLoaded` and need not recur after `Loaded`. The next source explicitly applies the same verified region from the host and viewport `Loaded` events as well as later layout updates. Scalar loaded/handle/event-count/region state is included in checkpoints and the final report; no handle value is recorded. This is a harness lifecycle correction before media opening, not evidence about VLC codec support or color fidelity.

Run03 established the viewport and reached a paused input but remained at25% buffering without a video output. Official VLC3 source explains why a blind `NextFrame()` is unsuitable here: `EsOutFrameNext` returns while buffering. The next sequence waits for Paused and uses one normal seek to time0. `INPUT_CONTROL_SET_TIME` resets decoder state; `input_DecoderFlush` grants one frame while paused. Input seeks are processed during buffering after a bounded postponement. This does not resume playback and does not waive any output/readiness or exact frame-zero comparison. Its runtime result still must be measured. Primary source: [frame-next buffering guard](https://github.com/videolan/vlc-3.0/blob/master/src/input/es_out.c), [paused decoder flush](https://github.com/videolan/vlc-3.0/blob/master/src/input/decoder.c), [input seek processing](https://github.com/videolan/vlc-3.0/blob/master/src/input/input.c).

## Parent-owned commands

Run from the checkout with the pinned .NET SDK used by the other tools. The first restore produces the actual project-local `packages.lock.json`; inspect and retain it, then require locked mode. No fabricated lock or package content hashes are checked in before restore.

```[WINDOWS POWERSHELL]
dotnet restore .\tools\Wisp.LosslessPlaybackProbe\Wisp.LosslessPlaybackProbe.csproj --use-lock-file -p:Platform=x64
dotnet restore .\tools\Wisp.LosslessPlaybackProbe\Wisp.LosslessPlaybackProbe.csproj --locked-mode -p:Platform=x64
dotnet build .\tools\Wisp.LosslessPlaybackProbe\Wisp.LosslessPlaybackProbe.csproj -c Release --no-restore -p:Platform=x64 -m:1
```

The native package's actual build output must have only `libvlc/win-x64` and no `lua` directory. Package authenticity, native plugin licensing and source availability are reviewed separately. Native/managed runtime hashes are recorded by the probe; they should match the reviewed package outputs.

Once both apps are closed, keep the current app focused during this synthetic-only window check:

```[WINDOWS POWERSHELL]
$probeProject = Join-Path $PWD 'tools\Wisp.LosslessPlaybackProbe'
$probeExe = Join-Path $probeProject 'bin\x64\Release\net8.0-windows\win-x64\Wisp.LosslessPlaybackProbe.exe'
$probeSource = Join-Path $PWD 'work\lossless-rgb-03\fixture.mp4'
$probeOutput = Join-Path $PWD 'work\libvlc-lossless-01'
& $probeExe --source $probeSource --output $probeOutput
```

If MSBuild uses a different output layout, root must inspect it and use the exact resulting EXE, retaining the app-local native directory beside it. No runtime auto-download or fallback to a system VLC installation is supported.

## Primary API references

- [Official managed package](https://www.nuget.org/packages/LibVLCSharp/3.10.1) and [official native package](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.24).
- [LibVLCSharp MediaPlayer](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp/Shared/MediaPlayer.cs): native HWND, snapshot, muted playback, events and synchronous Stop semantics.
- [LibVLCSharp best practices](https://github.com/videolan/libvlcsharp/blob/3.x/docs/best_practices.md): shared engine lifetime and avoiding native callback reentrancy.
- [VLC 3 input state](https://github.com/videolan/vlc-3.0/blob/master/src/input/input.c): start-paused behavior.
- Both packages are LGPL-2.1-or-later; the managed package is not MIT. This isolated experiment neither licenses product redistribution nor changes Wisp's proprietary license. Shipping needs the separately reviewed source/notices and replaceable-library arrangements.
