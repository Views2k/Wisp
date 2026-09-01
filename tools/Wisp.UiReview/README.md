# Wisp UI review

Windows/.NET 8 WPF console harness; no extra NuGet dependencies. Build only after production edits have finished. This project references the real `Wisp.App` project but is not part of the application or installer.

From the Wisp checkout:

```[WINDOWS POWERSHELL]
dotnet build .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --disable-build-servers -m:1 --nologo
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\public-polish-01
```

The output directory must be new or empty and inside this checkout; paths through junctions/symbolic links are rejected. Existing captures are never overwritten. The default review produces 23 PNGs plus `review.json`:

- Native Digital: all five pages at 980x750, 720x440, 1280x900, and 2560x1440 device-independent pixels, at 96 DPI (20 PNGs).
- Native Analogue, Native EV Digital, and Combined: Appearance at 980x750 and 144 DPI (three PNGs).

At 144 DPI the baseline PNG is 1470x1125 pixels. These are actual root-DPI layouts, not resized screenshots.

Before each bounded layout pass, the harness updates root DPI and invalidates measurement throughout the visual tree, including collapsed steps and generated control-template labels. This mirrors [WPF's window-DPI invalidation path](https://raw.githubusercontent.com/dotnet/wpf/v8.0.0/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndTarget.cs): setting root DPI flags alone can leave cached text measurements at the previous DPI. It does not change text, padding, fonts, or overflow tolerances.

`RenderTargetBitmap` uses WPF's software path, which does not support the native PS 3.0 shaders. White input rectangles in those captures are not GPU-rendered gauge output. The matrix remains useful for layout, resources, and bindings; it cannot establish shader or native visual parity.

Options:

- `--fixture native-digital`, `native-analogue`, `native-ev-digital`, `native-ev-analogue`, `minimal`, `combined`, or `separate-boxes` selects just one fixture and omits the default supplements. There is no unbounded/all-fixture mode.
- `--scope matrix` (default) captures all five pages at four viewport sizes;
  `appearance` captures only Appearance at the baseline size. `wizard` uses the
  separate setup matrix below.
- `--telemetry sample` (default) feeds a deterministic synthetic vehicle directly into `DiagnosticsViewModel.Update`; `waiting` leaves real telemetry unavailable and exercises the production `NativePreviewFrame` offline sample. Waiting mode intentionally has no active EV classification.
- `--dpi 96` (default) or `144` controls the primary fixture. The default three supplementary captures always use 144 DPI.

## Bounded native lifetime check

The separate opt-in `--native-lifetime-check` accepts only `--output`. It shows four real native controls in an independent nonactivating, input-blocked host and automatically checks visible → minimized → restored → collapsed → visible → closed. It normally takes about four seconds; an 8-second close timer and 10-second own-process watchdog bound it. No controller, settings service, provider, UDP, game access, screenshot, or input injection is used. `review.json` records actual visibility, render-subscription state, retained frames, hidden digit mutations, live callback advances, EV gear history on resume, and zero blur on resume. This checks lifecycle, not GPU/game performance. A hard watchdog termination may leave no final report. Other modes are unchanged.

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\native-lifetime-01 --native-lifetime-check
```

## Loaded shell and theme check

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --calm-shell-check --output .\work\ui-review\calm-shell-01
```

This bounded mode loads the real application resources and validates the
sidebar, compact shell, all 15 accent palettes, all 15 background palettes, and
their independent settings persistence. It writes three PNGs and `review.json`,
uses isolated settings, opens no listener or game process, and closes
automatically. The report records geometry, resource identity, contrast, and
binding findings. Native HUD resources are checked to ensure application themes
do not replace their authored colors.

## Bounded scroll mechanism check

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\scroll-check-01 --scroll-check
```

Without `--present`, this explicit mode writes `review.json` without PNGs or a
visible window. It uses one fixture (Native Digital by default), the four
scroll-mechanism pages (Dashboard, Appearance, Diagnostics, and Setup), and
720x440/980x750 DIP at the selected `--dpi`. Extras is validated by the default
matrix and the loaded-shell check rather than this four-page A/B comparison.
`--fixture` and `--telemetry waiting` remain available. `--scope` and `--step`
are rejected; the existing default capture/presentation behavior is unchanged.

Each case compares direct `ScrollViewer > Viewbox` content with `ScrollViewer > Decorator > Viewbox`, temporarily reparenting the same existing controls and restoring the original tree afterward. An already-decorated production tree is also accepted, and its original content type is reported. A/B order alternates by case. Each scrollable variant receives 16 warm-up steps and 120 measured steps, with a 30-second cooperative budget for the eight comparisons. Tabs without overflow are explicitly marked non-scrollable, not reported as successful scrolling samples.

The `scrollCheck` report counts actual offset changes and internal Viewbox `ContainerVisual.Transform` reference replacements. Timings and same-thread allocations cover only `ScrollToVerticalOffset` plus synchronous `UpdateLayout`, excluding metadata construction. Top/middle/bottom anchors compare offsets, extents, viewport sizes, scale, and transformed Viewbox/content bounds; drift checks verify that content translates by exactly the scroll offset throughout the measured steps. Geometry, offset, incomplete-matrix, and binding findings return exit code `2`.

This is a mechanism check, **not a GPU or frame-rate benchmark**: no bitmap rendering, compositor presentation, live telemetry cadence, or external input is timed. Fixed synthetic/offline data isolate layout and transform churn. Small noisy CPU timing differences must not be treated as proof of user-visible lag or its removal. The same source-window-handle/presentation-source isolation checks, dormant controller, temporary settings ownership, input blocking, and no-startup guarantees apply. As with the existing review budget, a hung synchronous WPF call cannot be forcibly interrupted.

For an explicitly authorized visible A/B run:

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\scroll-present-01 --scroll-check --present
```

This shows only an independent, fixed-size host titled **Wisp UI review - synthetic scroll A/B**, without requesting activation. It automatically scrolls the actual Appearance surface at monitor DPI: direct Viewbox first, then a temporary plain Decorator; each phase has one warm-up second and six measured seconds, capped at 4,096 samples. An already-wrapped production tree is temporarily unwrapped only inside the detached diagnostic tree, and restored afterward. There is no OS input injection or production window `Show()`; keys, text, mouse actions, and production-content hit testing remain blocked. Escape and ordinary host close still work.

The new `scrollCheck.presentation` metadata records monotonic `Stopwatch` intervals at `CompositionTarget.Rendering`, deduplicating identical rendering timestamps. It includes per-phase >33 ms/>50 ms gap counts, median/p95/maximum intervals, actual/requested offsets, transform reference replacements, scroll/layout time and allocations, and extent/scale stability. Raw bounded samples retain exact offset progress and relative rendering timestamps. Renderer capability and host render-mode metadata remain in the normal report. These are **compositor callback intervals, not GPU present/completion timestamps**, and fixed synthetic data do not reproduce live telemetry load. No screenshot is captured; use separately authorized OS capture for visual evidence.

The A/B sequence normally closes itself after about 14 seconds. A 20-second dispatcher timer closes a slow/incomplete presentation; an independent 30-second watchdog terminates only the current review process with exit code `124` if synchronous WPF work prevents closure. A hard watchdog termination may leave no final `review.json`. `--dpi` is rejected for visible presentation. No visible run occurs unless both `--scroll-check` and `--present` are explicitly supplied; normal `--present` still uses its existing display-only 120-second behavior.

## Wizard review

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\wizard-layout-01 --scope wizard
```

Wizard scope captures all four `SetupWindow` steps at 800x730, 540x440, 840x760, and the default launch size of 900x780 DIP, at both 96 and 144 DPI: 32 PNGs plus `review.json`. `--dpi` restricts this scope to one DPI. `--step welcome|connection|display|appearance` restricts it to one step. The default preference fixture is Native Digital; an explicit `--fixture` selects other saved preferences, not live vehicle data. Wizard previews remain the production illustrative samples. `--telemetry` is rejected in wizard scope.

Only the tool uses reflection to set the existing private `_step` and call `UpdateStep`. It never invokes Test, Next, or Finish; confirmations stay unchecked, successful-test evidence remains absent, and the setup gate remains closed. This inspects otherwise unreachable later-step layout without adding a production bypass. Each offscreen capture checks that the source window has no handle and the detached surface has no presentation source. Settings use the existing isolated temporary-directory cleanup.

The optional display-only wizard presentation shows one step at monitor DPI:

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\wizard-present-01 --scope wizard --present --step display
```

It uses the same independent, input-blocked host and 120-second auto-close as the main-window presentation. No production wizard window is shown, no wizard action can be clicked, and no test or completion is performed. `--dpi` is rejected with `--present`; omitted `--step` presents Welcome.

Each wizard capture includes `wizard` metadata: effective foreground colors for named headings and large labels, estimated contrast, unexpected clipping, footer-button bounds, and unconfirmed setup/test state. Black foregrounds, low contrast, missing or clipped required footer controls, and unexpected clipping produce findings. Contrast estimates use solid ancestor/window backgrounds; gradients, ambient effects, and image pixels are not sampled, so the report identifies its estimation basis. Vertical content below a scroll viewport is reported separately from unexpected clipping; horizontal clipping still produces a finding. Existing binding, label-overflow, logo, resource-hash, and visual-tree checks also apply. A wizard without a diagnostics data context has `previewState: null`; the harness does not inject a view model to hide binding errors.

The ambient background is captured in its current static offscreen state. This does not validate animation, native shader output, production window chrome, or game visibility.

One additional offline sample capture, using another output directory:

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\public-polish-offline-01 --fixture native-digital --scope appearance --telemetry waiting --dpi 144
```

For an explicitly requested GPU presentation, select exactly one fixture:

```[WINDOWS POWERSHELL]
dotnet run --project .\tools\Wisp.UiReview\Wisp.UiReview.csproj -c Release --no-build --no-restore --no-launch-profile -- --output .\work\ui-review\public-polish-present-01 --present --fixture native-digital
```

For the main-window scope, `--present` shows only an independent host titled **Wisp UI review - synthetic preview**, initially 980x750 DIP on Appearance. It uses monitor DPI (so `--dpi` and explicit main-window `--scope` are rejected), does not activate itself, and blocks interaction with the production content. Its ordinary host frame can be moved/resized/closed; Escape also closes it. A 120-second dispatcher timer closes the host automatically. Like the matrix budget, that timer cannot interrupt a hung synchronous WPF call. `--telemetry waiting` is also supported outside wizard scope.

Presentation preserves the source window's fonts, foreground, background, data context, name scope, and resources. It does not show the production `MainWindow`, start the controller, or capture the screen. Use a separately authorized OS capture tool for visual evidence. `review.json` is written after the host closes, with renderer tier, WPF-reported PS3 hardware/software support, render-mode preferences, first-render layout metadata, and close timing. Its `captures` array is empty; `presentation.surface.image` is null. Capability flags do not prove that every shader ran on the GPU or establish game parity, especially if the render tier changes later.

`review.json` records separate SHA-256 hashes for the reviewed Wisp assembly and `App.xaml` resource source, the `HeaderLogo` resource hash/decoded dimensions and bounds, root DPI, named control bounds, scroll extents, and preview controls' local and transformed sizes. Preview center offsets are relative to `HudPreviewSurface`; collapsed branches are excluded. All bounds and offsets are in device-independent pixels. Center offsets are measurements, not automatic design approval; visible drawing bounds are not an alpha/ink bounding box. The preview-state record separately reports real-frame and preview-frame availability, proving that the waiting fixture did not fill real telemetry with sample values.

Label diagnostics compare visible plain-text labels against their allocated sizes using their actual font, DPI, wrapping, formatting mode, and line height, with 1.5 DIP rounding tolerance. Mixed/styled inline labels are counted as skipped; they still require visual inspection. Text itself is not written to the report. This is a useful overflow check, not a proof of every glyph or deliberately scroll-clipped paragraph; scroll extents explain content legitimately below the captured viewport.

Binding diagnostics retain only categories, numeric codes, and property identifiers; raw values, exception text, private paths, and URLs are not emitted. Metadata is capped at 4,096 visited visual nodes, 96 named elements, 64 label-overflow/local-binding records per capture, and 128 binding messages per run. The matrix has a three-minute cooperative cancellation budget, with bounded dispatcher drains; a synchronous WPF layout/render call cannot be forcibly interrupted by this budget.

Isolation:

- An STA subclass of plain `Application` suppresses startup/exit. It never constructs `Wisp.App.App` or calls its `InitializeComponent()` across an assembly boundary. It reads only the exact `src/Wisp.App/App.xaml` `Application.Resources` subtree, preserves namespace declarations, and loads it as a `ResourceDictionary` with a Wisp pack base URI. XML DTDs/external entities are disabled; the source is capped at 1 MiB. Shared styles are not copied to a second maintained file.
- `Application` can queue startup even without `Run()`, so the plain-Application no-op startup override is still required. The real compiled `MainWindow` and native controls continue to load their BAML and packed assets normally. Rebuild after production edits; the two recorded hashes distinguish compiled UI from the source-loaded shared resources.
- No `Application.Run`, input simulation, display screenshot, `AppController.StartAsync`, listener restart, compatibility check, or controller telemetry processing is invoked. Only explicit `--present` calls `Show()` on the independent display-only host; the default matrix never shows a window.
- Only `MainWindow.Content`, or `SetupWindow.Content` in wizard scope, is detached and reviewed; the Wisp source window handle must remain zero. Its actual data context, name scope, and window resources are retained for the detached tree. Neither mode proves production window-chrome, focus, hover, or animation parity.
- Settings are synthetic, with `StartWithWindows=false`, and use a unique temporary subdirectory within the explicit output directory. The controller is disposed; only its two known temporary settings filenames are removed. Unexpected extra files are not deleted recursively.
- The controller constructs its normal dormant native-reader worker and may read the local compatibility catalog, but no telemetry is sent to that worker and no process handles or UDP sockets are opened. Native textures/renderers are not changed. Synthetic frames are not evidence of live FH6 offsets or motion-blur behavior.

Exit codes: `0` completed without logo/binding/text-overflow findings; `2` completed with findings or truncated visual inspection; `1` capture failure (partial output is retained); `64` invalid arguments/output. No captures have been generated merely by adding these harness sources.
