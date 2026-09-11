# Changelog

Notable changes to Wisp are recorded here.

## Unreleased

- Keep the main Native HUD stationary when toggling its attached G-force meter.
- Service the bundled .NET 8 runtime to 8.0.31 and build with SDK 8.0.425.

## 1.2.1 - 2026-09-10

- Add an optional **CPU rendering** toggle in Diagnostics for Analogue tachometer lag or hitching. GPU rendering remains the default. Choose **Exit Wisp** from the tray menu and reopen Wisp to apply the change.
- CPU mode can increase CPU usage. Other gauges keep their existing renderers, and Windows still uses the GPU to compose the overlay.
- Reuse unchanged dial-background pixels in CPU mode to reduce repeated drawing work while the needle and live readings continue updating.
- Separate queued telemetry publication time from consumption time so a sample queued during rendering cannot falsely trigger the needle's clock-rewind reset.
- Draw the Analogue HUD once per pending frame, then retry only Present while the queue is busy. Continue consuming telemetry; discard pending pixels when their layout or source becomes invalid, or after occlusion.
- Add bounded, nonblocking debug capture for frame waits, scene building, drawing, buffer mapping, presentation results and retry delays. Exports identify the active rendering mode and separate the native wait from its surrounding checks. These measure CPU-side operations, not displayed FPS.
- Preserve the existing artwork, needle interpolation, HUD settings and Record and Compare Runs features.

## 1.2.0 - 2026-09-09

- Record drives locally from Dashboard or Runs, with optional shortcuts, a start countdown, timed stops and markers for moments to review.
- Review measured findings, readable charts and selected-interval statistics inside Wisp. Open all eight graph views from one Show graphs button.
- Compare two saved runs over time or through a shared speed range, including starting-condition differences and capture gaps. Separate run colors and line styles carry through graphs, legends, markers and exported images.
- Inspect power and torque against RPM with gear and throttle filters, a G-force plot, or front/rear tire-temperature changes.
- Export a readable report image, raw telemetry CSV, or a Wisp recording that can be imported for comparison.
- Keep raw run capture and post-run analysis separate from the live HUD renderer and preserve existing HUD profiles.

## 1.1.4 - 2026-09-08

- Move the live combustion Analogue HUD to a dedicated Direct3D11 and DirectComposition renderer to address tachometer stutter while Forza is focused.
- Keep the original gauge artwork, native needle angle and blur, and existing playback timing. Digital and electric HUDs and Appearance previews continue using WPF.
- Keep the Analogue tach rendering through HUD resizes, fixing a stalled, misplaced or clipped speedometer after toggling the attached G-force meter.
- Correct RPM fallback motion blur to use FH6's combustion needle shutter calculation when native needle data is unavailable.
- Include telemetry, native needle source, and render-submission timing in local debug exports, grouped by logging period to help investigate remaining smoothness reports.

## 1.1.3 - 2026-09-08

- Add the reviewed native HUD map for Xbox app / Microsoft Store FH6 `3.440.853.0` on Windows PC, retaining Store `3.430.771.0` and the existing Steam maps.
- Retain the signed compatibility-map update path from 1.1.2, including exact Store identity checks, atomic installation, and offline reuse for reviewed maps within the supported reader.
- Add an optional **Show vacuum pressure** toggle in Appearance > Boost, using negative pressure reported by FH6.
- Let the existing boost-gauge visibility toggle show a stationary zero gauge on naturally aspirated cars. Electric vehicles never show it. Require positive boost before enabling pressure readings, so vacuum alone does not identify forced induction; after detection, the option follows negative telemetry at idle and cruise.
- Support vacuum in Digital and Analogue gauges, attached and detached layouts, and the Appearance preview in PSI or bar.
- Extend the enabled gauge range to -20 through 70 PSI or -1 through 5 bar, with a zero marker on the Digital rail.
- Keep the option off by default and save it with settings and HUD profiles. Existing settings and profiles leave vacuum disabled.
- Check for application updates every time Wisp opens and every 24 hours while it remains running, when automatic checks are enabled. A recent check from a previous launch no longer suppresses the startup check.
- Preserve an available-update banner if a later automatic refresh fails. Downloads and installation still require confirmation.

## 1.1.2 - 2026-09-07

- Add the native compatibility map for Steam FH6 `6.440.853.0`, retaining the previous Steam and Store maps.
- Enable signed compatibility-map updates with bounded multi-build bundles, atomic cache installation, offline reuse, and revision protection.
- Select future reviewed Store maps by their actual Windows package identity while preserving origin, path, image, and reader guards.

Future changes to the reader's supported native structures or semantics can still require an application update.

## 1.1.1 - 2026-09-06

### Added

- Native HUD support for the Xbox app and Microsoft Store Windows PC edition of FH6, build `3.430.771.0`. Steam build `6.430.771.0` retains its existing compatibility path.

### Fixed

- Validate the supported Store build using its Windows package identity and bounded loaded-image checks when the protected executable cannot be opened for a file hash.
- Recognize the supported Store executable's Windows path aliases only after confirming both paths identify the same file. No administrator access, permission changes, or game modifications are required.

HUD settings, profiles, tire calibration, and speed-smoothing behavior are unchanged from 1.1.

## 1.1.0 - 2026-09-06

Wisp 1.1 maintenance release.

### Fixed

- Remove expired local debug-log segments before export, including after logging has stopped. Existing exported ZIPs are not deleted.
- Confirm profile saves only after successful persistence, retaining retry support on failure.
- Include drivetrain and calibration revision when deciding whether calibration needs saving.
- Use monotonic elapsed time for telemetry freshness and retain release details on update retries.
- Honor speed smoothing during large wheel-speed changes, and prevent release-history version labels from clipping.

### Maintenance

- Bring update, retention, boost, and developer documentation into line with the application; clarify older screenshots and link current downloads.
- Organize detailed release notes and document shader regeneration.
- Match setup's particle material and depth styling to the website without low-opacity gradient banding.
- Accept shortened stable release versions while preserving numeric installer identity and verification.

## 1.0.12 - 2026-09-04

### Improved

- Local debug reports correlate telemetry reception and processing, dispatcher delay, native-data freshness, composition callbacks, focus transitions, and Wisp CPU/memory usage. Background collection continues when the UI stalls.
- Diagnostic summaries include observation times, supporting measurements, the likely affected component, uncertainty, and a next step. Menus, hidden overlays, and ordinary disconnection are not treated as rendering faults. Collection remains local, opt-in, and bounded.

### Fixed

- Recover native race providers whose secondary local-provider flag is cleared, while retaining validated contracts and a unique live car/RPM match.
- Remove the outer outline from the update confirmation dialog.

## 1.0.11 - 2026-09-04

### Maintenance

- Mapped the temporary build-author alias to Views2k for repository tools that support author aliases.
- Updated the application and installer version to 1.0.11. Application behavior is unchanged from 1.0.10.

## 1.0.10 - 2026-09-04

### Added

- Added smoothed live torque to the Wheel Speed Ready card, with Nm and lb-ft units.
- Added session top speed, peak power, and peak torque, with one reset action and automatic reset when the car changes.
- Added an update-available Dashboard banner and an optional startup check limited to once every 24 hours. Downloads and installation still require confirmation.
- Added a customizable global shortcut for showing or hiding the overlay.
- Added bounded local debug logging with 24-hour automatic expiry and a ZIP export intended for GitHub issue reports.
- Added one continuous color editor for the app accent, background surfaces, HUD border, three gauge-gradient colors, and traction hook cue.
- Added named HUD profiles for complete visual combinations without changing tire calibration, placement, telemetry, startup, update, or debug settings.
- Added in-app release notes and a direct GitHub star shortcut.

### Changed

- Update confirmation now shows the short summary supplied by the matching GitHub release before a download begins.
- Consolidated the previous color choices into a themed element list and large focused editor with saturation, brightness, opacity, direct wheel selection, and exact color input.

### Fixed

- Preserved detached boost and tire-temperature positions across restarts and updates, including secondary-display placements.
- Preserved the correct placement when a HUD profile changes layouts or switches between Native Digital and Native Analogue.
- Included the selected torque unit when saving and applying HUD profiles.
- Prevented expired native tachometer samples from interrupting the smooth RPM fallback during a reader stall.
- Kept color-wheel selection independent from slider adjustments and made every point in the wheel selectable.
- Kept very dark background colors visible and editable while maintaining readable surfaces.
- Restored the traction-loss hook cue across Native HUD styles and cleared stale slip evidence after stopping.
- Prevented simultaneous debug-log actions from waiting indefinitely.
- Prevented a failed telemetry-listener start from leaving UI callbacks running.
- Removed the duplicate profile-save action and simplified the profile confirmation dialog.
- Styled the local debug logging control consistently with the rest of Wisp.

## 1.0.8 - 2026-09-03

### Added

- Added a PSI or bar setting for boost pressure. The selected unit applies to Digital and Analogue gauges, attached and detached layouts, and the Appearance preview.

## 1.0.7 - 2026-09-03

### Fixed

- Decoupled live HUD telemetry delivery from WPF presentation callbacks so background compositor throttling cannot stall Wisp's HUD state.
- Smoothed and rate-limited the dashboard horsepower readout so rapidly changing power telemetry remains readable without altering raw power data.
- Fixed native tachometer source discovery across race/menu transitions so stale unrelated local-player HUD sources cannot invalidate the active car's tachometer state.
- Refreshed gallery image identities so browsers do not reuse stale 1.0.5 screenshots.

## 1.0.6 - 2026-09-02

### Fixed

- Fixed the speed-smoothing control being ignored by Native HUD layouts.

## 1.0.5 - 2026-09-02

### Added

- Added a boost gauge for confirmed turbocharged and supercharged cars. Native
  Digital uses a slim rail below the tachometer, and Native Analogue uses a
  0 to 70 PSI dial with 5 PSI ticks and a centered two-digit readout.
- Added independent PSI-number color controls, attached or detached Analogue
  placement, Analogue size control, fifteen boost palettes, and a stock
  no-color style.
- Added an independent Digital boost stock-material option that reuses the
  native tachometer's neutral fill and marker shader without changing the
  shared Analogue and tire palette.
- Added the boost gauge to the Appearance HUD preview so its layout, palette,
  readout color, and attachment state can be reviewed without running FH6.
- Added a tire-temperature gauge to both Native layouts. Digital uses one rail
  with separate front and rear markers. Analogue uses a compact dual-needle
  dial with exact front and rear readings.
- Added Fahrenheit and Celsius tire-temperature display, palette-aware solid
  distinct front/rear needle and marker colors, attachment, and size controls.
- Added a Native HUD attachment option for the G-force meter and included the
  fully attached arrangement in the HUD preview.

### Changed

- Grouped the longer Appearance page into focused sections.
- Restored forward and reverse gear state on electric Native HUD layouts.
- Extended the existing G-force motion trail by half a second.

### Fixed

- Made confirmed boost displays appear with the speedometer instead of waiting
  for sustained positive pressure.
- Kept boost gauges hidden for naturally aspirated and electric cars.
- Prevented attached Digital and Analogue boost displays from clipping their
  overlay windows or covering the stock tachometer.
- Corrected Digital rail spacing, connector alignment, native-style marker
  glow, contained color fill, label alignment, and PSI placement.
- Matched the Analogue PSI number to the needle's current color position.
- Clamped tire-temperature readouts and marker positions to the authored
  50 F to 350 F gauge range.
- Corrected the tire gauge size, label alignment, needle length, needle glow,
  and Digital one-rail composition.
- Removed the small Analogue tire-needle endpoint artifacts and held saturated
  tire markers at the 350 F endpoint without coloring or filling the rail.
- Prevented a transient Native tachometer source mismatch during a fast shift
  or rapid RPM bounce from blanking the stable tachometer texture.

## 1.0.4 - 2026-09-01

### Changed

- Reduced uploaded release files to one versioned installer and one installer
  archive. GitHub's generated source archives remain available.
- Kept application behavior unchanged from 1.0.3.

## 1.0.3 - 2026-09-01

### Added

- Added a short, connected trajectory trail to the standard and Native G-force
  meters. The trail keeps eight meaningful samples, fades and tapers older
  movement, ignores small positional jiggle, and clears when live telemetry stops.

### Changed

- Updated public issue, security-reporting, and release-validation guidance for
  public distribution.
- Aligned public version examples with the current release and used reserved
  example values for sample data.

## 1.0.2 - 2026-09-01

### Changed

- Made the setup backdrop motion visibly dynamic while preserving its grouped
  particle composition and lightweight WPF renderer.
- Kept setup animation running while the visible wizard is inactive, while
  still pausing when hidden, minimized, disabled, or reduced motion is active.
- Added a stable `Wisp-Setup.exe` release asset so the website can always link
  directly to the latest installer without a site update.

## 1.0.1 - 2026-08-31

### Added

- Added an independent HUD border palette for the Combined and Two boxes
  layouts.

### Changed

- Replaced the decorative diamond backdrop in the setup wizard and control
  center with a slower layered particle field.
- Added a CI-generated Appearance capture so public screenshots can be built
  from the reviewed application instead of a hand-edited mockup.

### Fixed

- Accepted the trailing NUL and space padding that Inno Setup 6.7 adds to
  version-resource strings while preserving exact product, description, and
  semantic-version checks.
