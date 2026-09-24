# Wisp 2.0.1

This update adds a quick tour of Wisp 2.0 and addresses recurring interface pauses, uneven particle animation and dashboard clipping.

- An optional welcome banner offers a four-step tour of the drift gauge, Display mode, Runs and Appearance colors. Skip or dismiss it at any time, or replay it from Release Notes. It does not interrupt automatic game-triggered launches.
- Game-detection scans no longer pause the interface when the standard Forza process is not found. Unrelated window owners no longer trigger repeated module queries.
- Background and dashboard-rim particles handle small timing variations without discarding alternating animation updates. Animation work remains bounded.
- The dashboard top-speed label stays inside the oval in wide windows, including after returning from Display mode.

HUD artwork, saved settings, profiles, tire calibration and recorded runs are preserved. The bundled runtime remains .NET 8.0.31.
