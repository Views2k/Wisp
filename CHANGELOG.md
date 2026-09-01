# Changelog

Notable changes to Wisp are recorded here.

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
