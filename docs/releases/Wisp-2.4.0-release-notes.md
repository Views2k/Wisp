# Wisp 2.4

Wisp 2.4 is a performance hotfix that fixes the G-SYNC/VRR needle issue and improves rendering efficiency. It improves how Wisp delivers the overlay to Windows, separates needle motion from ordinary HUD drawing and UI work, and fixes unnecessary wakeups, allocations and rendering-state transitions found during the investigation.

The symptom was unusually misleading: the game could remain smooth while Wisp's needle looked as though it was running at a lower frame rate. Switching away from Forza, or disabling G-SYNC, made the needle smoother immediately. That made the stock tachometer in the same focused game the most useful comparison.

## What changed

### An overlay window designed for variable refresh rate

When the main HUD fits entirely on one monitor, its native presentation window now covers that monitor exactly. The artwork stays at the chosen size and position inside it, using HUD-sized drawing surfaces. Moving the HUD updates its visual offset without resetting the needle animation or rebuilding the presentation surface unnecessarily.

Wisp also requests **passive updates** on its native presentation windows and the shared WPF overlay windows. This tells Windows to update those windows when desktop composition is already running for other reasons. The request is applied when the window is configured, and its result is included in debug exports.

These changes came from reviewing how transparent overlays coexist with variable refresh rate, including window bounds, flip presentation and hardware overlay availability. Thanks to **[fredemmott](https://github.com/OpenKneeboard/OpenKneeboard/issues/677#issuecomment-3250237599)** for sharing guidance from Microsoft's Direct3D team in the OpenKneeboard investigation. His explanation gave us a concrete basis for these changes and a much better direction for the diagnosis.

### Needle motion gets its own update path

On the hardware rendering path, the tachometer needle is a separate compositor visual. Wisp supplies a short movement curve, and Windows applies the rotation as it composes the overlay. The dial, needle and foreground retain their intended layer order, including needle blur and the hub and readings above it.

Needle motion now has a dedicated worker and an independent DirectComposition device. Drawing the rest of the HUD and waiting to present its pixels can therefore proceed separately from publishing the next needle movement.

RPM data also reaches that worker directly from the telemetry receiver through a small, bounded history. Previously, RPM-driven motion had to wait for the UI thread to publish a HUD snapshot. That added avoidable delay when the UI was busy. The UI still supplies the validated layout and scale, while fresh RPM readings can advance independently.

The direct path keeps native-angle input priority and handles car changes, RPM-scale changes, session resets and stale input. Brief race-state transitions retain the existing visibility hysteresis. Each consumer has its own position in the history so one worker cannot consume another worker's updates.

### Less delay and less repeated work

Live RPM playback now starts with a **20 ms minimum buffer**, with adaptive protection up to 75 ms when the input needs more buffering. Needle curves use accepted observations and stop when the input becomes stale.

The audit also produced several smaller fixes that matter together:

- **Consume waiting input before advancing playback.** After a render wait, queued needle history is read before playback moves forward. This avoids processing fresh observations against a playback clock that has already advanced past them.
- **Fix repeated wakeups from tiny waits.** A positive wait shorter than a millisecond is rounded up instead of becoming a zero-length poll. This removes unnecessary wakeups and gauge refresh attempts near timing boundaries.
- **Reuse rendering storage and unchanged needle pixels.** Workers reuse their command buffers and append layer commands directly. The compositor reuses the needle's bitmap while its angle changes, reducing allocations and repeated content preparation.
- **Balance frame-readiness waits.** Startup, swap-chain replacement and recovery from an occluded window now acquire and release readiness consistently, including the initial transparent frame.
- **Handle hidden windows and shutdown cleanly.** Hidden or minimized HUDs stop their rendering work. Shutdown releases compositor resources before destroying their window and keeps cleanup off the UI thread.
- **Correct supplementary-gauge timing.** Queued boost, tire-temperature, power/torque and G-force samples use their actual consumption time, preserving playback timing after a delayed update.
- **Reduce diagnostic overhead.** Memory-health sampling uses the direct process working-set reading through `Environment.WorkingSet`.
- **Use the appropriate composition opacity mode.** A single already-composed surface uses multiply opacity; the split needle layers use grouped opacity so overlapping artwork blends correctly.
- **Make native failures explicit.** Renderer status and diagnostics report initialization and runtime failures directly, making the active rendering path clear when troubleshooting.

Hardware rendering also requests a higher GPU scheduling class and device priority, recording the request and readback results. HUD presentation uses a timer paced to the monitor's configured refresh rate with interval-zero presentation, and refreshes that configuration when the display changes.

### Better evidence when something goes wrong

Debug exports identify the exact build and rendering path and include more detail about needle input, authored motion, presentation waits and passive-update requests. This makes it easier to distinguish old input, a delayed worker, a rejected native operation and a presentation problem.

The added timing details help follow accepted input through the needle worker and into Windows composition, alongside the visible in-game result.

## Why the earlier CPU-rendering fix was only part of the answer

The earlier [tachometer investigation in #30](https://github.com/Views2k/Wisp/issues/30) already included G-SYNC as a possible factor. It found real timing and drawing problems, and the CPU-rendering option helped the people who reported back. That was a useful result and remains part of the history of this fix.

CPU rendering changes where Wisp rasterizes its pixels. Windows still composes the overlay afterward. We [noted that distinction in the earlier issue](https://github.com/Views2k/Wisp/issues/30#issuecomment-5617134893), but the available evidence had not isolated which part of that delivery path caused the remaining focused-game symptom.

Our earlier diagnostics concentrated on telemetry reception, CPU drawing and update submission. Those stages could look healthy while the needle still looked uneven. We therefore expanded the investigation to the actual overlay window and composition path, and used the stock needle in the same focused scene as the visual reference. Capture-based experiments that changed the behavior being investigated were set aside.

The later investigation separated four parts of the problem: receiving needle data, publishing it from the UI, drawing the HUD, and handing its windows and motion to Windows composition. That exposed an avoidable UI dependency and led to the independent needle path. The external-overlay guidance then provided a concrete reason to test monitor-sized hosting and passive updates. Those changes address a different boundary from the earlier CPU-rendering option.

For other overlay developers, the useful lesson is to check the actual window and presentation setup alongside drawing cost. Microsoft's [flip-model guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model) explains how overlapping desktop content can affect composition and hardware overlay paths. Its [DirectComposition documentation](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts#cross-device-visual-trees) also describes how independently updated visuals can share a composition tree.

## If you still see a difference

1. **Check the version and restart after renderer changes.** Confirm Wisp 2.4 is installed. Changes to the CPU-rendering option take effect after exiting Wisp from the tray and reopening it. The hardware path uses the new independent compositor needle.
2. **Keep the main HUD inside one monitor.** This is the arrangement that uses the monitor-sized presentation host. Include detached gauges and a HUD spanning monitor boundaries when describing your setup.
3. **Compare with the stock needle in the same focused scene.** Leave the usual resolution, frame cap, display mode and G-SYNC settings in place, and check again after several minutes. A steady scene and repeated revs make the difference easier to judge.
4. **Change one thing at a time if the problem remains.** A brief G-SYNC on/off comparison or focus change can help identify the symptom. Restore the original setting afterward and note what changed. Keep the rest of the display setup consistent.
5. **Export a fresh debug report after the problem happens.** Include the build, rendering mode, resolution and refresh rate, whether G-SYNC was enabled, which application was focused, and approximate times of any changes. A brief description of how Wisp compared with the stock needle is particularly useful.

Hardware-accelerated GPU scheduling performed better enabled on the machine used for this investigation. Keep a known-good baseline and judge settings changes individually. If other overlays are running, mention them in the report so their interaction can be tested deliberately.

## Validation

The private candidate passed 4,814 managed tests, 100 support-tool tests, native GPU and CPU/WARP checks, and real-window checks covering artwork, transparency, movement, locking, click-through and passive-update requests. Regression checks also exercise fresh RPM delivery while UI publication is stalled and the resets needed when car or session data changes.

On the affected setup, the combined candidate was substantially smoother with G-SYNC enabled. The matching debug capture covered about 86 seconds with Forza focused and confirmed the hardware rendering path. All 63,000 retained renderer records reported successful native operations, and the direct RPM path was observed receiving fresher input than the UI layout snapshot. The captured motion-update stream remained consistent through the later part of the recording.

[Download Wisp 2.4](https://github.com/Views2k/Wisp/releases/tag/v2.4.0)
