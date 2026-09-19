# Wisp 2.2

Shared native rendering across the live HUD, a closer EV gauge layout, and fixes for the Appearance preview.

- Move the EV and Digital HUDs, supplementary gauges, G-force meter, and text layouts onto the native renderer already used by the Analogue speedometer. Preserve existing artwork, colors, needle behavior, and smoothing controls.
- Keep G-force trails aligned when the display scale changes, and interpolate the displayed dot between telemetry samples.
- Wrap attached EV tire-temperature, power, and torque gauges closely around the speedometer's right side. Use a smaller EV attachment baseline while retaining individual size controls and detached placement.
- Use the same EV attachment geometry in the live HUD and Appearance preview. Keep enabled detached gauges visible in the preview through car and tune changes.
- Preserve saved settings, HUD profiles, tire calibration, and recorded runs.

[Wisp 2.1 feature notes](https://github.com/Views2k/Wisp/blob/v2.1.0/docs/releases/Wisp-2.1.0-release-notes.md).
