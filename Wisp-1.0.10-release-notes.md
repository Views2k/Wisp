# Wisp 1.0.10

Save your HUD setups, choose your own colors, and track more of each drive.
Wisp 1.0.10 adds named HUD profiles, a rebuilt color editor, live torque and
session peaks, update notifications, and local troubleshooting tools.
These notes cover the changes since 1.0.8.

## Added

- Added smoothed live torque to the Wheel Speed Ready card, with Nm and lb-ft units.
- Added session top speed, peak power, and peak torque, with one reset action and automatic reset when the car changes.
- Added an update-available Dashboard banner and an optional startup check limited to once every 24 hours. Downloads and installation still require confirmation.
- Added a customizable global shortcut for showing or hiding the overlay.
- Added bounded local debug logging with 24-hour automatic expiry, seven-day retention, and an issue-ready ZIP export.
- Added one continuous color editor for the app accent, background surfaces, HUD border, three gauge-gradient colors, and traction hook cue.
- Added named HUD profiles that save complete visual combinations while keeping tire calibration, overlay positions, telemetry, startup, update, and debug settings separate.
- Profiles support Apply, Update, Rename, and Delete, with saving available from Appearance. Hotkey settings also stay separate from profiles.
- Added in-app release notes and a direct GitHub star shortcut.

## Changed

- Update confirmation now shows the short summary supplied by the matching GitHub release before a download begins.
- Consolidated color customization into a themed element list and large focused editor with saturation, brightness, opacity, direct wheel selection, and exact color input.
- Styled the local debug logging control to match the rest of Wisp.
- Ordered the sidebar as Dashboard, Appearance, Diagnostics, Profiles, Setup, Extras, and Release Notes.

## Fixed

- Preserved detached boost and tire-temperature positions across restarts and updates, including secondary-display placements.
- Preserved the correct placement when a HUD profile changes layouts or switches between Native Digital and Native Analogue.
- Included the selected torque unit when saving and applying HUD profiles.
- Prevented expired native tachometer samples from interrupting the smooth RPM fallback during a reader stall.
- Kept color-wheel selection independent from slider adjustments and made every point in the wheel selectable.
- Kept very dark background colors visible and editable while maintaining readable surfaces.
- Restored the traction-loss hook cue across Native HUD styles and cleared stale slip evidence after stopping.
- Prevented simultaneous debug-log actions from waiting indefinitely.
- Prevented a failed telemetry-listener start from leaving UI callbacks running.
- Removed the duplicate profile-save action and the outer border on its confirmation dialog.

## Install

Download `Wisp-Setup-1.0.10.exe`, or download and extract
`Wisp-Setup-1.0.10.zip`. The installer is self-contained, installs for the
current Windows user, and does not require a separate .NET runtime.

The installer is not code-signed, so Windows may show an unknown-publisher
warning. Compare the installer's SHA-256 hash with its digest shown in the
GitHub release assets before running it.
