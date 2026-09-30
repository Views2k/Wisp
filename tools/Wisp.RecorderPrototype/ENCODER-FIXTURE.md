# Hardware encoder fixture

This is the next isolated feasibility stage. It is not included in the Wisp installer. The existing capture-only executable and its build remain separate so their saved measurements retain exact provenance.

The user accepts strictly less than 5% performance loss. That budget applies to the complete recorder against an unchanged Wisp-only baseline under matched conditions. The latest capture-only result was a 2.10% average-FPS reduction. It does not measure conversion, encoding, audio or buffering and does not certify the full recorder.

## Intended check

The fixture generates NV12 input for one explicitly selected, adapter-filtered hardware H.264 Media Foundation transform. It creates no window and captures neither gameplay nor the desktop. Activation must refuse while Forza is running. Default invocation and `--self-test` must not allocate GPU or encoder resources.

The initial configuration is 1920×1080 at 30 frames/s, with a bounded three-surface input pool. This is an engineering fixture, not a change to the requested product default of 1080p60. Input sample completion, output timestamps, encoded bytes and drain/shutdown behavior require verification. Hardware enumeration alone is insufficient.

No software encoder fallback is allowed. This fixture does not prove a live GPU conversion path, correct HDR tone mapping, decoded visual quality, sound capture, useful 60fps video or acceptable game performance. In particular, the game's HDR display metadata does not establish the transfer function of WGC's FP16 capture pixels.

## Build and execution

Root coordinates compilation and execution. Source authors and reviewers must not run parallel fixtures or gameplay checks.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\Build-EncoderFixture.ps1
& .\tools\Wisp.RecorderPrototype\bin\encoder\Wisp.EncoderFixture.exe --self-test
```

After source review and confirming Forza is closed, root can run the explicit fixture under an external process watchdog. The current verification is recorded below and in the workspace's `outputs/gameplay-clips-20260930/STATUS.json`.

```[WINDOWS POWERSHELL]
& .\tools\Wisp.RecorderPrototype\bin\encoder\Wisp.EncoderFixture.exe --encode-fixture --frames 60 --timeout-ms 10000
```

Keep generated diagnostics local. Do not print hardware symbolic links, process paths or private window identities. A successful fixture establishes only the selected driver/adapter/configuration exercised, not support for other vendors or the finished Clips feature.

## Verified fixture result

The reviewed build `cfe8b248265fcfe1026c87b018937a635f2eb56be172cbb3e14f2fe638609976` passed 22 CPU contracts and one externally bounded headless fixture. It submitted 60 samples and received 60 H.264 output samples, 668,300 compressed bytes, two clean points and a 39-byte sequence header. Timestamps matched; all 60 input samples returned, with peak ownership of two out of three surfaces. Drain, callback shutdown and process exit completed cleanly. The elapsed 353 ms reflects finite, demand-driven encoding; 30fps media timestamps do not establish sustained real-time capture.

The first attempt exposed an optional-attribute handling error before any frames were submitted. The driver returns `E_NOTIMPL` for `GetInputStreamAttributes`, which is documented as optional. Only that result now means no allocation hint. The controlled render-target texture default succeeded in the subsequent fixture; other failures remain fatal. Both attempts are retained under `outputs/gameplay-clips-20260930/encoder-fixture-*`.

Output was counted and hashed in memory, not decoded or exported. HDR conversion, recorded image quality, audio and full-recorder performance remain unverified.

## API contracts

- [Hardware transform requirements](https://learn.microsoft.com/en-us/windows/win32/medfound/hardware-mfts)
- [Asynchronous input, output, drain and shutdown](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts)
- [Tracked sample ownership and release notification](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-imftrackedsample-setallocator)
- [D3D-aware transforms](https://learn.microsoft.com/en-us/windows/win32/medfound/direct3d-aware-mfts)
- [Capture frame lifetime and HDR considerations](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
