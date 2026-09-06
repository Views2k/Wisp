# Wisp 1.0.5

Wisp 1.0.5 adds boost and tire-temperature gauges to the Native Digital and
Native Analogue HUDs.

The boost PSI value stays absolute while its color progression scales to the
learned peak of the current car. Digital mode places a slim rail below the
tachometer. Analogue mode adds a 0 to 70 PSI dial with 5 PSI ticks and a
centered two-digit readout. Both layouts include independent PSI color
controls and fifteen palettes. Digital mode also has an independent stock
material option that matches the native tachometer's neutral fill and white
marker. Boost remains hidden unless forced induction is confirmed.

The tire-temperature gauge reports exact front and rear axle averages from
FH6 telemetry. Digital mode uses two marker lines in one neutral rail with no
colored fill. Analogue mode uses two short, solid-color needles in one compact
dial. The gauge scale runs from 50°F to 350°F, with numeric readings clamped to
the same ceiling. Fahrenheit and Celsius are both supported.

Native mode can now attach the G-force meter above the speedometer and side
gauges. Its existing motion trail also lasts half a second longer. The software
HUD preview shows the complete attached arrangement.

This update also restores the electric HUD gear indicator, reorganizes the
Appearance settings, and fixes the clipping, spacing, connector, readout,
needle, and transient tachometer problems found during 1.0.5 testing.

## Install

Download `Wisp-Setup-1.0.5.exe`, or download and extract
`Wisp-Setup-1.0.5.zip`. The installer is self-contained, installs for the
current Windows user, and does not require a separate .NET runtime.

The installer is not code-signed, so Windows may show an unknown-publisher
warning. Verify the SHA-256 file supplied with the release before running it.

Forza Horizon 6 build `6.430.771.0` remains the reviewed Steam build for Native
process-derived HUD data. Standard Data Out telemetry and dashboard values do
not depend on that compatibility contract.
