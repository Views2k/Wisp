# Lap review

This describes the unreleased lap-review candidate. The public 2.5.2 installer does not include it.

Open **Runs → Lap review**. Enable **Save completed laps automatically** to retain detailed laps while Wisp is running. Capture uses the timing mode in the HUD's lap settings: Race / Rivals laps or Time Attack. It works with the lap overlays disabled. Manual run recording remains available; older recordings need position and lap telemetry to show a driven map.

Automatic capture uses the existing lap tracker's accepted completion boundary. Joining halfway through a lap, missing telemetry, rewinding, changing car or exceeding the recording limit prevents that interrupted lap from being saved as a complete capture. Completed saves are written to the existing local run library, with its storage limits and export/removal tools. Turn the option off to stop future captures. Pending completed saves finish when Wisp closes.

Choose a saved run and a lap. The map is the car's recorded path, not a supplied circuit outline or a claim about track limits. The start ring and shared cursor identify locations. Click the map or graph, use arrow keys, or move the sample slider. Choose speed, time delta, throttle, brake, steering input, G-force, RPM or gear as the channel. Tire temperature, slip ratio, raw slip angle and normalized suspension travel also have a wheel selector for the map and graph. The sample readout shows these values for all four wheels together.

Use **Start section here** and **End section here** around a corner or straight. Compare entry, minimum and exit speed, time, distance, throttle/brake/coasting fractions, G-force, RPM, gear changes and tire values. **Open section in graphs** connects the same interval to the existing Runs graph workspace. Whole-lap timing uses the recorded timing boundary; section statistics describe the actual recorded samples and their coverage.

Select Run B using the existing comparison controls, then choose its reference lap. Without Run B, the reference picker uses laps from the current run. **Pin this lap as benchmark** keeps a selected run/lap reference between Wisp sessions; rename the run and add its tune label in the normal run details. Pinning does not copy the recording, prevent its removal, or change the live HUD's session-best/previous-lap reference. If the pinned run is unavailable, choose another or unpin it.

Time delta is this lap's time minus the reference's time at matching world positions. A negative value is ahead. Route direction, continuity, start location, car and timing mode must agree, and most of both routes must match. Ambiguous or missing positions get no delta. Comparison does not establish identical tune, conditions or an officially clean lap. Existing telemetry does not provide a reliable official lap-validity flag or full track boundary.

## Why these measurements

RACELOGIC recommends locating time losses in the delta trace, then examining braking location, apex speed and the line through corner entry and exit. Wisp uses the corresponding telemetry and selected sections; it does not infer where a real apex or legal kerb lies from a bare path. [Circuit Tools guidance](https://en.racelogic.support/motorsport/discontinued-legacy/software/circuit-tools-2/kb/how-to-use-circuit-tools-to-become-faster/).

AiM's analysis workflow combines a track map with speed/RPM and other channels, synchronized cursors and time/distance analysis. That supports the shared location cursor and channel/section comparisons here. Braking and throttle traces explain how the speed trace changed; G-force and wheel channels provide context rather than a universal driving score. [RaceStudio 3 Analysis manual](https://www.aim-sportline.com/docs/racestudio3/manual/html/analysis.html).

Brake is the game's input percentage, not hydraulic pressure. Steering is an input, not steering-wheel degrees. Slip angle remains the raw game telemetry value because its physical-unit contract has not been established here. Tire temperature has no universal ideal threshold across cars and conditions. Coasting is measured, not automatically classified as a mistake. Missing channels do not become invented measurements or predicted lap-time gains.
