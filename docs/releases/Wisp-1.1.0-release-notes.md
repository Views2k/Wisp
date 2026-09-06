# Wisp 1.1

Wisp 1.1 improves wheel-speed readability and settings reliability, with a refreshed
setup background and more flexible release-version support.

- Profile changes are confirmed only after the settings write succeeds. If a
  write fails, the existing dialog offers a retry without adding another profile.
- Calibration save detection now includes drivetrain and calibration revision,
  even when tire radii remain unchanged.
- Windows clock changes no longer affect the telemetry connection timeout.
- Release details remain available when retrying a downloaded update.
- Setup uses the website's particle material and depth styling, with smoother
  faint gradients and a new particle distribution.
- Wheel-indicated speed smoothing now honors the selected amount during large
  wheel-speed changes, instead of staying within 1.5 mph of the raw reading.
- Includes the previously merged expired-log cleanup before debug export.
- Release-history version labels have room to display without clipping.
- Update checks recognize shortened version numbers and stable release labels.
  Installer hashes, sizes, embedded versions, and downgrade protection are still checked.

Wisp 1.1 uses the numeric Windows version 1.1.0. The release tag and installer
filename remain `v1.1.0` and `Wisp-Setup-1.1.0.exe` so existing installations
can discover this update. Newer clients also understand shortened release tags.

HUD layouts, colors, tire-learning rules, and the settings
format are unchanged. These changes do not address intermittent tachometer lag.
