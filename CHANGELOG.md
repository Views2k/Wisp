# Changelog

Notable changes to Wisp are recorded here.

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
