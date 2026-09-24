# Wisp 2.2

Shared native rendering across the live HUD, a closer EV gauge layout, and fixes for the Appearance preview.

- The EV and Digital HUDs, supplementary gauges, G-force meter, and text layouts now use the native renderer already used by the Analogue speedometer. Artwork, colors, needle behavior, and smoothing controls are unchanged.
- G-force trails stay aligned when the display scale changes, and Wisp interpolates the dot's position between telemetry samples.
- Attached EV tire-temperature, power, and torque gauges now sit closer to the speedometer's right side. They are smaller by default, and you can still resize or detach each gauge.
- The live HUD and Appearance preview use the same attached EV gauge layout. Enabled detached gauges stay visible in the preview through car and tune changes.
- Saved settings, HUD profiles, tire calibration, and recorded runs are preserved.

[Wisp 2.1 feature notes](https://github.com/Views2k/Wisp/blob/v2.1.0/docs/releases/Wisp-2.1.0-release-notes.md).
