# Wisp capture-only prototype

Standalone diagnostic, not installed or integrated with Wisp. It does not use or change the HUD renderer. No encoding, audio, scaling, disk frames, preview, texture readback, game-memory reads, injection, elevation, priority changes or desktop fallback are implemented. Default invocation only prints help.

The build uses the existing VS x64 C++ tools, C++17, static runtime and Windows SDK 10.0.26100.0. There are no package dependencies. Only root coordinates compilation and execution; source authors and reviewers must not start parallel builds or captures.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\Build-RecorderPrototype.ps1
& .\tools\Wisp.RecorderPrototype\bin\x64\Wisp.RecorderPrototype.exe --self-test
```

`--self-test` exercises the actual argument parser, size bounds and target-lifetime policy without WinRT initialization or graphics resources. `--probe` enumerates WGC metadata, hardware display adapters, indexed output dimensions/attachment/color capability and adapter-filtered NV12/H.264 hardware MFT candidates. It neither activates an encoder nor proves a functioning encode path. Probe output omits device symbolic links, window titles, display names, serials, desktop coordinates and process paths. Unknown display color capability is reported as null, never assumed to mean SDR.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\bin\x64\Wisp.RecorderPrototype.exe --probe
```

Own-window validation requires Forza to remain closed. It creates one nonactivating visible fixture window and captures only that window. It never requests foreground. The ordinary fixture should complete with exit 0 and frames > 0. Resize, minimize and close are expected invalidations with exit 2, after about one second; they must report respectively `size_changed`, `window_minimized`, and either `window_closed` or `capture_item_closed`. Unsupported WGC/rate-control/HDR configurations return exit 3 instead; those are unavailable tests, not passing captures. Use at least two seconds for transition scenarios.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\bin\x64\Wisp.RecorderPrototype.exe --fixture --seconds 3 --scenario normal
& .\tools\Wisp.RecorderPrototype\bin\x64\Wisp.RecorderPrototype.exe --fixture --seconds 3 --scenario resize
& .\tools\Wisp.RecorderPrototype\bin\x64\Wisp.RecorderPrototype.exe --fixture --seconds 3 --scenario minimize
& .\tools\Wisp.RecorderPrototype\bin\x64\Wisp.RecorderPrototype.exe --fixture --seconds 3 --scenario close
```

The separate `--capture` mode requires all of `--hwnd`, `--pid`, and `--creation-time` (unsigned UTC FILETIME ticks from `GetProcessTimes`) plus explicit live-test readiness. It accepts only a top-level window owned directly by `ForzaHorizon6.exe`, with the matching still-held process instance. Hosted-window variants fail closed. It never discovers or switches to another capture target. Duration is 1–60 seconds and frame-pool buffers 2–3. No ready-made game invocation is supplied because the actual target identity and user readiness must be established immediately before use.

The prototype captures the **native window size**, not 1080p: downscaling belongs to a later independently measured stage. It requests a minimum update interval of 333334 x 100 ns (approximately 30 fps), verifies both runtime metadata and `IGraphicsCaptureSession5`, and reads back the applied interval. If unavailable it refuses capture. This is a maximum requested delivery rate, not a promise of exact cadence. By default, the target monitor must report SDR/sRGB; HDR or unknown color capability is rejected. Monitor identity, color state, foreground, process lifetime, visibility, minimization, client size and device removal are rechecked at most every 100 ms. Frame content-size changes or capture-item closure also stop the trial. Two seconds without delivered frames invalidates the trial, including a genuinely static source. Forza starting during a fixture stops it. No automatic restart/recovery is attempted.

The OS capture border remains enabled. Removing it requires a separate permission/capability decision and is not attempted.

An explicit `--fixture-hdr-metadata` option is available only with `--fixture`. It uses a float16 RGBA pool for metadata-only lifecycle checks on an existing SDR or HDR PQ/BT.2020 output and cannot accept another window/process target. Separately, `--capture-hdr-metadata` is available only with `--capture`, with all three immutable target arguments and live-test readiness still required. Both options are boolean flags without values; they cannot be mixed. Ordinary fixture and game capture retain the SDR guard.

Either explicit experiment uses the same metadata-only callbacks and rechecks the accepted output color state during the trial. Results separately identify `fixtureHdrMetadata` and `captureHdrMetadata`, the float16 pool, and nominal pixel bytes (dimensions x 8 bytes x configured buffers), excluding row padding and hidden driver/OS allocations. No captured pixels are inspected or saved, so these experiments do not establish HDR color fidelity, HDR encoding, or gameplay performance. They change no display settings. Target identity, foreground, size, lifetime, device and other invalidation checks remain unchanged.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\bin\x64\Wisp.RecorderPrototype.exe --fixture --fixture-hdr-metadata --seconds 3 --scenario normal
```

Frame callbacks borrow one frame, read scalar metadata, explicitly close it, and retain no texture. Weak/shared state ownership keeps late callbacks safe. Shutdown first marks stopping, revokes events, closes the session and pool without holding callback locks, and waits up to two seconds for callbacks to drain. Windows API calls themselves remain subject to OS behavior; an external bounded runner should still supervise diagnostic execution.

Scalar results identify capture delivery, dimensions, configured pool size, interval application, adapter vendor/device/LUID and timestamp/arrival counters. The chosen adapter is the **display adapter**, not proof of the game's render adapter. `meanAcceptedFrameWork100ns` and its maximum measure successful frame handling through explicit frame release and counter acquisition, excluding callback entry/exit bookkeeping and other system work. They are not end-to-end capture cost or displayed FPS. Exit 0 proves only that this diagnostic completed; it does not establish acceptable gameplay/HUD performance.

Capture timestamps must be positive and strictly increasing. A bounded fixture exposed a compositor timestamp 1.9474 ms ahead of callback QPC, disproving this prototype's original one-sided arrival comparison. The cause of that lead remains unknown. The prototype now retains those original media timestamps and reports signed timestamp-minus-arrival minimum/maximum/last, plus positive-lead count/maximum; these are offsets, not latency. No offset sum or average is accumulated, and no tolerance, timestamp shift or clamping of accepted media timestamps is introduced. Zero-valued offset summaries with zero accepted frames are empty summaries, not measurements.

Follow-on hardware encoding, after the capture performance gate: enumerate `MFT_CATEGORY_VIDEO_ENCODER` using only `MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER` plus `MFT_ENUM_ADAPTER_LUID`; activate and verify the actual transform, `MF_TRANSFORM_ASYNC`, `MF_SA_D3D11_AWARE`, and compatible NV12/H.264 media types. Use the same D3D device manager and a separately verified GPU BGRA-to-NV12 processor, bounded owned surfaces and asynchronous transform events. Do not equate an enumeration result, the Microsoft software encoder CLSID, or `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS` with verified hardware-only recording.

Primary references:

- [Window-specific capture and minimum Windows version](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
- [Free-threaded frame pool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded?view=winrt-26100)
- [Frame lifetime, resizing and HDR](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [Capture update interval](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval?view=winrt-28000)
- [Capture-border permission requirements](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired?view=winrt-26100)
- [Hardware MFT enumeration with adapter filter](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mftenum2)
- [D3D-aware transforms](https://learn.microsoft.com/en-us/windows/win32/medfound/direct3d-aware-mfts)
- [Asynchronous transform lifecycle](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts)
- [The newer hardware-only sink-writer option requires Windows 11 25H2](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-readwrite-use-only-hardware-transforms)

Private diagnostic output includes firstFrameTimestamp100ns and lastFrameTimestamp100ns: the original positive WGC media timestamps in the QPC time domain converted to 100 ns units. A supervisor can compare these with its integer QPC phase boundaries using the measured QPC frequency. Keep absolute timing values in local evidence; public summaries use durations. These delimit delivered frames and do not establish presentation or recording quality.
