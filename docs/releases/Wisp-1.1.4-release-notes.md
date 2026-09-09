# Wisp 1.1.4

Addresses choppy Analogue tachometer motion while Forza has focus. The live
combustion Analogue HUD now uses Direct3D11 and DirectComposition on a dedicated
render thread, with the original gauge artwork, native needle angle and blur,
and existing playback timing.

- Fixes the Analogue tach freezing in the wrong position or clipping the speed
  readout after toggling the attached G-force meter.
- Corrects motion blur when the tachometer uses RPM because native needle data
  is unavailable, including when Forza stops updating its stock gauge.
- Adds needle-source and render-submission timing to local debug exports so
  remaining smoothness reports are easier to investigate.

The renderer change applies to the live combustion Analogue HUD. Digital and
electric HUDs and Appearance previews continue using WPF. No new setting is
required.

The installer is `Wisp-Setup-1.1.4.exe`. Install over your current Wisp version
to retain completed setup, HUD preferences, profiles, and tire calibration.
