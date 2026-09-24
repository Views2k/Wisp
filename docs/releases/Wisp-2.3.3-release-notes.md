# Wisp 2.3.3

Wisp 2.3.3 adds calibrated full-load shift guidance for supported combustion cars and fixes supplementary-gauge attachment and update recovery.

- Calibrate a car and tune with a rolling full-throttle pull and a confirming upshift. Wisp compares measured output in adjacent gears.
- Show green, yellow and flashing red stages around the gear number, with each color configurable. Missing RPM coverage or a changed configuration requires calibration before guidance is available.
- Identify an upper-range target separately when the measured curve does not show an acceleration crossover. Targets use your calibration; driver reaction, changing grip and boost recovery also influence shift timing.
- Attach power and torque only to Native Analogue. In Minimal, Combined, Box and Native Digital, move them independently with Edit HUD layout.
- Disable incompatible attachment controls while preserving the preference for a return to Native Analogue.
- Account for the separate G-force meter when placing supplementary gauges by default, and try a side placement when there is no clear space above.
- Give repair instructions when a copy is missing its update helper, and release failed update handoffs without closing Wisp.

The full installer includes the update helper and asks you to exit a running Wisp copy before installation. Existing settings, profiles, tire calibration and runs are preserved. The boost and tire-temperature preview fixes from 2.3.2 remain included.
