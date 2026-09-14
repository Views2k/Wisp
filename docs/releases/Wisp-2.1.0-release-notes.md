# Wisp 2.1

Private installer candidate: optional power and torque gauges beside the speedometer.

- Enable **Power gauge** and **Torque gauge** independently in **Appearance → Gauges**.
- Native-style needles and ticks surround live BHP and torque readouts. Torque uses the existing Nm or lb-ft preference.
- Adjustable smoothing steadies the needles; numbers refresh ten times per second. Negative output is hidden by default, with a Show negative output option. Recorded telemetry keeps the original signed values.
- Attach power and torque to the speedometer or detach each one and move it in Edit HUD layout. Their positions are remembered separately.
- Customize each gauge line's start, middle and end colors, with optional matching numbers, using the same color editor as boost.
- Peak markers and numbers show the highest recorded output for the current car. **Reset peaks** clears them; changing cars starts fresh.
- **More options** provides gauge size and fixed scale limits saved separately for each car. **Set from this run** sets both ranges once using observed peaks plus 10%; subsequent readings do not move the scale. Reset peaks after retuning, then make a full-throttle pull before setting the range again.
- The number remains accurate beyond the selected scale even when the needle reaches its end. Before a car is detected, scale controls set the defaults for cars without a saved range.
- HUD profiles preserve visibility, attachment, smoothing, colors, size and the currently selected ranges. Existing installations keep their saved choices.
- Fresh installations start with Native Analogue and all gauges enabled. The four supplementary dials match in size, with power and torque closer to boost and tire temperature.

Includes the 2.0.1 interface hotfixes and optional quick tour. The bundled runtime remains .NET 8.0.31.

These gauges display the game's reported engine output. They do not calculate an optimal shift point or replace a controlled tuning comparison.
