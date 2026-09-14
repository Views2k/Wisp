# Wisp 2.1

Live power and torque gauges join the Wisp HUD, with separate placement, peak readings, and fixed ranges for each car.

- Enable **Power gauge** and **Torque gauge** independently in **Appearance → Gauges**.
- Native-style needles and ticks surround live BHP and torque readouts. Torque uses the existing Nm or lb-ft preference.
- Adjust **Gauge smoothing** under **More options**, from no smoothing to 1500 ms; the default is **250 ms**. Numeric readouts update at a slower cadence than the needles so they are easier to read. Negative output is hidden by default, with a **Show negative power and torque** option. Recorded telemetry keeps the original signed values.
- Attach power and torque to the speedometer or detach each one and move it in Edit HUD layout. Their positions are remembered separately.
- Set **Gauge start**, **Gauge middle**, and **Gauge end** in **Appearance → Colors** to share a palette across boost, tire temperature, power, and torque. Power and torque each have an optional matching number color.
- Peak markers and numbers show the highest output Wisp has observed for the current car since the last reset. **Reset peaks** clears them; changing cars starts fresh.
- **More options** provides gauge size and fixed scale limits saved separately for each car. **Set from this run** sets both ranges once using observed peaks plus 10%; subsequent readings do not move the scale. Reset peaks after retuning, then make a full-throttle pull before setting the range again.
- Numbers can show readings beyond the selected scale even when the needle reaches its end. Before a car is detected, scale controls set the defaults for cars without a saved range.
- HUD profiles preserve visibility, attachment, smoothing, colors, size and the currently selected ranges. Existing installations keep their saved choices.
- Fresh installations start with Native Analogue and all gauges enabled. The four supplementary dials match in size, with power and torque closer to boost and tire temperature.

Includes the 2.0.1 interface hotfixes and optional quick tour. The bundled runtime remains .NET 8.0.31.

The power and torque needles now retain fresh telemetry readings that share the same game timestamp, and advance between received samples. Playback stays within the available readings and resets after a data gap instead of extrapolating missing output.

These gauges display the game's reported engine output. **Set from this run** uses observed peaks, not a rated engine or tune maximum. The gauges do not calculate an optimal shift point or replace a controlled tuning comparison.

[See the Wisp 2.1 gauges in gameplay](../images/wisp-2.1-power-torque-gameplay.webp).
