# Wisp 2.5.1

Lap delta (beta) fixes for Time Attack.

- **Attempts end when Forza ends them.** Stop for five seconds, or, once two laps agree on the circuit, stay off it or go the wrong way for five seconds, and the panel shows START A LAP until you next cross the start line. Before, the delta kept counting after the attempt had ended.
- **Reference laps survive a Wisp restart.** If Wisp restarts or updates while Forza keeps running, it keeps your session best and previous lap, as Forza keeps its Current Best. Changing car or timing mode, leaving the circuit, or Reset reference laps still starts afresh.
- **The panel shows which lap it compares against.** The reference lap's time appears next to SESSION BEST or PREVIOUS LAP.
- **Less drawing.** The lap delta panel and track map redraw only when what they show changes. Before, each redrew at the display's refresh rate, about 240 times a second on a 240 Hz monitor, although their content changes at most with each telemetry packet.

HUD profiles now also save the background particle color, the app border and text colors, and the drift gauge's visibility, size, dark mode and black background. Profiles saved before 2.5.1 leave these settings as they are when you apply them.
