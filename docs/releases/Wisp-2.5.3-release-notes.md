# Wisp 2.5.3

Private test candidate. This version has not been published as a public release.

- Clips saves recent gameplay locally, with selectable length, resolution, frame rate and quality; separate toggle/save shortcuts; a paged gallery; playback, export and dashboard reminders. Game-process audio is included when available. Full-recorder gameplay performance is still unverified.
- HUD profiles include driving settings and gauge positions; startup, connections, updates and diagnostics remain app-wide.
- Recorded laps can be reviewed on their driven path with linked graphs, section statistics and a saved reference. Automatic completed-lap saving is optional.
- Runs charts reuse static drawings while the cursor moves. Sample readings follow the app's typography, and empty lap selectors cannot open a blank popup.
- New automatic lap recordings save calibrated wheel data when available. Missing fields in existing recordings remain missing and are explained in the UI.
- Shift calibration keeps one guidance label, uses the normalized grip boundary, and accepts correctly timed telemetry when native data refreshes between receipt and processing. No first- or second-gear launch is required; the measured curve and confirming shift must still pass their checks.
- The confirmed shift-coverage hotfix is preserved: idle guidance stays stable, measured higher-gear targets can save without first-gear coverage, and unmeasured gears remain off.

Fixture and UI checks do not establish that every car will calibrate or that gameplay performance is unchanged. Live confirmation uses this candidate's explicit private build identity.
