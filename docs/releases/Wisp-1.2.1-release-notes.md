# Wisp 1.2.1

September 10, 2026

Adds an optional CPU rendering mode for the Analogue tachometer, for PCs that
experience overlay lag or hitching.

1. Open **Diagnostics** and enable **CPU rendering**.
2. Right-click Wisp's tray icon and choose **Exit Wisp**.
3. Reopen Wisp to apply the change.

CPU mode reuses the unchanged dial background to reduce repeated drawing work,
while the needle and live readings continue updating.

GPU rendering remains the default. CPU mode can increase CPU usage. Other
gauges keep their existing renderers, and Windows still uses the GPU to compose
the overlay. The gauge artwork and needle smoothing are preserved.

Also included:

- Prevents telemetry queued during rendering from falsely resetting needle
  playback when the render thread catches up.
- Reuses the already drawn HUD when Windows is busy accepting a frame, avoiding
  repeated drawing work during presentation retries.
- Expands bounded local debug exports with the active rendering mode, detailed
  frame waits, drawing and presentation timings. These measurements help
  investigate hitching; they are not displayed frame rates.

The installer is `Wisp-Setup-1.2.1.exe`. Install over your current Wisp version
to keep your HUD settings, profiles, tire calibration and saved runs. Record
and Compare Runs remain available.
