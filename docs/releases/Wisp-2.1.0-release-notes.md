# Wisp 2.1

Private installer candidate: optional power and torque gauges beside the speedometer.

- Enable **Power gauge** and **Torque gauge** independently in **Appearance → Gauges**.
- Native-style needles and ticks surround live BHP and torque readouts. Torque uses the existing Nm or lb-ft preference.
- Peak markers and numbers show the highest recorded output for the current car. **Reset peaks** clears them; changing cars starts fresh.
- **More options** provides gauge size and fixed scale limits saved separately for each car. **Set from this run** sets both ranges once using observed peaks plus 10%; subsequent readings do not move the scale. Reset peaks after retuning, then make a full-throttle pull before setting the range again.
- The number remains accurate beyond the selected scale even when the needle reaches its end. Before a car is detected, scale controls set the defaults for cars without a saved range.
- HUD profiles preserve visibility, size and the currently selected ranges. Existing installations keep both gauges off until enabled.

Includes the 2.0.1 interface hotfixes and optional quick tour. The bundled runtime remains .NET 8.0.31.

These gauges display the game's reported engine output. They do not calculate an optimal shift point or replace a controlled tuning comparison.
